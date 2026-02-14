using System.Diagnostics;
using AutoMapper;
using Erao.Core.DTOs.Chat;
using Erao.Core.Entities;
using Erao.Core.Enums;
using Erao.Core.Interfaces;

namespace Erao.Application.Services;

public interface IChatService
{
    Task<ChatResponse> ProcessMessageAsync(Guid userId, ChatRequest request);
}

public class ChatService : IChatService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IOllamaService _ollamaService;
    private readonly IDatabaseQueryService _databaseQueryService;
    private readonly IFileQueryService _fileQueryService;
    private readonly IEncryptionService _encryptionService;
    private readonly IMapper _mapper;

    public ChatService(
        IUnitOfWork unitOfWork,
        IOllamaService ollamaService,
        IDatabaseQueryService databaseQueryService,
        IFileQueryService fileQueryService,
        IEncryptionService encryptionService,
        IMapper mapper)
    {
        _unitOfWork = unitOfWork;
        _ollamaService = ollamaService;
        _databaseQueryService = databaseQueryService;
        _fileQueryService = fileQueryService;
        _encryptionService = encryptionService;
        _mapper = mapper;
    }

    public async Task<ChatResponse> ProcessMessageAsync(Guid userId, ChatRequest request)
    {
        var stopwatch = Stopwatch.StartNew();

        // Validate user query limit
        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        if (user == null)
        {
            throw new UnauthorizedAccessException("User not found");
        }

        // Reset billing cycle if needed
        if (DateTime.UtcNow >= user.BillingCycleReset)
        {
            user.QueriesUsedThisMonth = 0;
            user.BillingCycleReset = DateTime.UtcNow.AddMonths(1);
        }

        if (user.QueryLimitPerMonth != -1 && user.QueriesUsedThisMonth >= user.QueryLimitPerMonth)
        {
            throw new InvalidOperationException("Query limit reached for this billing cycle");
        }

        // Get conversation (without messages — loaded separately below)
        var conversation = await _unitOfWork.Conversations.GetWithConnectionsAsync(request.ConversationId);
        if (conversation == null || conversation.UserId != userId)
        {
            throw new InvalidOperationException("Conversation not found");
        }

        // Load only recent messages for AI context (not all messages)
        const int maxContextMessages = 20;
        var recentMessages = await _unitOfWork.Messages.GetRecentAsync(conversation.Id, maxContextMessages);

        // Auto-generate conversation title from first message if empty
        var isFirstMessage = string.IsNullOrEmpty(conversation.Title) || conversation.Title == "New Chat";
        if (isFirstMessage && recentMessages.Count == 0)
        {
            conversation.Title = GenerateTitle(request.Message);
            await _unitOfWork.Conversations.UpdateAsync(conversation);
        }

        // Save user message
        var userMessage = new Message
        {
            ConversationId = conversation.Id,
            Role = MessageRole.User,
            Content = request.Message
        };
        await _unitOfWork.Messages.AddAsync(userMessage);

        // Get schema context if database connection or file is set
        string? schemaContext = null;
        DatabaseConnection? dbConnection = null;
        FileDocument? fileDocument = null;

        if (conversation.DatabaseConnectionId.HasValue)
        {
            dbConnection = await _unitOfWork.DatabaseConnections.GetByIdAsync(conversation.DatabaseConnectionId.Value);
            if (dbConnection != null)
            {
                schemaContext = dbConnection.SchemaCache;

                if (string.IsNullOrEmpty(schemaContext))
                {
                    var host = _encryptionService.Decrypt(dbConnection.EncryptedHost);
                    var port = int.Parse(_encryptionService.Decrypt(dbConnection.EncryptedPort));
                    var database = _encryptionService.Decrypt(dbConnection.EncryptedDatabaseName);
                    var username = _encryptionService.Decrypt(dbConnection.EncryptedUsername);
                    var password = _encryptionService.Decrypt(dbConnection.EncryptedPassword);

                    schemaContext = await _databaseQueryService.GetSchemaAsync(
                        dbConnection.DatabaseType, host, port, database, username, password);

                    dbConnection.SchemaCache = schemaContext;
                    await _unitOfWork.DatabaseConnections.UpdateAsync(dbConnection);
                }
            }
        }
        else if (conversation.FileDocumentId.HasValue)
        {
            fileDocument = await _unitOfWork.FileDocuments.GetByIdAsync(conversation.FileDocumentId.Value);
            if (fileDocument != null)
            {
                // Build a SQLite-oriented schema description with sample data for the AI
                schemaContext = _fileQueryService.BuildSchemaDescription(
                    fileDocument.SchemaInfo ?? "[]", "data", fileDocument.RowCount, fileDocument.ParsedContent);
            }
        }

        // Build chat history from recent messages - include query results for context
        // Use [DATA_CONTEXT] tags so AI understands this is reference info, not text to repeat
        var history = recentMessages
            .Select(m => {
                var role = m.Role == MessageRole.User ? "user" : "assistant";
                var content = m.Content;

                // For assistant messages, append query results as context for follow-up questions
                // Format as hidden context that AI should reference but NOT repeat in responses
                if (m.Role == MessageRole.Assistant && !string.IsNullOrEmpty(m.QueryResult))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(m.QueryResult);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("rows", out var rows) && rows.GetArrayLength() > 0)
                        {
                            var rowCount = rows.GetArrayLength();
                            if (rowCount <= 5)
                            {
                                // Small result set - include actual data for context
                                var dataLines = new List<string>();
                                foreach (var row in rows.EnumerateArray())
                                {
                                    var vals = new List<string>();
                                    foreach (var prop in row.EnumerateObject())
                                    {
                                        vals.Add($"{prop.Name}={prop.Value}");
                                    }
                                    dataLines.Add(string.Join(", ", vals));
                                }
                                content += $"\n[DATA_CONTEXT: {rowCount} row(s): {string.Join(" | ", dataLines)}]";
                            }
                            else
                            {
                                // Large result set - just note the count
                                content += $"\n[DATA_CONTEXT: Query returned {rowCount} rows]";
                            }
                        }
                    }
                    catch
                    {
                        // If JSON parsing fails, skip adding result context
                    }
                }

                return (role, content);
            })
            .ToList();

        // Build system prompt - different for database vs file
        var systemPrompt = fileDocument != null
            ? BuildFileSystemPrompt(schemaContext, fileDocument.OriginalFileName, fileDocument.RowCount)
            : BuildSystemPrompt(schemaContext, dbConnection?.DatabaseType);

        // Get AI response with full conversation history
        var (aiResponse, tokensUsed) = await _ollamaService.ChatAsync(request.Message, history, systemPrompt);

        // Try to extract SQL from response (for database mode) or analyze file data
        string? sqlQuery = null;
        string? queryResult = null;

        // Decrypt credentials once if needed for DB queries
        string? dbHost = null, dbDatabase = null, dbUsername = null, dbPassword = null;
        int dbPort = 0;
        if (dbConnection != null && request.ExecuteQuery)
        {
            dbHost = _encryptionService.Decrypt(dbConnection.EncryptedHost);
            dbPort = int.Parse(_encryptionService.Decrypt(dbConnection.EncryptedPort));
            dbDatabase = _encryptionService.Decrypt(dbConnection.EncryptedDatabaseName);
            dbUsername = _encryptionService.Decrypt(dbConnection.EncryptedUsername);
            dbPassword = _encryptionService.Decrypt(dbConnection.EncryptedPassword);
        }

        if (dbConnection != null && request.ExecuteQuery)
        {
            var sqlQueries = ExtractAllSqlFromResponse(aiResponse);

            if (sqlQueries.Count > 0)
            {
                sqlQuery = string.Join("\n\n-- Next Query --\n\n", sqlQueries);

                // Try executing with auto-retry on failure
                const int maxRetries = 2;
                for (var attempt = 0; attempt <= maxRetries; attempt++)
                {
                    try
                    {
                        if (sqlQueries.Count == 1)
                        {
                            queryResult = await _databaseQueryService.ExecuteQueryAsync(
                                dbConnection.DatabaseType, dbHost!, dbPort, dbDatabase!, dbUsername!, dbPassword!, sqlQueries[0]);
                        }
                        else
                        {
                            var results = await _databaseQueryService.ExecuteQueriesAsync(
                                dbConnection.DatabaseType, dbHost!, dbPort, dbDatabase!, dbUsername!, dbPassword!, sqlQueries);
                            var allResults = new List<object>();
                            foreach (var result in results)
                            {
                                if (!string.IsNullOrEmpty(result))
                                {
                                    var parsed = System.Text.Json.JsonSerializer.Deserialize<object>(result);
                                    allResults.Add(parsed!);
                                }
                            }
                            queryResult = System.Text.Json.JsonSerializer.Serialize(new { tables = allResults });
                        }
                        break; // Success — exit retry loop
                    }
                    catch (Exception ex)
                    {
                        if (attempt < maxRetries)
                        {
                            // Ask AI to fix the SQL
                            var failedSql = string.Join("\n\n", sqlQueries);
                            var retryPrompt = BuildSqlRetryPrompt(failedSql, ex.Message, schemaContext, dbConnection.DatabaseType);
                            var (retryResponse, retryTokens) = await _ollamaService.ChatAsync(
                                $"Fix this SQL error: {ex.Message}", new List<(string role, string content)>(), retryPrompt);
                            tokensUsed += retryTokens;

                            var retrySqlQueries = ExtractAllSqlFromResponse(retryResponse);
                            if (retrySqlQueries.Count > 0)
                            {
                                sqlQueries = retrySqlQueries;
                                sqlQuery = string.Join("\n\n-- Next Query --\n\n", sqlQueries);
                                // Also update the AI response so viz hints and cleaned content use the retry
                                aiResponse = retryResponse;
                            }
                            else
                            {
                                // AI didn't return SQL in retry — give up
                                queryResult = $"Error executing query: {ex.Message}";
                                break;
                            }
                        }
                        else
                        {
                            queryResult = $"Error executing query: {ex.Message}";
                        }
                    }
                }
            }
            else
            {
                queryResult = ExtractDataContextAsResult(aiResponse);
            }
        }
        else if (fileDocument != null && request.ExecuteQuery)
        {
            // File mode: extract SQL from AI response and execute against in-memory SQLite
            var sqlQueries = ExtractAllSqlFromResponse(aiResponse);

            if (sqlQueries.Count > 0 && !string.IsNullOrEmpty(fileDocument.ParsedContent) && !string.IsNullOrEmpty(fileDocument.SchemaInfo))
            {
                sqlQuery = string.Join("\n\n-- Next Query --\n\n", sqlQueries);

                // Try executing with auto-retry on failure
                const int maxRetries = 2;
                for (var attempt = 0; attempt <= maxRetries; attempt++)
                {
                    try
                    {
                        if (sqlQueries.Count == 1)
                        {
                            queryResult = await _fileQueryService.ExecuteQueryAsync(
                                fileDocument.ParsedContent, fileDocument.SchemaInfo, sqlQueries[0]);
                        }
                        else
                        {
                            var results = await _fileQueryService.ExecuteQueriesAsync(
                                fileDocument.ParsedContent, fileDocument.SchemaInfo, sqlQueries);
                            var allResults = new List<object>();
                            foreach (var result in results)
                            {
                                if (!string.IsNullOrEmpty(result))
                                {
                                    var parsed = System.Text.Json.JsonSerializer.Deserialize<object>(result);
                                    allResults.Add(parsed!);
                                }
                            }
                            queryResult = System.Text.Json.JsonSerializer.Serialize(new { tables = allResults });
                        }
                        break; // Success — exit retry loop
                    }
                    catch (Exception ex)
                    {
                        if (attempt < maxRetries)
                        {
                            // Ask AI to fix the SQL
                            var failedSql = string.Join("\n\n", sqlQueries);
                            var retryPrompt = BuildSqlRetryPrompt(failedSql, ex.Message, schemaContext, null, true);
                            var (retryResponse, retryTokens) = await _ollamaService.ChatAsync(
                                $"Fix this SQL error: {ex.Message}", new List<(string role, string content)>(), retryPrompt);
                            tokensUsed += retryTokens;

                            var retrySqlQueries = ExtractAllSqlFromResponse(retryResponse);
                            if (retrySqlQueries.Count > 0)
                            {
                                sqlQueries = retrySqlQueries;
                                sqlQuery = string.Join("\n\n-- Next Query --\n\n", sqlQueries);
                                aiResponse = retryResponse;
                            }
                            else
                            {
                                queryResult = $"Error executing query: {ex.Message}";
                                break;
                            }
                        }
                        else
                        {
                            queryResult = $"Error executing query: {ex.Message}";
                        }
                    }
                }
            }
            else
            {
                // Fallback: extract JSON data blocks from AI response
                queryResult = ExtractJsonBlock(aiResponse);

                if (string.IsNullOrEmpty(queryResult))
                {
                    queryResult = ExtractDataContextAsResult(aiResponse);
                }
            }
        }

        // Clean the response - remove code blocks that are now in queryResult
        var cleanedContent = StripCodeBlocks(aiResponse);

        // If the AI only returned SQL with no text, keep content empty — the table is the response
        if (string.IsNullOrWhiteSpace(cleanedContent) && !string.IsNullOrEmpty(queryResult))
        {
            cleanedContent = "";
        }

        // Save assistant message
        var assistantMessage = new Message
        {
            ConversationId = conversation.Id,
            Role = MessageRole.Assistant,
            Content = cleanedContent,
            SqlQuery = sqlQuery,
            QueryResult = queryResult,
            TokensUsed = tokensUsed
        };
        await _unitOfWork.Messages.AddAsync(assistantMessage);

        // Update user query count
        user.QueriesUsedThisMonth++;
        await _unitOfWork.Users.UpdateAsync(user);

        // Log usage
        stopwatch.Stop();
        var usageLog = new UsageLog
        {
            UserId = userId,
            DatabaseConnectionId = dbConnection?.Id,
            QueryType = sqlQuery != null ? "SQL" : "Chat",
            TokensUsed = tokensUsed,
            ExecutionTimeMs = (int)stopwatch.ElapsedMilliseconds
        };
        await _unitOfWork.UsageLogs.AddAsync(usageLog);

        await _unitOfWork.SaveChangesAsync();

        var assistantDto = _mapper.Map<MessageDto>(assistantMessage);

        // Extract visualization hint from AI response (if present)
        var visualizationHint = ExtractVisualizationHint(aiResponse);

        return new ChatResponse
        {
            UserMessage = _mapper.Map<MessageDto>(userMessage),
            AssistantMessage = assistantDto,
            QueryResult = queryResult,
            TokensUsed = tokensUsed,
            VisualizationHint = visualizationHint
        };
    }

    private static string BuildSystemPrompt(string? schemaContext, DatabaseType? dbType = null)
    {
        var dialect = dbType switch
        {
            DatabaseType.PostgreSQL => "PostgreSQL",
            DatabaseType.MySQL => "MySQL",
            DatabaseType.SQLServer => "SQL Server",
            DatabaseType.MongoDB => "MongoDB",
            DatabaseType.Oracle => "Oracle",
            DatabaseType.SQLite => "SQLite",
            DatabaseType.MariaDB => "MariaDB",
            DatabaseType.CockroachDB => "CockroachDB",
            DatabaseType.Redshift => "Amazon Redshift",
            DatabaseType.ClickHouse => "ClickHouse",
            DatabaseType.Firebird => "Firebird",
            DatabaseType.DuckDB => "DuckDB",
            DatabaseType.TimescaleDB => "TimescaleDB",
            DatabaseType.YugabyteDB => "YugabyteDB",
            DatabaseType.Snowflake => "Snowflake",
            _ => "PostgreSQL"
        };

        var quoteStyle = dbType switch
        {
            DatabaseType.MySQL or DatabaseType.MariaDB
                => "backtick-quote identifiers: `TableName`, `ColumnName`",
            DatabaseType.SQLServer
                => "bracket-quote identifiers: [TableName], [ColumnName]",
            DatabaseType.ClickHouse
                => "backtick-quote identifiers: `TableName`, `ColumnName`",
            _ => "double-quote identifiers: \"TableName\", \"ColumnName\""
        };

        var dateFunc = dbType switch
        {
            DatabaseType.MySQL or DatabaseType.MariaDB => "NOW(), CURDATE()",
            DatabaseType.SQLServer => "GETDATE(), CURRENT_TIMESTAMP",
            DatabaseType.Oracle => "SYSDATE, CURRENT_TIMESTAMP",
            DatabaseType.SQLite or DatabaseType.DuckDB => "DATE('now'), DATETIME('now')",
            DatabaseType.ClickHouse => "now(), today()",
            DatabaseType.Firebird => "CURRENT_TIMESTAMP, CURRENT_DATE",
            DatabaseType.Snowflake => "CURRENT_TIMESTAMP(), CURRENT_DATE()",
            _ => "NOW(), CURRENT_DATE"
        };

        var dialectNotes = dbType switch
        {
            DatabaseType.PostgreSQL or DatabaseType.CockroachDB or DatabaseType.TimescaleDB
                or DatabaseType.YugabyteDB => @"
PostgreSQL-specific rules (CRITICAL — violating these causes runtime errors):
- ROUND(double precision, N) does NOT exist. You MUST cast to numeric first: ROUND(value::numeric, N). This applies to ANY expression with floating point results (divisions, AVG, PERCENT_RANK, etc.).
- String concatenation uses || operator, not CONCAT (though CONCAT also works).
- Boolean values: use TRUE/FALSE, not 1/0.
- ILIKE for case-insensitive LIKE.",
            DatabaseType.Redshift => @"
Amazon Redshift-specific rules (PostgreSQL-based):
- ROUND(double precision, N) does NOT exist. You MUST cast to numeric first: ROUND(value::numeric, N).
- String concatenation uses || operator.
- Boolean values: use TRUE/FALSE, not 1/0.
- ILIKE for case-insensitive LIKE.
- No LATERAL joins. Use window functions instead of correlated subqueries where possible.",
            DatabaseType.MySQL or DatabaseType.MariaDB => @"
MySQL-specific rules:
- Use ROUND(value, N) directly — works on all numeric types.
- Use IFNULL() instead of COALESCE if only 2 args.
- String comparison is case-insensitive by default.",
            DatabaseType.SQLServer => @"
SQL Server-specific rules:
- Use ROUND(value, N) directly — works on all numeric types.
- Use TOP N instead of LIMIT N: SELECT TOP 20 ... ORDER BY ...
- Use ISNULL() or COALESCE() for null handling.
- No BOOLEAN type — use BIT (1/0).",
            DatabaseType.Oracle => @"
Oracle-specific rules:
- Use ROUND(value, N) directly.
- Use FETCH FIRST N ROWS ONLY instead of LIMIT N (Oracle 12c+). Example: SELECT ... ORDER BY x FETCH FIRST 20 ROWS ONLY.
- Use NVL() or COALESCE() for null handling.
- String concatenation uses || operator.
- No BOOLEAN type in SQL — use NUMBER(1) with 0/1.",
            DatabaseType.SQLite => @"
SQLite-specific rules:
- Double-quote all identifiers: ""TableName"", ""ColumnName"".
- Use ROUND(value, N) directly.
- Date functions: DATE('now'), STRFTIME(), JULIANDAY().
- No RIGHT JOIN or FULL OUTER JOIN — rewrite using LEFT JOIN.",
            DatabaseType.ClickHouse => @"
ClickHouse-specific rules:
- Use ROUND(value, N) directly.
- Use LIMIT N (standard SQL syntax).
- Use ifNull() or coalesce() for null handling.
- String functions: lower(), upper(), like (case-sensitive), ilike (case-insensitive).
- ClickHouse is columnar — avoid SELECT * on large tables.",
            DatabaseType.Firebird => @"
Firebird-specific rules:
- Use ROUND(value, N) directly.
- Use FIRST N or ROWS N instead of LIMIT N: SELECT FIRST 20 ... FROM ...
- String concatenation uses || operator.
- Use COALESCE() for null handling.",
            DatabaseType.DuckDB => @"
DuckDB-specific rules:
- PostgreSQL-compatible syntax. Double-quote identifiers.
- Use ROUND(value, N) directly — works on all numeric types.
- ILIKE for case-insensitive matching.
- String concatenation uses || operator.
- Supports LIMIT N.",
            DatabaseType.Snowflake => @"
Snowflake-specific rules:
- Use ROUND(value, N) directly.
- Use LIMIT N (standard SQL syntax).
- Identifiers are case-insensitive by default; use double-quotes to preserve case.
- Use NVL() or COALESCE() for null handling.
- ILIKE for case-insensitive matching.
- String concatenation uses || operator.",
            _ => ""
        };

        var prompt = $@"You are Erao, a data analyst. The user's {dialect} database is connected. Answer ONLY about this database. If a table matches the question, query it. Refuse only if NO table matches.

Write fresh SQL for EVERY request. Never reuse [DATA_CONTEXT] tags. Never ask clarifying questions — just query.

## INTENT → FORMAT

DATA (numbers, rankings, charts) → ```sql block + ```viz block. For complex/abstract questions, add 1-2 sentences before explaining your scoring approach.
SHOW SQL (""show the query"") → explain logic + ```text block (NOT ```sql). Add ```sql + ```viz only if they also want results.
EXPLANATION (""describe"", ""what is"") → **Bold** summary. Bullets. No SQL. No filler. No emojis.
OFF-TOPIC → one sentence decline.

