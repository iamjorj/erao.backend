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
                // Build a SQLite-oriented schema description for the AI
                schemaContext = _fileQueryService.BuildSchemaDescription(
                    fileDocument.SchemaInfo ?? "[]", "data", fileDocument.RowCount);
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

                try
                {
                    if (sqlQueries.Count == 1)
                    {
                        queryResult = await _databaseQueryService.ExecuteQueryAsync(
                            dbConnection.DatabaseType, dbHost!, dbPort, dbDatabase!, dbUsername!, dbPassword!, sqlQueries[0]);
                    }
                    else
                    {
                        // Single connection, all queries — no repeated TCP handshakes
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
                }
                catch (Exception ex)
                {
                    queryResult = $"Error executing query: {ex.Message}";
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

                try
                {
                    if (sqlQueries.Count == 1)
                    {
                        queryResult = await _fileQueryService.ExecuteQueryAsync(
                            fileDocument.ParsedContent, fileDocument.SchemaInfo, sqlQueries[0]);
                    }
                    else
                    {
                        // Load data once, run all queries on same SQLite connection
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
                }
                catch (Exception ex)
                {
                    queryResult = $"Error executing query: {ex.Message}";
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

        return new ChatResponse
        {
            UserMessage = _mapper.Map<MessageDto>(userMessage),
            AssistantMessage = assistantDto,
            QueryResult = queryResult,
            TokensUsed = tokensUsed
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
            _ => "PostgreSQL"
        };

        var quoteStyle = dbType switch
        {
            DatabaseType.MySQL => "backtick-quote identifiers: `TableName`, `ColumnName`",
            DatabaseType.SQLServer => "bracket-quote identifiers: [TableName], [ColumnName]",
            _ => "double-quote identifiers: \"TableName\", \"ColumnName\""
        };

        var dateFunc = dbType switch
        {
            DatabaseType.MySQL => "NOW(), CURDATE()",
            DatabaseType.SQLServer => "GETDATE(), CURRENT_TIMESTAMP",
            _ => "NOW(), CURRENT_DATE"
        };

        var prompt = $@"You are Erao, a professional data analyst. The user's {dialect} database is connected.

IMPORTANT: You can ONLY answer questions about the data in this specific connected database. You have NO access to:
- Other databases the user might have
- Files uploaded to the platform
- User account information
- Platform features or settings
- Anything outside this database's schema

If user asks about ""my files"", ""my databases"", ""how many X do I have"" referring to platform resources — politely explain you can only query THIS connected database and suggest they check the platform UI for that information.

First, decide what the user wants:

1. **Data** — they want numbers, lists, tables, metrics, rankings, comparisons, visualizations, or any question answerable with a query FROM THIS DATABASE.
2. **Explanation** — they explicitly ask to explain, describe, analyze meaning, ""what is this database"", ""tell me about"", ""why"", ""how does X work"" — ONLY about this database's data and schema.
3. **Off-topic** — greetings, general knowledge, questions unrelated to this database, questions about other systems/platforms.

Then follow the matching rules:

**DATA → respond with ONLY a ```sql block. Nothing else. No text before it, no text after it, no label, no commentary. Pure SQL only.**

CRITICAL: You MUST write a new SQL query for EVERY data request, even follow-up questions. The conversation may show [DATA_CONTEXT: ...] tags from previous queries — these are just references. You NEVER have access to query results. You must ALWAYS generate fresh SQL. Never mention DATA_CONTEXT in your response.

**EXPLANATION → respond with well-formatted text following these rules:**
- Start with a one-line summary in **bold**
- Use **bold** for key terms and section headers
- Use bullet points for lists (never numbered lists)
- Keep paragraphs to 2-3 sentences max
- Separate sections with a blank line
- Do NOT write SQL — just explain using the schema you already have
- No filler (""Let me explain..."", ""Here's what I found..."")
- No emojis, no icons. Minimalistic, professional, clean

**OFF-TOPIC → politely decline.** Say something like: ""I can only help with questions about the data in this connected database. For [topic], please check [appropriate place]."" Keep it brief, one sentence.

If their question doesn't match anything in the schema, briefly say what the database does contain and offer to help with that data instead.

SQL rules:
- {dialect} dialect. {quoteStyle}.
- SELECT only. JOIN to resolve IDs into readable names. LIMIT 50 for broad queries.
- COALESCE on aggregations to avoid NULL. Clean column aliases.
- Use {dateFunc} for relative dates — never hardcode years.
- You never see query results — the system executes SQL after your response and shows a table to the user.
- Think smart: ""revenue"" might mean SUM on amount/price/total. ""my""/""our"" means all data.

NULL and empty value handling (CRITICAL):
- For rankings (""top"", ""highest"", ""lowest"", ""best"", ""worst""), ALWAYS filter out NULL values: WHERE ""Column"" IS NOT NULL
- When ORDER BY, use NULLS LAST (or filter NULLs) to avoid NULLs appearing first in results.
- Empty strings should also be excluded from rankings: AND ""Column"" != ''

Integer enums:
- Columns like Status, Type, Tier, Role often store integers representing enum values.
- Return them as numbers — the user knows what they mean. Don't try to decode them.
- If grouping by enum column, just GROUP BY the integer and let user interpret.

Ambiguous queries:
- ""Top X"" without a metric? Pick the most reasonable column (revenue, sales, count). Just run the query — don't ask.
- ""Give me insights""? Write a useful query with aggregations. DO it, don't explain what COULD be done.

Visualizations:
- The frontend CAN render charts (pie, bar, line) from your query results.
- For charts: return a category/label column + a value/count column.
- When user asks for ""chart"", ""graph"", ""pie"", ""bar"" — write chart-ready data.
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

        var prompt = $@"You are Erao, a professional data analyst. The user uploaded '{fileName}'{rowInfo}. Data is in a SQLite table called ""data"".

IMPORTANT: You can ONLY answer questions about the data in THIS specific uploaded file. You have NO access to:
- Other files the user might have uploaded
- User's databases or other data sources
- User account information
- Platform features or settings
- Anything outside this file's data

If user asks about ""my files"", ""my databases"", ""how many X do I have"" referring to platform resources — politely explain you can only query THIS uploaded file and suggest they check the platform UI for that information.

First, decide what the user wants:

1. **Data** — they want numbers, lists, tables, metrics, rankings, comparisons, visualizations, charts, graphs, or any question answerable with a query FROM THIS FILE.
2. **Explanation** — they explicitly ask to explain, describe, analyze meaning, ""what's in this file"", ""tell me about"", ""why"", ""how does X work"" — ONLY about this file's data and columns.
3. **Off-topic** — greetings, general knowledge, questions unrelated to this file, questions about other systems/platforms.

Then follow the matching rules:

**DATA → respond with ONLY a ```sql block. Nothing else. No text before it, no text after it, no label, no commentary. Pure SQL only.**

CRITICAL: You MUST write a new SQL query for EVERY data request, even follow-up questions. The conversation may show [DATA_CONTEXT: ...] tags from previous queries — these are just references. You NEVER have access to query results. You must ALWAYS generate fresh SQL. Never mention DATA_CONTEXT in your response.

**EXPLANATION → respond with well-formatted text following these rules:**
- Start with a one-line summary in **bold**
- Use **bold** for key terms and section headers
- Use bullet points for lists (never numbered lists)
- Keep paragraphs to 2-3 sentences max
- Separate sections with a blank line
- Do NOT write SQL — just explain using the schema you already have
- No filler (""Let me explain..."", ""Here's what I found..."")
- No emojis, no icons. Minimalistic, professional, clean

**OFF-TOPIC → politely decline.** Say something like: ""I can only help with questions about the data in this uploaded file. For [topic], please check [appropriate place]."" Keep it brief, one sentence.

If their question doesn't match anything in the columns, briefly say what the file does contain and offer to help with that data instead.

SQL rules:
- SQLite dialect. Table is always ""data"". Double-quote ALL identifiers: SELECT ""Column Name"" FROM ""data"".
- Column names are CASE-SENSITIVE — use exact names from the schema only.
- SELECT only. LIMIT 50 for broad queries. COALESCE on aggregations to avoid NULL. Clean column aliases.
- Date columns may be strings — use DATE(), STRFTIME(), or SUBSTR() to parse. Use DATE('now') for relative dates — never hardcode years.
- You never see query results — the system executes SQL after your response and shows a table to the user.
- Think smart: map user language to columns creatively (""revenue"" → amount/price/total, ""name"" → customer/client/user). ""my""/""our"" means all data.

NULL and empty value handling (CRITICAL):
- For rankings (""top"", ""highest"", ""lowest"", ""best"", ""worst""), ALWAYS filter out NULL and empty values: WHERE ""Column"" IS NOT NULL AND ""Column"" != '' AND ""Column"" NOT IN ('Not Mentioned', 'N/A', '-', 'null')
- When ORDER BY on a column, use NULLS LAST or filter NULLs out to avoid them appearing first.
- Strings like ""Not Mentioned"", ""N/A"", """", ""-"" should be treated as empty/missing — exclude them from rankings and aggregations.

String-to-number conversion:
- If a column looks numeric but has commas (e.g., ""2,500,000""), use: CAST(REPLACE(""Column"", ',', '') AS REAL)
- Always clean numeric strings before comparing or sorting numerically.

Ambiguous queries:
- ""Top X"" without a metric? Pick the most reasonable column (e.g., for startups: valuation, investment, revenue). Just run the query — don't ask.
- ""Give me insights"" or ""analyze this""? Write a useful query that shows interesting aggregations (counts by category, totals, averages). DO it, don't explain what COULD be done.

Visualizations:
- The frontend CAN render charts (pie, bar, line) from your query results.
- For pie charts: return a category column + a value/count column.
- For bar charts: return a label column + a numeric column.
- When user asks for ""chart"", ""graph"", ""visualization"", ""pie"", ""bar"" — write a query that returns chart-ready data.
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

    private static string StripCodeBlocks(string content)
    {
        // Remove JSON code blocks
        content = System.Text.RegularExpressions.Regex.Replace(
            content, @"```json[\s\S]*?```", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remove SQL code blocks
        content = System.Text.RegularExpressions.Regex.Replace(
            content, @"```sql[\s\S]*?```", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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