## SQL TEMPLATE — COPY THIS STRUCTURE FOR RANKINGS

{dialect} dialect. {quoteStyle}. {dateFunc} for dates. SELECT only. JOIN to resolve IDs.
{dialectNotes}
You CANNOT use an alias in the same SELECT that defines it. Always use separate CTEs.

-- CTE 1: CLEAN. Keep ALL rows. Convert junk to NULL, numbers to REAL, booleans to 1/0.
-- NEVER use WHERE to remove rows with 'Not Mentioned'/'N/A'/NULL. They still compete.
WITH clean AS (
  SELECT *,
    CASE WHEN ""Revenue"" IS NOT NULL AND ""Revenue"" NOT IN ('Not Mentioned','N/A','','-','null')
         THEN CAST(REPLACE(""Revenue"", ',', '') AS REAL) ELSE NULL END AS revenue_num,
    CASE WHEN ""Cost"" IS NOT NULL AND ""Cost"" NOT IN ('Not Mentioned','N/A','','-','null')
         THEN CAST(REPLACE(""Cost"", ',', '') AS REAL) ELSE NULL END AS cost_num,
    CASE WHEN UPPER(""HasMVP"") = 'YES' THEN 1 ELSE 0 END AS mvp_flag
  FROM ""TableName""
),
-- CTE 2: SCORE. Think: what makes this ""good""? Use ALL relevant columns.
-- DESC = higher is better (revenue, MRR). ASC = lower is better (cost, risk).
-- Missing data: YOU DECIDE the COALESCE value. 0 = neutral, negative = penalty, positive = bonus.
-- Example: no MRR → probably no revenue → COALESCE(..., -0.1). No cost data → unknown → COALESCE(..., 0).
scored AS (
  SELECT *,
    COALESCE(PERCENT_RANK() OVER (ORDER BY revenue_num DESC), -0.1) * 0.30
    + COALESCE(PERCENT_RANK() OVER (ORDER BY cost_num ASC), 0) * 0.20
    + COALESCE(PERCENT_RANK() OVER (ORDER BY revenue_num / NULLIF(cost_num, 0) DESC), 0) * 0.15
    + mvp_flag * 0.20
    AS score
  FROM clean
)
-- FINAL: Include real data columns so user sees WHY. Alias clearly.
SELECT ""Name"", ""Sector"", ""Revenue"", ""Cost"", ""HasMVP"",
  ROUND(COALESCE(score, 0), 2) AS ""Score""
FROM scored ORDER BY score DESC LIMIT 20;

## SCORING RULES

- Use ALL relevant numeric columns, not just 1-2. Abstract concepts (""best"", ""top"", ""at-risk"") ALWAYS need multiple columns.
- PERCENT_RANK(ORDER BY col DESC) = higher is better. PERCENT_RANK(ORDER BY col ASC) = lower is better.
- Include ratio columns when both input/output exist (e.g., revenue/cost, MRR/investment).
- Booleans (YES/NO): convert to 1/0, weight directly into score.
- NEVER hardcode CASE WHEN for unknown category values. Use DENSE_RANK() or exclude from score.
- NULLIF(x, 0) in EVERY division. COALESCE every score.
- Rankings: ORDER BY score DESC, LIMIT 20 (unless user says otherwise). GROUP BY queries: no LIMIT.
- Final SELECT: always include name + key data columns + score. Never just name + score.
- ""revenue"" might mean SUM(amount/price/total). ""my""/""our"" = all data. Enums as numbers.
- Simple queries (COUNT, SUM, basic filter): just write direct SQL, no scoring template needed.

## VIZ BLOCK — after every ```sql

{{""chart"":""bar"",""group"":""Name"",""values"":[{{""col"":""Score"",""agg"":""NONE""}}]}}

chart: ""bar"" default | ""line"" time-series | ""pie"" 2-8 categories | ""table"" lists only
group: most readable column (name > ID). values: ONLY final metric, not sub-scores. agg: ""NONE"" if SQL already computed.
";

        if (!string.IsNullOrEmpty(schemaContext))
        {
            prompt += $@"
## Schema (ONLY these tables/columns exist)
Use EXACT names, properly quoted. Never invent tables or columns.

{schemaContext}";
        }
        else
        {
            prompt += @"

No schema available. Tell the user to connect a database first.";
        }

        return prompt;
    }

    private static string BuildSqlRetryPrompt(string failedSql, string errorMessage, string? schemaContext, DatabaseType? dbType, bool isFile = false)
    {
        var dialect = isFile ? "SQLite" : dbType switch
        {
            DatabaseType.PostgreSQL => "PostgreSQL",
            DatabaseType.MySQL => "MySQL",
            DatabaseType.SQLServer => "SQL Server",
            DatabaseType.Oracle => "Oracle",
            DatabaseType.SQLite => "SQLite",
            DatabaseType.MariaDB => "MariaDB",
            DatabaseType.CockroachDB => "CockroachDB",
            DatabaseType.Redshift => "Amazon Redshift",
            DatabaseType.ClickHouse => "ClickHouse",
            DatabaseType.Firebird => "Firebird",
            DatabaseType.DuckDB => "DuckDB",
            DatabaseType.TimescaleDB => "TimescaleDB",
            DatabaseType.YugabyteDB => "YugabyteDB",
            DatabaseType.Snowflake => "Snowflake",
            _ => "PostgreSQL"
        };

        var prompt = $@"You are a SQL expert. A {dialect} query failed with an error. Fix the SQL and return ONLY the corrected query in a ```sql code block. No explanation needed.

FAILED SQL:
```sql
{failedSql}
```

ERROR:
{errorMessage}

RULES:
- Return ONLY the fixed SQL in a ```sql code block.
- Keep the same intent/logic — just fix the syntax or dialect issue.
- SELECT queries only.
- Also include the original ```viz block if the query had visualization intent.";

        if (isFile)
        {
            prompt += @"
- SQLite dialect. Table is ""data"". Double-quote all identifiers.";
        }
        else
        {
            var dialectHints = dbType switch
            {
                DatabaseType.PostgreSQL or DatabaseType.CockroachDB or DatabaseType.TimescaleDB
                    or DatabaseType.YugabyteDB => @"
- PostgreSQL: ROUND() requires numeric type — use ROUND(value::numeric, N).
- Use double-quote identifiers: ""TableName"".
- ILIKE for case-insensitive matching.
- Boolean: TRUE/FALSE not 1/0.
- String concat: || operator.",
                DatabaseType.Redshift => @"
- Redshift (PostgreSQL-based): ROUND() requires numeric type — use ROUND(value::numeric, N).
- Use double-quote identifiers: ""TableName"".
- ILIKE for case-insensitive matching.
- No LATERAL joins.",
                DatabaseType.MySQL or DatabaseType.MariaDB => @"
- MySQL: backtick identifiers: `TableName`.
- ROUND(value, N) works directly.
- IFNULL() for 2-arg null handling.",
                DatabaseType.SQLServer => @"
- SQL Server: bracket identifiers: [TableName].
- TOP N instead of LIMIT N.
- BIT type instead of BOOLEAN.",
                DatabaseType.Oracle => @"
- Oracle: double-quote identifiers: ""TableName"".
- FETCH FIRST N ROWS ONLY instead of LIMIT N.
- NVL() for null handling. String concat: || operator.",
                DatabaseType.SQLite => @"
- SQLite: double-quote identifiers: ""TableName"".
- No RIGHT/FULL OUTER JOIN. Use DATE('now') for dates.",
                DatabaseType.ClickHouse => @"
- ClickHouse: backtick identifiers: `TableName`.
- ifNull() for null handling. Columnar engine.",
                DatabaseType.Firebird => @"
- Firebird: double-quote identifiers: ""TableName"".
- FIRST N instead of LIMIT N. String concat: || operator.",
                DatabaseType.DuckDB => @"
- DuckDB: double-quote identifiers: ""TableName"".
- PostgreSQL-compatible syntax. ILIKE for case-insensitive.",
                DatabaseType.Snowflake => @"
- Snowflake: double-quote identifiers for case-sensitive.
- NVL() or COALESCE(). ILIKE for case-insensitive.",
                _ => ""
            };
            prompt += dialectHints;
        }

        if (!string.IsNullOrEmpty(schemaContext))
        {
            prompt += $@"

SCHEMA:
{schemaContext}";
        }

        return prompt;
    }

    private static string? ExtractSqlFromResponse(string response)
    {
        // Try to extract SQL from markdown code blocks - multiple patterns
        var patterns = new[]
        {
            ("```sql", "```"),      // Standard SQL block
            ("```SQL", "```"),      // Uppercase
            ("```\nSELECT", "```"), // Generic block starting with SELECT
            ("```\nWITH", "```"),   // Generic block starting with WITH
        };

        foreach (var (startMarker, endMarker) in patterns)
        {
            var startIndex = response.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
            if (startIndex >= 0)
            {
                // Skip past the marker but keep SELECT/WITH if that's what we matched
                var offset = startMarker.StartsWith("```\n") ? 4 : startMarker.Length; // Skip ``` and newline
                startIndex += offset;

                var endIndex = response.IndexOf(endMarker, startIndex, StringComparison.OrdinalIgnoreCase);
                if (endIndex > startIndex)
                {
                    var sql = response.Substring(startIndex, endIndex - startIndex).Trim();
                    if (IsSafeQuery(sql))
                    {
                        return sql;
                    }
                }
            }
        }

        // Fallback: Try to find raw SELECT or WITH statements if no code block found
        var lines = response.Split('\n');
        var sqlLines = new List<string>();
        var inSql = false;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            var upperLine = trimmedLine.ToUpperInvariant();

            // Start capturing when we see SELECT or WITH at the start of a line
            if (!inSql && (upperLine.StartsWith("SELECT ") || upperLine.StartsWith("WITH ")))
            {
                inSql = true;
            }

            if (inSql)
            {
                // Stop if we hit an empty line or text that doesn't look like SQL
                if (string.IsNullOrWhiteSpace(trimmedLine))
                {
                    break;
                }
                // Stop if line starts with common non-SQL patterns
                if (trimmedLine.StartsWith("This ") || trimmedLine.StartsWith("The ") ||
                    trimmedLine.StartsWith("I ") || trimmedLine.StartsWith("Here"))
                {
                    break;
                }
                sqlLines.Add(trimmedLine);
            }
        }

        if (sqlLines.Count > 0)
        {
            var sql = string.Join("\n", sqlLines);
            if (IsSafeQuery(sql))
            {
                return sql;
            }
        }

        return null;
    }

    private static List<string> ExtractAllSqlFromResponse(string response)
    {
        var queries = new List<string>();
        var searchStart = 0;

        // Find all SQL code blocks
        while (searchStart < response.Length)
        {
            var sqlStart = response.IndexOf("```sql", searchStart, StringComparison.OrdinalIgnoreCase);
            if (sqlStart == -1)
            {
                // Try generic code block with SQL
                sqlStart = response.IndexOf("```\nSELECT", searchStart, StringComparison.OrdinalIgnoreCase);
                if (sqlStart == -1)
                {
                    sqlStart = response.IndexOf("```\nWITH", searchStart, StringComparison.OrdinalIgnoreCase);
                }
            }

            if (sqlStart == -1) break;

            // Find where the code block content starts
            var contentStart = response.IndexOf('\n', sqlStart);
            if (contentStart == -1) break;
            contentStart++; // Skip the newline

            // Find the closing ```
            var sqlEnd = response.IndexOf("```", contentStart);
            if (sqlEnd == -1) break;

            var sql = response.Substring(contentStart, sqlEnd - contentStart).Trim();
            if (!string.IsNullOrEmpty(sql) && IsSafeQuery(sql))
            {
                queries.Add(sql);
            }

            searchStart = sqlEnd + 3;
        }

        // If no code blocks found, try the single query fallback
        if (queries.Count == 0)
        {
            var fallback = ExtractSqlFromResponse(response);
            if (!string.IsNullOrEmpty(fallback))
            {
                queries.Add(fallback);
            }
        }

        return queries;
    }

    private static bool IsSafeQuery(string sql)
    {
        var upperSql = sql.ToUpperInvariant();

        // Use word-boundary matching so column names like "CreatedAt", "UpdatedAt", "DeletedAt" don't trigger false positives
        var dangerousPatterns = new[]
        {
            @"\bDROP\b", @"\bDELETE\s+FROM\b", @"\bTRUNCATE\b", @"\bALTER\b",
            @"\bCREATE\b", @"\bINSERT\b", @"\bUPDATE\s+\S+\s+SET\b",
            @"\bEXEC\b", @"\bEXECUTE\b"
        };

        foreach (var pattern in dangerousPatterns)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(upperSql, pattern))
            {
                return false;
            }
        }

        return upperSql.TrimStart().StartsWith("SELECT") || upperSql.TrimStart().StartsWith("WITH");
    }

    private static string? ExtractDataContextAsResult(string response)
    {
        // Parse [DATA_CONTEXT: N row(s): key=value, key=value | key=value, key=value]
        var match = System.Text.RegularExpressions.Regex.Match(
            response,
            @"\[DATA_CONTEXT:\s*(\d+)\s*row\(s\):\s*(.+?)\]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline
        );

        if (!match.Success) return null;

        var rowsData = match.Groups[2].Value;
        var rowStrings = rowsData.Split('|', StringSplitOptions.RemoveEmptyEntries);

        var columns = new List<string>();
        var rows = new List<Dictionary<string, object>>();

        foreach (var rowStr in rowStrings)
        {
            var row = new Dictionary<string, object>();
            var pairs = rowStr.Split(',', StringSplitOptions.RemoveEmptyEntries);

            foreach (var pair in pairs)
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2)
                {
                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    // Add column if not seen before
                    if (!columns.Contains(key))
                    {
                        columns.Add(key);
                    }

                    // Try to parse as number
                    if (double.TryParse(value, out var numValue))
                    {
                        row[key] = numValue;
                    }
                    else
                    {
                        row[key] = value;
                    }
                }
            }

            if (row.Count > 0)
            {
                rows.Add(row);
            }
        }

        if (rows.Count == 0) return null;

        // Build JSON result
        var result = new
        {
            columns = columns,
            rows = rows,
            rowCount = rows.Count
        };

        return System.Text.Json.JsonSerializer.Serialize(result);
    }

    private static string GenerateTitle(string message)
    {
        // Clean up the message and take first 50 characters
        var cleaned = message.Trim();

        // Remove common filler words at the start
        var fillerPrefixes = new[] { "can you ", "please ", "i want to ", "show me ", "get me ", "find ", "what is ", "what are " };
        foreach (var prefix in fillerPrefixes)
        {
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(prefix.Length).TrimStart();
                break;
            }
        }

        // Capitalize first letter
        if (cleaned.Length > 0)
        {
            cleaned = char.ToUpper(cleaned[0]) + cleaned.Substring(1);
        }

        // Truncate to 50 characters max
        if (cleaned.Length > 50)
        {
            cleaned = cleaned.Substring(0, 47) + "...";
        }

        return string.IsNullOrEmpty(cleaned) ? "New Chat" : cleaned;
    }

    private static string BuildFileSystemPrompt(string? schemaContext, string fileName, int? rowCount)
    {
        var rowInfo = rowCount.HasValue ? $" ({rowCount.Value:N0} rows)" : "";

        var prompt = $@"You are Erao, a data analyst. The user uploaded '{fileName}'{rowInfo}. Data is in SQLite table ""data"". Answer ONLY about this file. If a column matches the question, query it. Refuse only if NO column matches.

Write fresh SQL for EVERY request. Never reuse [DATA_CONTEXT] tags. Never ask clarifying questions — just query.

## INTENT → FORMAT

DATA (numbers, rankings, charts) → ```sql block + ```viz block. For complex/abstract questions, add 1-2 sentences before explaining your scoring approach.
SHOW SQL (""show the query"") → explain logic + ```text block (NOT ```sql). Add ```sql + ```viz only if they also want results.
EXPLANATION (""describe"", ""what is"") → **Bold** summary. Bullets. No SQL. No filler. No emojis.
OFF-TOPIC → one sentence decline.

## SQL TEMPLATE — COPY THIS STRUCTURE FOR RANKINGS

SQLite. Table = ""data"". Double-quote ALL identifiers. Column names are CASE-SENSITIVE — use exact names from schema.
No RIGHT JOIN, no FULL OUTER JOIN. SELECT only. Dates: DATE('now'), STRFTIME().
You CANNOT use an alias in the same SELECT that defines it. Always use separate CTEs.

-- CTE 1: CLEAN. Keep ALL rows. Convert junk to NULL, numbers to REAL, booleans to 1/0.
-- NEVER use WHERE to remove rows with 'Not Mentioned'/'N/A'/NULL. They still compete.
WITH clean AS (
  SELECT *,
    CASE WHEN ""Investment Ask"" IS NOT NULL AND ""Investment Ask"" NOT IN ('Not Mentioned','N/A','','-','null')
         THEN CAST(REPLACE(""Investment Ask"", ',', '') AS REAL) ELSE NULL END AS invest_num,
    CASE WHEN ""MRR"" IS NOT NULL AND ""MRR"" NOT IN ('Not Mentioned','N/A','','-','null')
         THEN CAST(REPLACE(""MRR"", ',', '') AS REAL) ELSE NULL END AS mrr_num,
    CASE WHEN UPPER(""Sales"") = 'YES' THEN 1 ELSE 0 END AS has_sales,
    CASE WHEN UPPER(""MVP"") = 'YES' THEN 1 ELSE 0 END AS has_mvp
  FROM ""data""
),
-- CTE 2: SCORE. Think: what makes this ""good""? Use ALL relevant columns.
-- DESC = higher is better (revenue, MRR). ASC = lower is better (cost, risk).
-- Missing data: YOU DECIDE the COALESCE value. 0 = neutral, negative = penalty, positive = bonus.
-- Example: no MRR → probably no revenue → COALESCE(..., -0.1). No cost data → unknown → COALESCE(..., 0).
scored AS (
  SELECT *,
    COALESCE(PERCENT_RANK() OVER (ORDER BY mrr_num DESC), -0.1) * 0.30
    + COALESCE(PERCENT_RANK() OVER (ORDER BY invest_num ASC), 0) * 0.20
    + COALESCE(PERCENT_RANK() OVER (ORDER BY mrr_num / NULLIF(invest_num, 0) DESC), 0) * 0.15
    + has_sales * 0.20
    + has_mvp * 0.15
    AS score
  FROM clean
)
-- FINAL: Include real data columns so user sees WHY. Alias clearly.
SELECT ""Startup Name"", ""Sector"", ""Investment Ask"", ""MRR"", ""Sales"", ""MVP"",
  ROUND(COALESCE(score, 0), 2) AS ""Startup Score""
FROM scored ORDER BY score DESC LIMIT 20;

## SCORING RULES

- Use ALL relevant numeric columns, not just 1-2. Abstract concepts (""best"", ""top"", ""healthiest"") ALWAYS need multiple columns.
- PERCENT_RANK(ORDER BY col DESC) = higher is better. PERCENT_RANK(ORDER BY col ASC) = lower is better.
- Include ratio columns when both input/output exist (e.g., MRR/investment, revenue/cost).
- Booleans (YES/NO): convert to 1/0, weight directly into score.
- NEVER hardcode CASE WHEN for unknown category values. Use DENSE_RANK() or exclude from score.
- NULLIF(x, 0) in EVERY division. COALESCE every score.
- Text numbers with commas: CAST(REPLACE(col, ',', '') AS REAL).
- Rankings: ORDER BY score DESC, LIMIT 20 (unless user says otherwise). GROUP BY queries: no LIMIT.
- Final SELECT: always include name + key data columns + score. Never just name + score.
- Map user language to columns: ""revenue"" → amount/price/total, ""name"" → any name column. ""my""/""our"" = all data.
- Simple queries (COUNT, SUM, basic filter): just write direct SQL, no scoring template needed.

## VIZ BLOCK — after every ```sql

{{""chart"":""bar"",""group"":""Startup Name"",""values"":[{{""col"":""Startup Score"",""agg"":""NONE""}}]}}

chart: ""bar"" default | ""line"" time-series | ""pie"" 2-8 categories | ""table"" lists only
group: most readable column (name > ID). values: ONLY final metric, not sub-scores. agg: ""NONE"" if SQL already computed.
";

        if (!string.IsNullOrEmpty(schemaContext))
        {
            prompt += $@"
## File schema (ONLY these columns exist)
Use EXACT column names in double quotes. Never invent columns.

{schemaContext}";
        }

        return prompt;
    }

    private static string? ExtractJsonBlock(string response)
    {
        try
        {
            var tables = new List<object>();
            var searchStart = 0;

            // Find all JSON blocks
            while (searchStart < response.Length)
            {
                var jsonStart = response.IndexOf("```json", searchStart, StringComparison.OrdinalIgnoreCase);
                if (jsonStart == -1) break;

                jsonStart += 7; // Skip "```json"
                var jsonEnd = response.IndexOf("```", jsonStart, StringComparison.OrdinalIgnoreCase);
                if (jsonEnd <= jsonStart) break;

                var jsonStr = response.Substring(jsonStart, jsonEnd - jsonStart).Trim();
                searchStart = jsonEnd + 3;

                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(jsonStr);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("columns", out _) && root.TryGetProperty("rows", out _))
                    {
                        // Parse and add to tables list
                        var table = System.Text.Json.JsonSerializer.Deserialize<object>(jsonStr);
                        if (table != null)
                        {
                            tables.Add(table);
                        }
                    }
                }
                catch
                {
                    // Skip invalid JSON blocks
                }
            }

            if (tables.Count == 0) return null;
            if (tables.Count == 1)
            {
                // Single table - return as-is for backward compatibility
                return System.Text.Json.JsonSerializer.Serialize(tables[0]);
            }

            // Multiple tables - wrap in a tables array
            return System.Text.Json.JsonSerializer.Serialize(new { tables });
        }
        catch
        {
            // JSON extraction failed
        }
        return null;
    }

    private static VisualizationHint? ExtractVisualizationHint(string response)
    {
        try
        {
            // Look for ```viz block
            var vizStart = response.IndexOf("```viz", StringComparison.OrdinalIgnoreCase);
            if (vizStart == -1) return null;

            // Find content start (after ```viz and newline)
            var contentStart = response.IndexOf('\n', vizStart);
            if (contentStart == -1) return null;
            contentStart++;

            // Find closing ```
            var vizEnd = response.IndexOf("```", contentStart);
            if (vizEnd == -1) return null;

            var vizJson = response.Substring(contentStart, vizEnd - contentStart).Trim();
            if (string.IsNullOrEmpty(vizJson)) return null;

            // Parse the compact JSON format: {"chart":"bar","group":"col","values":[{"col":"x","agg":"SUM"}]}
            using var doc = System.Text.Json.JsonDocument.Parse(vizJson);
            var root = doc.RootElement;

            var hint = new VisualizationHint();

            if (root.TryGetProperty("chart", out var chartProp))
            {
                hint.ChartType = chartProp.GetString() ?? "bar";
            }

            if (root.TryGetProperty("group", out var groupProp) && groupProp.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                hint.GroupByColumn = groupProp.GetString();
            }

            if (root.TryGetProperty("values", out var valuesProp) && valuesProp.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var val in valuesProp.EnumerateArray())
                {
                    var colHint = new ValueColumnHint();

                    if (val.TryGetProperty("col", out var colProp))
                    {
                        colHint.Column = colProp.GetString() ?? "";
                    }

                    if (val.TryGetProperty("agg", out var aggProp))
                    {
                        colHint.Aggregation = aggProp.GetString() ?? "NONE";
                    }

                    if (!string.IsNullOrEmpty(colHint.Column))
                    {
                        hint.ValueColumns.Add(colHint);
                    }
                }
            }

            return hint;
        }
        catch
        {
            // Parsing failed, return null (frontend will use default behavior)
            return null;
        }
    }

    private static string StripCodeBlocks(string content)
    {
        // Remove JSON code blocks
        content = System.Text.RegularExpressions.Regex.Replace(
            content, @"```json[\s\S]*?```", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remove SQL code blocks
        content = System.Text.RegularExpressions.Regex.Replace(
            content, @"```sql[\s\S]*?```", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remove viz code blocks
        content = System.Text.RegularExpressions.Regex.Replace(
            content, @"```viz[\s\S]*?```", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remove empty markdown headers (e.g., "**Top 5 Sales:**" followed by empty line or end)
        // These appear when JSON blocks are stripped but headers remain
        // Only match headers followed by empty line or end, not headers with content after
        content = System.Text.RegularExpressions.Regex.Replace(
            content, @"\*\*[^*]+:\*\*[ \t]*\n(?=\s*\n|\s*$)", "", System.Text.RegularExpressions.RegexOptions.Multiline);

        // Clean up extra whitespace
        content = System.Text.RegularExpressions.Regex.Replace(content, @"\n{3,}", "\n\n");

        return content.Trim();
    }
}
