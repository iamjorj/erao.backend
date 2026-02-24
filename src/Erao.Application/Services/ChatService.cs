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
    private readonly IConnectorQueryService _connectorQueryService;
    private readonly IConnectorSyncService _connectorSyncService;
    private readonly IEncryptionService _encryptionService;
    private readonly IMapper _mapper;

    private static readonly TimeSpan ConnectorSyncStaleThreshold = TimeSpan.FromHours(1);

    public ChatService(
        IUnitOfWork unitOfWork,
        IOllamaService ollamaService,
        IDatabaseQueryService databaseQueryService,
        IFileQueryService fileQueryService,
        IConnectorQueryService connectorQueryService,
        IConnectorSyncService connectorSyncService,
        IEncryptionService encryptionService,
        IMapper mapper)
    {
        _unitOfWork = unitOfWork;
        _ollamaService = ollamaService;
        _databaseQueryService = databaseQueryService;
        _fileQueryService = fileQueryService;
        _connectorQueryService = connectorQueryService;
        _connectorSyncService = connectorSyncService;
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

        // Get schema context if database connection, file, or app connector is set
        string? schemaContext = null;
        DatabaseConnection? dbConnection = null;
        FileDocument? fileDocument = null;
        AppConnector? appConnector = null;

        if (conversation.AppConnectorId.HasValue)
        {
            appConnector = await _unitOfWork.AppConnectors.GetByIdAsync(conversation.AppConnectorId.Value);
            if (appConnector != null)
            {
                // Auto re-sync if data is stale (older than threshold) or never synced
                if (appConnector.SyncStatus != ConnectorSyncStatus.Syncing)
                {
                    var isStale = appConnector.LastSyncedAt == null ||
                                  (DateTime.UtcNow - appConnector.LastSyncedAt.Value) > ConnectorSyncStaleThreshold;

                    if (isStale)
                    {
                        try
                        {
                            await _connectorSyncService.SyncAsync(appConnector.Id, userId);
                            // Reload connector to get updated fields
                            appConnector = await _unitOfWork.AppConnectors.GetByIdAsync(appConnector.Id);
                        }
                        catch
                        {
                            // Sync failure is non-blocking — continue with existing data
                        }
                    }
                }

                // Use real schema from synced data if available, otherwise fall back to static template
                if (appConnector != null &&
                    appConnector.SyncStatus == ConnectorSyncStatus.Completed &&
                    !string.IsNullOrEmpty(appConnector.ParquetStoragePaths))
                {
                    schemaContext = _connectorQueryService.BuildConnectorSchemaDescription(appConnector);
                }
                else if (appConnector != null)
                {
                    schemaContext = appConnector.SchemaContext;
                }
            }
        }
        else if (conversation.DatabaseConnectionId.HasValue)
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
                // Use SampleDataJson for Parquet files (avoids loading huge ParsedContent)
                var sampleData = fileDocument.UsesParquet
                    ? fileDocument.SampleDataJson
                    : fileDocument.ParsedContent;
                var effectiveRowCount = fileDocument.UsesParquet
                    ? (int?)(fileDocument.TotalRowCount <= int.MaxValue ? (int?)fileDocument.TotalRowCount : int.MaxValue)
                    : fileDocument.RowCount;

                schemaContext = _fileQueryService.BuildSchemaDescription(
                    fileDocument.SchemaInfo ?? "[]", "data", effectiveRowCount, sampleData, fileDocument.UsesParquet);
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

        // Build system prompt - different for database vs file vs connector
        string systemPrompt;
        if (appConnector != null)
        {
            var hasSyncedData = appConnector.SyncStatus == ConnectorSyncStatus.Completed &&
                                !string.IsNullOrEmpty(appConnector.ParquetStoragePaths);
            systemPrompt = BuildConnectorSystemPrompt(schemaContext, appConnector.ConnectorType, appConnector.Name, hasSyncedData);
        }
        else if (fileDocument != null)
        {
            var isDocumentType = fileDocument.FileType == FileType.Word || fileDocument.FileType == FileType.Text;
            var effectiveRowCountForPrompt = fileDocument.UsesParquet
                ? (int?)(fileDocument.TotalRowCount <= int.MaxValue ? (int?)fileDocument.TotalRowCount : int.MaxValue)
                : fileDocument.RowCount;

            if (isDocumentType && !fileDocument.UsesParquet)
            {
                // Document mode: pass content as context + SQL capability for structured queries
                systemPrompt = BuildDocumentSystemPrompt(schemaContext, fileDocument.OriginalFileName,
                    effectiveRowCountForPrompt, fileDocument.ParsedContent, fileDocument.FileType);
            }
            else
            {
                systemPrompt = BuildFileSystemPrompt(schemaContext, fileDocument.OriginalFileName,
                    effectiveRowCountForPrompt, fileDocument.UsesParquet);
            }
        }
        else
        {
            systemPrompt = BuildSystemPrompt(schemaContext, dbConnection?.DatabaseType);
        }

        // Get AI response with full conversation history
        var (aiResponse, tokensUsed) = await _ollamaService.ChatAsync(request.Message, history, systemPrompt);

        // Layer 2: Check if AI wants to clarify before proceeding
        var clarification = ExtractClarification(aiResponse);
        if (clarification != null)
        {
            var cleanedClarificationContent = StripCodeBlocks(aiResponse);

            var clarificationAssistantMessage = new Message
            {
                ConversationId = conversation.Id,
                Role = MessageRole.Assistant,
                Content = cleanedClarificationContent,
                TokensUsed = tokensUsed
            };
            await _unitOfWork.Messages.AddAsync(clarificationAssistantMessage);

            user.QueriesUsedThisMonth++;
            await _unitOfWork.Users.UpdateAsync(user);

            stopwatch.Stop();
            var clarificationUsageLog = new UsageLog
            {
                UserId = userId,
                DatabaseConnectionId = dbConnection?.Id,
                QueryType = "Clarification",
                TokensUsed = tokensUsed,
                ExecutionTimeMs = (int)stopwatch.ElapsedMilliseconds
            };
            await _unitOfWork.UsageLogs.AddAsync(clarificationUsageLog);
            await _unitOfWork.SaveChangesAsync();

            return new ChatResponse
            {
                UserMessage = _mapper.Map<MessageDto>(userMessage),
                AssistantMessage = _mapper.Map<MessageDto>(clarificationAssistantMessage),
                QueryResult = null,
                TokensUsed = tokensUsed,
                Clarification = clarification
            };
        }

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

        // Layer 1: Parse schema once for pre-validation
        var schemaLookup = !string.IsNullOrEmpty(schemaContext)
            ? SqlSchemaValidator.ParseSchemaToLookup(schemaContext, fileDocument != null)
            : new Dictionary<string, HashSet<string>>();

        if (dbConnection != null && request.ExecuteQuery)
        {
            var sqlQueries = ExtractAllSqlFromResponse(aiResponse);

            if (sqlQueries.Count > 0)
            {
                // Layer 1: Schema pre-validation — catch hallucinated identifiers before DB roundtrip
                if (schemaLookup.Count > 0)
                {
                    var combinedSql = string.Join("\n", sqlQueries);
                    var validation = SqlSchemaValidator.Validate(combinedSql, schemaLookup);
                    if (!validation.IsValid)
                    {
                        // Build targeted retry with schema correction hints
                        var correctionHint = validation.BuildCorrectionHint();
                        var fixPrompt = BuildSqlRetryPrompt(combinedSql, correctionHint, schemaContext, dbConnection.DatabaseType);
                        var (fixResponse, fixTokens) = await _ollamaService.ChatAsync(
                            $"Fix schema issues: {correctionHint}", new List<(string, string)>(), fixPrompt);
                        tokensUsed += fixTokens;

                        var fixedQueries = ExtractAllSqlFromResponse(fixResponse);
                        if (fixedQueries.Count > 0)
                        {
                            sqlQueries = fixedQueries;
                            aiResponse = fixResponse;
                        }
                    }
                }

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
            // File mode: extract SQL from AI response and execute via DuckDB (Parquet) or SQLite (legacy)
            var sqlQueries = ExtractAllSqlFromResponse(aiResponse);
            var hasQueryableData = fileDocument.UsesParquet
                ? !string.IsNullOrEmpty(fileDocument.ParquetStoragePath)
                : !string.IsNullOrEmpty(fileDocument.ParsedContent) && !string.IsNullOrEmpty(fileDocument.SchemaInfo);

            if (sqlQueries.Count > 0 && hasQueryableData)
            {
                // Layer 1: Schema pre-validation for file mode
                if (schemaLookup.Count > 0)
                {
                    var combinedSql = string.Join("\n", sqlQueries);
                    var validation = SqlSchemaValidator.Validate(combinedSql, schemaLookup);
                    if (!validation.IsValid)
                    {
                        var correctionHint = validation.BuildCorrectionHint();
                        var fixPrompt = BuildSqlRetryPrompt(combinedSql, correctionHint, schemaContext, null, !fileDocument.UsesParquet, fileDocument.UsesParquet);
                        var (fixResponse, fixTokens) = await _ollamaService.ChatAsync(
                            $"Fix schema issues: {correctionHint}", new List<(string, string)>(), fixPrompt);
                        tokensUsed += fixTokens;

                        var fixedQueries = ExtractAllSqlFromResponse(fixResponse);
                        if (fixedQueries.Count > 0)
                        {
                            sqlQueries = fixedQueries;
                            aiResponse = fixResponse;
                        }
                    }
                }

                sqlQuery = string.Join("\n\n-- Next Query --\n\n", sqlQueries);

                // Try executing with auto-retry on failure
                const int maxRetries = 2;
                for (var attempt = 0; attempt <= maxRetries; attempt++)
                {
                    try
                    {
                        if (sqlQueries.Count == 1)
                        {
                            queryResult = await _fileQueryService.ExecuteQueryForFileAsync(
                                fileDocument.Id, sqlQueries[0]);
                        }
                        else
                        {
                            var results = await _fileQueryService.ExecuteQueriesForFileAsync(
                                fileDocument.Id, sqlQueries);
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
                            var retryPrompt = BuildSqlRetryPrompt(failedSql, ex.Message, schemaContext, null, !fileDocument.UsesParquet, fileDocument.UsesParquet);
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
        else if (appConnector != null && request.ExecuteQuery
            && appConnector.SyncStatus == ConnectorSyncStatus.Completed
            && !string.IsNullOrEmpty(appConnector.ParquetStoragePaths))
        {
            // Connector mode: extract SQL and execute via DuckDB (multi-table Parquet views)
            var sqlQueries = ExtractAllSqlFromResponse(aiResponse);

            if (sqlQueries.Count > 0)
            {
                // Layer 1: Schema pre-validation
                if (schemaLookup.Count > 0)
                {
                    var combinedSql = string.Join("\n", sqlQueries);
                    var validation = SqlSchemaValidator.Validate(combinedSql, schemaLookup);
                    if (!validation.IsValid)
                    {
                        var correctionHint = validation.BuildCorrectionHint();
                        var fixPrompt = BuildSqlRetryPrompt(combinedSql, correctionHint, schemaContext, null, false, true);
                        var (fixResponse, fixTokens) = await _ollamaService.ChatAsync(
                            $"Fix schema issues: {correctionHint}", new List<(string, string)>(), fixPrompt);
                        tokensUsed += fixTokens;

                        var fixedQueries = ExtractAllSqlFromResponse(fixResponse);
                        if (fixedQueries.Count > 0)
                        {
                            sqlQueries = fixedQueries;
                            aiResponse = fixResponse;
                        }
                    }
                }

                sqlQuery = string.Join("\n\n-- Next Query --\n\n", sqlQueries);

                const int maxRetries = 2;
                for (var attempt = 0; attempt <= maxRetries; attempt++)
                {
                    try
                    {
                        if (sqlQueries.Count == 1)
                        {
                            queryResult = await _connectorQueryService.ExecuteQueryForConnectorAsync(
                                appConnector.Id, sqlQueries[0]);
                        }
                        else
                        {
                            var results = await _connectorQueryService.ExecuteQueriesForConnectorAsync(
                                appConnector.Id, sqlQueries);
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
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (attempt < maxRetries)
                        {
                            var failedSql = string.Join("\n\n", sqlQueries);
                            var retryPrompt = BuildSqlRetryPrompt(failedSql, ex.Message, schemaContext, null, false, true);
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
                queryResult = ExtractDataContextAsResult(aiResponse);
            }
        }

        // Check if queryResult contains an error (either string prefix or JSON "error" field)
        bool hasQueryError = !string.IsNullOrEmpty(queryResult) &&
            (queryResult.StartsWith("Error", StringComparison.Ordinal) || HasJsonError(queryResult));

        // Layer 3: Empty/suspicious result handling — explain why query returned no data
        if (!string.IsNullOrEmpty(queryResult) && !hasQueryError && sqlQuery != null)
        {
            var (isEmpty, isSuspicious, rowCount) = AnalyzeQueryResult(queryResult);
            if (isEmpty || isSuspicious)
            {
                var explainPrompt = BuildResultExplanationPrompt(sqlQuery, request.Message, schemaContext, isEmpty);
                var (explanation, extraTokens) = await _ollamaService.ChatAsync(
                    "Explain empty result", new List<(string, string)>(), explainPrompt);
                tokensUsed += extraTokens;

                if (!string.IsNullOrWhiteSpace(explanation))
                {
                    aiResponse = explanation.Trim() + "\n\n" + aiResponse;
                }
            }
        }

        // Layer 4: Post-query insight generation — interpret results for non-technical users
        string? insight = null;
        List<string>? followUpQuestions = null;
        if (!string.IsNullOrEmpty(queryResult) && !hasQueryError && sqlQuery != null)
        {
            var (insightIsEmpty, _, insightRowCount) = AnalyzeQueryResult(queryResult);
            // Only generate insight when result has actual data rows
            if (!insightIsEmpty && insightRowCount > 0)
            {
                try
                {
                    var resultSummary = BuildResultSummary(queryResult);
                    var insightPrompt = BuildInsightPrompt(request.Message, sqlQuery, resultSummary);
                    var (insightResponse, insightTokens) = await _ollamaService.ChatAsync(
                        "Generate insight", new List<(string, string)>(), insightPrompt);
                    tokensUsed += insightTokens;

                    if (!string.IsNullOrWhiteSpace(insightResponse))
                    {
                        (insight, followUpQuestions) = ExtractInsightAndFollowUps(insightResponse);
                    }
                }
                catch
                {
                    // Non-blocking — insight failure doesn't break the response
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
            VisualizationHint = visualizationHint,
            Insight = insight,
            FollowUpQuestions = followUpQuestions
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
- ILIKE for case-insensitive LIKE.
- For DATE/TIMESTAMP columns, use ONLY IS NOT NULL — NEVER compare to empty string (!=''). PostgreSQL cannot cast '' to a timestamp.",
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
- No RIGHT JOIN or FULL OUTER JOIN — rewrite using LEFT JOIN.
- UNION ALL + ORDER BY: SQLite cannot use complex expressions (CASE, functions) in ORDER BY after UNION ALL. Wrap the UNION ALL in a subquery first: SELECT * FROM (...UNION ALL...) ORDER BY ...;",
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
- Supports LIMIT N.
- For DATE/TIMESTAMP columns, use ONLY IS NOT NULL — NEVER compare to empty string (!=''). DuckDB cannot cast '' to a timestamp.",
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

        var prompt = $@"You are Erao, an expert data analyst. The user's {dialect} database is connected. You can ONLY answer questions about THIS database's schema. If a question matches any table in the schema, ALWAYS query it. Only refuse for topics with no matching table.

**LANGUAGE RULE**: ALWAYS respond in the same language the user writes in. If they write in Russian, respond in Russian. If French, respond in French. SQL and code blocks stay in English, but all explanatory text must match the user's language.

## 1. RESPONSE FORMAT

Classify the user's intent, then follow the matching format:

**DATA** (DEFAULT — use this for almost everything):
- This includes: ""give me"", ""show me"", ""top 10"", ""how many"", ""compare"", ""best"", ""worst"", rankings, lists, charts, and ANY request that could involve querying the database.
- Start with 1 sentence framing the business question — what the data will reveal about their business, not what you're about to do. Example: 'Revenue concentration among your top customers will show if there's a dependency risk.' Never say 'I'll look at...' or 'Let me query...'.
- THEN include ```sql + ```viz blocks. The ```sql block is MANDATORY — without it, the user sees nothing.
- For complex concepts (""best"", ""most valuable""): explain what factors you chose and why.
- You MUST write fresh SQL for EVERY request. [DATA_CONTEXT] tags in history are past references only — never mention them.

**SHOW SQL** (""show me the sql"", ""show me sql"", ""give me the query"", ""show me the query"", ""just show sql"", ""explain the sql"", ""what sql would you use""):
- User wants to SEE and UNDERSTAND the query without running it. The UI has a separate button for viewing executed SQL.
- Write the SQL inside a ```text block (NOT ```sql — that triggers execution).
- After the ```text block, explain what each part does conversationally.
- Do NOT include ```sql or ```viz blocks. No execution. No chart. Just the query as readable text and your explanation.

**EXPLANATION** (for conceptual questions, follow-up questions about previous results, or definitions — ""what is this database about"", ""what do these columns mean"", ""describe the schema"", ""what is X"", ""what does Y mean"", ""explain that"", ""why did you...""):
- Use this when the user asks about the MEANING of something from a previous result (e.g. ""what is share_pct"", ""what does that column mean"", ""explain the last result"").
- Use this when the user asks general knowledge or conceptual questions that don't need new data.
- Do NOT use this if the user is clearly requesting NEW data, a NEW query, or a NEW comparison.
- Write like a knowledgeable colleague — conversational, clear, concise.
- Short paragraphs (2-3 sentences each). Bold only the key takeaway. No headers. No bullet walls. No numbered lists.
- No SQL blocks. No filler. No emojis.

**OFF-TOPIC** (greetings, general knowledge, unrelated):
- One sentence decline. Mention what the database contains.

## 2. SQL RULES

A. **Dialect**: {dialect}. {quoteStyle}. Use {dateFunc} for relative dates — never hardcode years. SELECT only. JOIN to resolve IDs into readable names.
{dialectNotes}

B. **Data cleaning**: Before ANY numeric operation, filter out NULL and empty values. Use NULLS LAST in ORDER BY. COALESCE every computed score to 0. Use NULLIF(x, 0) in denominators. If a column mixes numbers with text, filter non-numeric rows in a CTE first, then convert and rank.

C. **Composite scores**: For abstract concepts (""best"", ""most valuable"", ""at-risk""), NEVER sort by one column. Identify all relevant factors, normalize each with PERCENT_RANK() OVER (ORDER BY col) to 0-1 scale, weight by importance, sum into a final score. Use CTEs for cleaning → scoring → final SELECT.
- PERCENT_RANK: 0.0 = first row, 1.0 = last row. Higher-is-better → ORDER BY ASC. Lower-is-better → ORDER BY DESC.
- Boolean columns: convert to 0/1, multiply by weight directly (no PERCENT_RANK).
- Weights by importance to the question. Sum ≈ 1.0.

D. **Rankings**: ORDER BY score DESC. LIMIT 20 default unless user specifies. For GROUP BY and ""show all"": no LIMIT.

E. **Output columns**: Final SELECT MUST include real entity attributes alongside the computed score. Alias clearly. Never return only name + score.

F. **Structure**: Use CTEs for multi-step queries. Use window functions for comparisons. CRITICAL: When a CTE or subquery aliases a column (e.g., SUM(""Revenue"") AS ""Total Revenue""), the outer query MUST reference the alias (""Total Revenue""), NOT the original expression or column name. This applies to SELECT, WHERE, ORDER BY, and HAVING.

G. **Smart mapping**: ""revenue"" → amount/price/total. ""my""/""our"" → all data. Integer enum columns (Status, Type) — return as numbers.

H. **Ambiguity**: ""Top X"" without a metric? Build a composite score. ""Give me insights""? Write aggregations.

## 3. VISUALIZATION

Output a ```viz block after every ```sql block. Pick the MOST appropriate DEFAULT chart — the user can switch chart types in the UI, so just choose the best starting view:

**Choose chart type by asking: what story does this data tell?**

| User asks | chart | group | Example |
|---|---|---|---|
| ""top 10 employees"", ""best startups"", ""worst performers"" | ""bar"" | entity name (each item = own bar) | {{""chart"":""bar"",""group"":""Employee Name"",""values"":[{{""col"":""Score"",""agg"":""NONE""}}]}} |
| ""revenue by month"", ""sales over time"", ""trend"" | ""line"" | date/month column | {{""chart"":""line"",""group"":""Month"",""values"":[{{""col"":""Revenue"",""agg"":""NONE""}}]}} |
| ""breakdown by category"", ""distribution"", ""share"" (2-8 items) | ""pie"" | category column | {{""chart"":""pie"",""group"":""Department"",""values"":[{{""col"":""Count"",""agg"":""NONE""}}]}} |
| ""revenue by department"", ""count by status"" | ""bar"" | GROUP BY column | {{""chart"":""bar"",""group"":""Department"",""values"":[{{""col"":""Total Revenue"",""agg"":""NONE""}}]}} |
| ""stacked over time"", ""compare trends"" | ""area"" | date column | {{""chart"":""area"",""group"":""Quarter"",""values"":[{{""col"":""Revenue"",""agg"":""NONE""}}]}} |
| detailed list, many columns, no clear metric | ""table"" | first column | {{""chart"":""table"",""group"":""Name"",""values"":[{{""col"":""Status"",""agg"":""NONE""}}]}} |

**CRITICAL RULES:**
1. **group** = the X-axis column. For rankings/top-N → ALWAYS the individual entity name (""Startup Name"", ""Employee"", ""Product""). NEVER a category like ""Country"" or ""Sector"" for rankings. Each row = its own bar/point.
2. **values** = ONLY the 1-2 key metric columns the chart should show. agg: ""NONE"" when SQL already computed the value (which is almost always).
3. **line/area** ONLY when X-axis is a date or time period. Never for rankings.
4. **pie** ONLY for 2-8 category proportions. Never for rankings or time series.
5. If the query has no clear single metric (e.g. detailed profile of one entity), use ""table"".
6. **Cross-tab** (two GROUP BY dimensions, e.g. ""by sector and type""): use ""table"". Charts cannot show 2D cross-tabs properly.

## 4. CHECKLIST (verify before responding)
1. Column and table names match schema exactly.
2. All numeric ops preceded by NULL/empty filtering.
3. Rankings: ORDER BY DESC + LIMIT 20.
4. viz group = individual entity name for rankings. Category only for ""by X"" aggregations. Date only for time series.
5. Final SELECT has real data columns, not just name + score.
6. Return ONLY the columns the user asked about. Do NOT add extra analytical columns (row counts, averages, breakdowns) unless explicitly requested. ""Revenue year by year"" = Year + Revenue only. Keep output clean for non-technical users.
7. Outer query references CTE/subquery column ALIASES, not original column names or expressions.

## 5. CLARIFICATION (use RARELY — only when you truly cannot proceed)

If ALL of these are true, ask ONE clarification question:
1. The user's request references a concept that has NO matching column in the schema (not even partial match).
2. There are 2+ equally valid interpretations that would produce COMPLETELY different results.
3. Conversation history gives no clue about intent.

Format: output a ```clarification block with JSON:
{{""question"":""Which metric did you mean by 'performance'?"",""options"":[{{""label"":""Revenue"",""value"":""Show revenue data""}},{{""label"":""Employee Rating"",""value"":""Show employee ratings""}},{{""label"":""Something else"",""value"":""Let me clarify""}}]}}

NEVER clarify when:
- A column partially matches — just use it.
- ""Top X"" or ""best"" without metric — build composite score.
- ""Give me insights"" / ""analyze"" — just run meaningful aggregations.
- Only 1 reasonable interpretation exists.
- You can infer from conversation context.

Max 4 options. Always include ""Something else"" as the last option.
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

    private static string BuildSqlRetryPrompt(string failedSql, string errorMessage, string? schemaContext, DatabaseType? dbType, bool isFile = false, bool isDuckDBFile = false)
    {
        var dialect = isDuckDBFile ? "DuckDB" : isFile ? "SQLite" : dbType switch
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
- If using CTEs/subqueries: outer query must reference column ALIASES, not original column names or expressions.
- Also include the original ```viz block if the query had visualization intent.";

        if (isDuckDBFile)
        {
            prompt += @"
- DuckDB dialect (PostgreSQL-compatible). Table is ""data"". Double-quote all identifiers.
- ROUND(value, N) works directly. ILIKE for case-insensitive. TRY_CAST() for safe conversion.
- RIGHT JOIN and FULL OUTER JOIN work. UNION ALL + ORDER BY works directly.
- For DATE/TIMESTAMP columns: use ONLY IS NOT NULL — NEVER compare to '' (empty string causes conversion error).";
        }
        else if (isFile)
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
- For DATE/TIMESTAMP columns: use ONLY IS NOT NULL — NEVER compare to '' (causes conversion error).
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
        // BUT skip fallback if the response contains ```text blocks — that means
        // the AI intentionally used text (SHOW SQL mode), not sql (execution mode)
        if (queries.Count == 0 && !response.Contains("```text", StringComparison.OrdinalIgnoreCase))
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

    private static string BuildFileSystemPrompt(string? schemaContext, string fileName, int? rowCount, bool usesParquet = false)
    {
        var rowInfo = rowCount.HasValue ? $" ({rowCount.Value:N0} rows)" : "";

        // DuckDB (Parquet) vs SQLite dialect
        var dialect = usesParquet ? "DuckDB" : "SQLite";
        var dialectRules = usesParquet
            ? @"A. **Dialect**: DuckDB (PostgreSQL-compatible). Table is always ""data"". Double-quote ALL identifiers: SELECT ""Column Name"" FROM ""data"". Column names are CASE-SENSITIVE — use exact names from the schema. ROUND(value, N) works directly on all types. ILIKE for case-insensitive matching. Use TRY_CAST() for safe type conversion. RIGHT JOIN and FULL OUTER JOIN work. UNION ALL + ORDER BY works directly. SELECT only."
            : @"A. **Dialect**: SQLite. Table is always ""data"". Double-quote ALL identifiers: SELECT ""Column Name"" FROM ""data"". Column names are CASE-SENSITIVE — use exact names from the schema. Date functions: DATE('now'), STRFTIME(). No RIGHT JOIN or FULL OUTER JOIN. SELECT only. UNION ALL + ORDER BY: SQLite cannot use complex expressions (CASE, functions) in ORDER BY after UNION ALL. Instead, wrap the UNION ALL in a subquery first: SELECT * FROM (...UNION ALL...) ORDER BY ...;";

        var dataCleaning = usesParquet
            ? @"B. **Data cleaning**: Before ANY numeric operation on text columns, filter out junk: WHERE ""Col"" IS NOT NULL AND ""Col"" != '' AND ""Col"" NOT IN ('Not Mentioned', 'N/A', '-', 'null'). COALESCE every computed score to 0. Use NULLIF(x, 0) in denominators. For columns with commas in numbers: TRY_CAST(REPLACE(""Col"", ',', '') AS DOUBLE). When a column mixes numbers with text placeholders: filter non-numeric rows in a CTE first, TRY_CAST to DOUBLE, then rank.
IMPORTANT: For DATE/TIMESTAMP columns, use ONLY ""Col"" IS NOT NULL — NEVER compare to empty string (!=''). DuckDB cannot cast '' to a timestamp and will throw a conversion error."
            : @"B. **Data cleaning**: Before ANY numeric operation on text columns, filter out junk: WHERE ""Col"" IS NOT NULL AND ""Col"" != '' AND ""Col"" NOT IN ('Not Mentioned', 'N/A', '-', 'null'). COALESCE every computed score to 0. Use NULLIF(x, 0) in denominators. For columns with commas in numbers: CAST(REPLACE(""Col"", ',', '') AS REAL). When a column mixes numbers with text placeholders: filter non-numeric rows in a CTE first, CAST to REAL, then rank.";

        var prompt = $@"You are Erao, an expert data analyst. The user uploaded '{fileName}'{rowInfo}. Data is in a {dialect} table called ""data"". You can ONLY answer questions about THIS file's columns. If a question matches any column, ALWAYS query it. Only refuse for topics with no matching column.

**LANGUAGE RULE**: ALWAYS respond in the same language the user writes in. If they write in Russian, respond in Russian. If French, respond in French. SQL and code blocks stay in English, but all explanatory text must match the user's language.

## 1. RESPONSE FORMAT

Classify the user's intent, then follow the matching format:

**DATA** (DEFAULT — use this for almost everything):
- This includes: ""give me"", ""show me"", ""top 10"", ""how many"", ""compare"", ""best"", ""worst"", rankings, lists, charts, and ANY request that could involve querying the data.
- Start with 1 sentence framing the business question — what the data will reveal about their business, not what you're about to do. Example: 'Revenue concentration among your top customers will show if there's a dependency risk.' Never say 'I'll look at...' or 'Let me query...'.
- THEN include ```sql + ```viz blocks. The ```sql block is MANDATORY — without it, the user sees nothing.
- For complex concepts (""most productive"", ""healthiest""): explain what factors you chose and why.
- You MUST write fresh SQL for EVERY request. [DATA_CONTEXT] tags in history are past references only — never mention them.

**SHOW SQL** (""show me the sql"", ""show me sql"", ""give me the query"", ""show me the query"", ""just show sql"", ""explain the sql"", ""what sql would you use""):
- User wants to SEE and UNDERSTAND the query without running it. The UI has a separate button for viewing executed SQL.
- Write the SQL inside a ```text block (NOT ```sql — that triggers execution).
- After the ```text block, explain what each part does conversationally.
- Do NOT include ```sql or ```viz blocks. No execution. No chart. Just the query as readable text and your explanation.

**EXPLANATION** (for conceptual questions, follow-up questions about previous results, or definitions — ""what is this file about"", ""what do these columns mean"", ""describe the data"", ""what is X"", ""what does Y mean"", ""explain that"", ""why did you...""):
- Use this when the user asks about the MEANING of something from a previous result (e.g. ""what is share_pct"", ""what does that column mean"", ""explain the last result"").
- Use this when the user asks general knowledge or conceptual questions that don't need new data.
- Do NOT use this if the user is clearly requesting NEW data, a NEW query, or a NEW comparison.
- Write like a knowledgeable colleague — conversational, clear, concise.
- Short paragraphs (2-3 sentences each). Bold only the key takeaway. No headers. No bullet walls. No numbered lists.
- No SQL blocks. No filler. No emojis.

**OFF-TOPIC** (greetings, general knowledge, unrelated):
- One sentence decline. Mention what the file contains.

## 2. SQL RULES

{dialectRules}

{dataCleaning}

C. **Composite scores**: For abstract concepts (""most productive"", ""healthiest"", ""at-risk""), NEVER sort by one column. Look at ALL scoreable columns in the schema (tagged NUMERIC, MIXED, BOOLEAN), normalize each with PERCENT_RANK() OVER (ORDER BY col) to 0-1 scale, weight by importance to the question, sum into a final score. Use CTEs for cleaning → scoring → final SELECT.
- PERCENT_RANK: 0.0 = first row, 1.0 = last row. Higher-is-better → ORDER BY ASC. Lower-is-better → ORDER BY DESC.
- Boolean columns (YES/NO): convert to 0/1 flag, multiply by weight directly (no PERCENT_RANK).
- Weights by importance to the question. Sum ≈ 1.0.

D. **Rankings**: ORDER BY score DESC. LIMIT 20 default unless user specifies. For GROUP BY and ""show all"": no LIMIT.

E. **Output columns**: Final SELECT MUST include real entity attributes alongside the computed score. Alias clearly. Never return only name + score.

F. **Structure**: Use CTEs for multi-step queries. Use window functions for comparisons. CRITICAL: When a CTE or subquery aliases a column (e.g., SUM(""Revenue"") AS ""Total Revenue""), the outer query MUST reference the alias (""Total Revenue""), NOT the original expression or column name. This applies to SELECT, WHERE, ORDER BY, and HAVING.

G. **Smart mapping**: Map user language to columns (""revenue"" → amount/price/total). ""my""/""our"" → all data.

H. **Ambiguity**: ""Top X"" without a metric? Build a composite score. ""Give me insights""? Write aggregations.

## 3. VISUALIZATION

Output a ```viz block after every ```sql block. Pick the MOST appropriate DEFAULT chart — the user can switch chart types in the UI, so just choose the best starting view:

**Choose chart type by asking: what story does this data tell?**

| User asks | chart | group | Example |
|---|---|---|---|
| ""top 10 students"", ""best performers"", ""worst scores"" | ""bar"" | entity name (each item = own bar) | {{""chart"":""bar"",""group"":""Student Name"",""values"":[{{""col"":""Score"",""agg"":""NONE""}}]}} |
| ""revenue by month"", ""trend over time"" | ""line"" | date/month column | {{""chart"":""line"",""group"":""Month"",""values"":[{{""col"":""Revenue"",""agg"":""NONE""}}]}} |
| ""breakdown by category"", ""distribution"", ""share"" (2-8 items) | ""pie"" | category column | {{""chart"":""pie"",""group"":""Department"",""values"":[{{""col"":""Count"",""agg"":""NONE""}}]}} |
| ""count by status"", ""average by group"" | ""bar"" | GROUP BY column | {{""chart"":""bar"",""group"":""Status"",""values"":[{{""col"":""Total"",""agg"":""NONE""}}]}} |
| ""stacked over time"", ""compare trends"" | ""area"" | date column | {{""chart"":""area"",""group"":""Quarter"",""values"":[{{""col"":""Revenue"",""agg"":""NONE""}}]}} |
| detailed list, many columns, no clear metric | ""table"" | first column | {{""chart"":""table"",""group"":""Name"",""values"":[{{""col"":""Status"",""agg"":""NONE""}}]}} |

**CRITICAL RULES:**
1. **group** = the X-axis column. For rankings/top-N → ALWAYS the individual entity name (""Student Name"", ""Employee"", ""Startup Name""). NEVER a category like ""Country"" or ""Subject"" for rankings. Each row = its own bar/point.
2. **values** = ONLY the 1-2 key metric columns the chart should show. agg: ""NONE"" when SQL already computed the value (which is almost always).
3. **line/area** ONLY when X-axis is a date or time period. Never for rankings.
4. **pie** ONLY for 2-8 category proportions. Never for rankings or time series.
5. If the query has no clear single metric (e.g. detailed profile of one entity), use ""table"".
6. **Cross-tab** (two GROUP BY dimensions, e.g. ""by sector and type""): use ""table"". Charts cannot show 2D cross-tabs properly.

## 4. CHECKLIST (verify before responding)
1. Column names match schema exactly (spelling, case, double-quoted).
2. All numeric ops preceded by NULL/empty/placeholder filtering.
3. Rankings: ORDER BY DESC + LIMIT 20.
4. viz group = individual entity name for rankings. Category only for ""by X"" aggregations. Date only for time series.
5. Final SELECT has real data columns, not just name + score.
6. Return ONLY the columns the user asked about. Do NOT add extra analytical columns (row counts, averages, breakdowns) unless explicitly requested. ""Revenue year by year"" = Year + Revenue only. Keep output clean for non-technical users.
7. Outer query references CTE/subquery column ALIASES, not original column names or expressions.

## 5. CLARIFICATION (use RARELY — only when you truly cannot proceed)

If ALL of these are true, ask ONE clarification question:
1. The user's request references a concept that has NO matching column in the schema (not even partial match).
2. There are 2+ equally valid interpretations that would produce COMPLETELY different results.
3. Conversation history gives no clue about intent.

Format: output a ```clarification block with JSON:
{{""question"":""Which metric did you mean by 'performance'?"",""options"":[{{""label"":""Revenue"",""value"":""Show revenue data""}},{{""label"":""Employee Rating"",""value"":""Show employee ratings""}},{{""label"":""Something else"",""value"":""Let me clarify""}}]}}

NEVER clarify when:
- A column partially matches — just use it.
- ""Top X"" or ""best"" without metric — build composite score.
- ""Give me insights"" / ""analyze"" — just run meaningful aggregations.
- Only 1 reasonable interpretation exists.
- You can infer from conversation context.

Max 4 options. Always include ""Something else"" as the last option.
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

    private static string BuildConnectorSystemPrompt(string? schemaContext, ConnectorType connectorType, string connectorName, bool hasSyncedData = false)
    {
        var appName = connectorType switch
        {
            ConnectorType.Shopify => "Shopify",
            ConnectorType.Stripe => "Stripe",
            ConnectorType.WooCommerce => "WooCommerce",
            ConnectorType.QuickBooks => "QuickBooks",
            ConnectorType.HubSpot => "HubSpot",
            ConnectorType.Salesforce => "Salesforce",
            ConnectorType.GoogleAnalytics => "Google Analytics",
            ConnectorType.Notion => "Notion",
            ConnectorType.Airtable => "Airtable",
            ConnectorType.GoogleSheets => "Google Sheets",
            _ => connectorName
        };

        if (hasSyncedData)
        {
            return BuildSyncedConnectorSystemPrompt(schemaContext, appName, connectorName, connectorType);
        }

        var domainGuidance = GetConnectorDomainGuidance(connectorType);

        return $@"You are Erao, an expert data analyst specializing in {appName} data. The user connected their {appName} account ""{connectorName}"".

**LANGUAGE RULE**: ALWAYS respond in the same language the user writes in. SQL and code blocks stay in English, but all explanatory text must match the user's language.

## IMPORTANT: DATA SYNC NOT YET ACTIVE

The user's {appName} data has NOT been synced yet — live data sync is coming soon. You CANNOT run queries or return actual numbers. NEVER fabricate, estimate, or hallucinate data values.

Instead, for every data question:
1. Explain clearly what the query would return once data sync is live.
2. Show the exact SQL query that will answer their question (so they can see Erao understands their {appName} data model).
3. Describe the expected shape of the results (columns, typical patterns for {appName} stores).

## SCHEMA (will be queryable once data sync is live)

{schemaContext ?? "No schema available."}

{domainGuidance}

## 1. RESPONSE FORMAT

**DATA** (DEFAULT — use for almost every question):
Start with: ""Once your {appName} data syncs, here's exactly how I'll answer this:""
Then provide the SQL query in a ```sql code block. After the query, briefly describe what the results would look like and what business insights the user could expect.

**CONVERSATIONAL** (ONLY for greetings, thanks, or off-topic):
Reply naturally in 1-2 sentences. Mention you're ready to analyze their {appName} data once sync is live.

## 2. SQL RULES

A. **Dialect**: PostgreSQL. Double-quote all identifiers.
B. **Tables**: Only use tables and columns defined in the schema above.
C. **SELECT only**: Never INSERT, UPDATE, DELETE, DROP, or ALTER.
D. **Aggregations**: Use GROUP BY for any aggregate function.
E. **Formatting**: ROUND monetary values to 2 decimal places.
F. **Limits**: Default LIMIT 100 unless user specifies otherwise.

## 3. VISUALIZATION HINT

After the ```sql block, if the result is chartable, add a ```viz block:
```viz
chartType: bar | line | pie | area | table
groupByColumn: <x-axis column>
valueColumns:
  - column: <y-axis column>
    aggregation: SUM | AVG | COUNT | MIN | MAX | NONE
```

## 4. CHECKLIST
- [ ] NEVER return fake data or made-up numbers
- [ ] SQL uses only schema columns
- [ ] All identifiers double-quoted
- [ ] SELECT only
- [ ] Has GROUP BY if using aggregates
- [ ] LIMIT present";
    }

    private static string GetConnectorDomainGuidance(ConnectorType connectorType) => connectorType switch
    {
        ConnectorType.Shopify => @"## DOMAIN KNOWLEDGE — Shopify

### Vocabulary Mappings
- ""revenue"" / ""sales"" → SUM(""total_price"") WHERE ""financial_status"" = 'paid'
- ""AOV"" (average order value) → AVG(""total_price"") WHERE ""financial_status"" = 'paid'
- ""repeat customers"" → customers WHERE ""orders_count"" > 1
- ""new customers"" → customers WHERE ""orders_count"" = 1
- ""refunds"" → orders WHERE ""financial_status"" = 'refunded' OR 'partially_refunded'
- ""unfulfilled orders"" → orders WHERE ""fulfillment_status"" IS NULL (NULL = unfulfilled, not empty string)
- ""products sold"" → SUM(""quantity"") from line_items

### Table Relationships
- ""orders"" → ""customers"" via ""customer_id""
- ""orders"" → ""line_items"" via ""order_id""
- ""line_items"" → ""products"" via ""product_id""
- ""products"" → ""variants"" via ""product_id""

### Data Quirks
- Timestamps: ""created_at"", ""updated_at"", ""closed_at"", ""cancelled_at""
- ""fulfillment_status"": NULL (unfulfilled), 'partial', 'fulfilled' — NULL is the default, not an error
- ""financial_status"": 'pending', 'authorized', 'paid', 'partially_paid', 'refunded', 'partially_refunded', 'voided'
- ""total_price"" is already in the store's currency (no cents conversion needed)
- ""discount_codes"" may be a comma-separated string — use LIKE for matching

### Common Metrics
- Revenue: SELECT SUM(""total_price"") FROM ""orders"" WHERE ""financial_status"" = 'paid'
- AOV: SELECT ROUND(AVG(""total_price""), 2) FROM ""orders"" WHERE ""financial_status"" = 'paid'
- Repeat rate: SELECT ROUND(COUNT(*) FILTER (WHERE ""orders_count"" > 1) * 100.0 / COUNT(*), 1) FROM ""customers""",

        ConnectorType.Stripe => @"## DOMAIN KNOWLEDGE — Stripe

### Vocabulary Mappings
- ""revenue"" / ""sales"" → SUM(""amount"") from charges/payments WHERE ""status"" = 'succeeded'
- ""MRR"" → SUM(""plan_amount"") from subscriptions WHERE ""status"" = 'active' AND ""interval"" = 'month'
- ""ARR"" → MRR * 12
- ""churn"" → subscriptions WHERE ""status"" = 'canceled'
- ""failed payments"" → charges WHERE ""status"" = 'failed'
- ""disputes"" → disputes table, ""reason"" column

### Table Relationships
- ""charges"" / ""payments"" → ""customers"" via ""customer_id""
- ""subscriptions"" → ""customers"" via ""customer_id""
- ""subscriptions"" → ""plans"" / ""prices"" via ""plan_id"" or ""price_id""
- ""invoices"" → ""customers"" via ""customer_id""
- ""invoices"" → ""subscriptions"" via ""subscription_id""

### Data Quirks
- **Amounts are already in DOLLARS** (converted from cents during sync) — do NOT divide by 100
- ID columns have prefixes: ch_ (charges), sub_ (subscriptions), cus_ (customers), in_ (invoices)
- Timestamps: ""created"" (Unix epoch in some cases) — check schema for actual format
- ""status"" values for subscriptions: 'active', 'past_due', 'canceled', 'unpaid', 'trialing', 'incomplete'
- ""status"" values for charges: 'succeeded', 'pending', 'failed'

### Common Metrics
- MRR: SELECT SUM(""plan_amount"") FROM ""subscriptions"" WHERE ""status"" = 'active' AND ""interval"" = 'month'
- Churn rate: canceled in period / active at start of period * 100
- ARPU: SUM(""amount"") / COUNT(DISTINCT ""customer_id"") for succeeded charges",

        ConnectorType.WooCommerce => @"## DOMAIN KNOWLEDGE — WooCommerce

### Vocabulary Mappings
- ""revenue"" / ""sales"" → SUM(""total"") WHERE ""status"" IN ('wc-processing', 'wc-completed')
- ""AOV"" → AVG(""total"") for valid statuses
- ""refunds"" → orders WHERE ""status"" = 'wc-refunded'
- ""coupons"" / ""discounts"" → ""coupons"" table or ""discount_total"" column on orders

### Table Relationships
- ""orders"" → ""customers"" via ""customer_id""
- ""orders"" → ""order_items"" / ""line_items"" via ""order_id""
- ""order_items"" → ""products"" via ""product_id""
- ""products"" → ""categories"" via ""category_id"" or category mapping table

### Data Quirks
- Timestamp column: ""date_created"" (NOT ""created_at"" — differs from Shopify!)
- Also has ""date_modified"", ""date_completed"", ""date_paid""
- Order statuses are prefixed: 'wc-pending', 'wc-processing', 'wc-on-hold', 'wc-completed', 'wc-cancelled', 'wc-refunded', 'wc-failed'
- ""total"" is the order total in store currency (no cents conversion)
- ""discount_total"" is per-order discount amount

### Common Metrics
- Revenue: SELECT SUM(""total"") FROM ""orders"" WHERE ""status"" IN ('wc-processing', 'wc-completed')
- AOV: SELECT ROUND(AVG(""total""), 2) FROM ""orders"" WHERE ""status"" IN ('wc-processing', 'wc-completed')",

        ConnectorType.QuickBooks => @"## DOMAIN KNOWLEDGE — QuickBooks

### Vocabulary Mappings
- ""revenue"" / ""income"" → ""total_income"" from profit_and_loss
- ""expenses"" → ""total_expenses"" from profit_and_loss
- ""net income"" / ""profit"" → ""net_income"" from profit_and_loss
- ""COGS"" / ""cost of goods"" → ""cost_of_goods_sold"" from profit_and_loss
- ""gross profit"" → ""gross_profit"" from profit_and_loss
- ""accounts receivable"" / ""AR"" → invoices/customers WHERE balance > 0
- ""accounts payable"" / ""AP"" → bills WHERE balance > 0

### Table Relationships
- ""invoices"" → ""customers"" via ""customer_id""
- ""bills"" / ""expenses"" → ""vendors"" via ""vendor_id""
- ""journal_entries"" → ""accounts"" via ""account_id""

### Data Quirks
- **""profit_and_loss"" is a MONTHLY SUMMARY table** — each row is one month's totals
  - Do NOT re-aggregate (no SUM of monthly totals unless computing annual total)
  - To get ""Q1 revenue"": filter months 1-3 and SUM ""total_income""
  - Column ""period"" or ""month"" identifies the time period
- Amounts are in the company's base currency
- ""balance"" columns represent outstanding amounts

### Common Metrics
- Monthly revenue: SELECT ""period"", ""total_income"" FROM ""profit_and_loss"" ORDER BY ""period""
- Annual net income: SELECT SUM(""net_income"") FROM ""profit_and_loss"" WHERE EXTRACT(YEAR FROM ""period"") = <year>
- Gross margin: ROUND(""gross_profit"" * 100.0 / NULLIF(""total_income"", 0), 1)",

        ConnectorType.HubSpot => @"## DOMAIN KNOWLEDGE — HubSpot

### Vocabulary Mappings
- ""pipeline"" / ""deals by stage"" → GROUP BY ""deal_stage"" from deals
- ""win rate"" → COUNT(won deals) / COUNT(closed deals) * 100
- ""deal value"" → ""amount"" column (can be NULL for early-stage deals)
- ""lifecycle"" / ""funnel"" → contacts grouped by ""lifecycle_stage""
- ""leads"" → contacts WHERE ""lifecycle_stage"" = 'lead'
- ""MQLs"" → contacts WHERE ""lifecycle_stage"" = 'marketingqualifiedlead'
- ""SQLs"" → contacts WHERE ""lifecycle_stage"" = 'salesqualifiedlead'

### Table Relationships
- ""deals"" → ""contacts"" via ""contact_id"" or association table
- ""deals"" → ""companies"" via ""company_id""
- ""contacts"" → ""companies"" via ""company_id""

### Data Quirks
- Deal stages are **lowercase**: 'appointmentscheduled', 'qualifiedtobuy', 'presentationscheduled', 'decisionmakerboughtin', 'contractsent', 'closedwon', 'closedlost'
- Lifecycle stages are **lowercase concatenated**: 'subscriber', 'lead', 'marketingqualifiedlead', 'salesqualifiedlead', 'opportunity', 'customer', 'evangelist'
- ""amount"" can be NULL for deals without a value set — always COALESCE or filter
- Timestamps: ""created_at"", ""updated_at"", ""closed_at""

### Common Metrics
- Pipeline value: SELECT ""deal_stage"", COUNT(*), SUM(COALESCE(""amount"", 0)) FROM ""deals"" GROUP BY ""deal_stage""
- Win rate: SELECT ROUND(COUNT(*) FILTER (WHERE ""deal_stage"" = 'closedwon') * 100.0 / NULLIF(COUNT(*) FILTER (WHERE ""deal_stage"" IN ('closedwon', 'closedlost')), 0), 1) FROM ""deals""
- Lifecycle funnel: SELECT ""lifecycle_stage"", COUNT(*) FROM ""contacts"" GROUP BY ""lifecycle_stage""",

        ConnectorType.Salesforce => @"## DOMAIN KNOWLEDGE — Salesforce

### Vocabulary Mappings
- ""pipeline"" / ""deals by stage"" → GROUP BY ""stage_name"" from opportunities
- ""win rate"" → COUNT(closed-won) / COUNT(all closed) * 100
- ""forecast"" → SUM(""amount"" * ""probability"" / 100) for open opportunities
- ""deal value"" → ""amount"" column on opportunities
- ""lead conversion"" → leads WHERE ""converted_at"" IS NOT NULL
- ""activities"" → tasks + events tables

### Table Relationships
- ""opportunities"" → ""accounts"" via ""account_id""
- ""opportunities"" → ""contacts"" via contact roles or ""contact_id""
- ""leads"" → ""accounts"" via conversion (""converted_account_id"")
- ""contacts"" → ""accounts"" via ""account_id""
- ""tasks"" / ""events"" → ""opportunities"" or ""contacts"" via ""related_to_id""

### Data Quirks
- Opportunity stages are **Title Case**: 'Prospecting', 'Qualification', 'Needs Analysis', 'Proposal/Price Quote', 'Negotiation/Review', 'Closed Won', 'Closed Lost'
- ""probability"" is 0-100 (integer percentage), NOT a decimal 0-1
- Lead conversion: ""is_converted"" boolean, ""converted_at"" timestamp, ""converted_account_id"", ""converted_contact_id""
- Timestamps: ""created_date"", ""last_modified_date"", ""close_date""

### Common Metrics
- Pipeline: SELECT ""stage_name"", COUNT(*), SUM(""amount"") FROM ""opportunities"" WHERE ""is_closed"" = false GROUP BY ""stage_name""
- Weighted forecast: SELECT SUM(""amount"" * ""probability"" / 100) FROM ""opportunities"" WHERE ""is_closed"" = false
- Win rate: SELECT ROUND(COUNT(*) FILTER (WHERE ""stage_name"" = 'Closed Won') * 100.0 / NULLIF(COUNT(*) FILTER (WHERE ""is_closed"" = true), 0), 1) FROM ""opportunities""",

        ConnectorType.GoogleAnalytics => @"## DOMAIN KNOWLEDGE — Google Analytics

### Vocabulary Mappings
- ""traffic"" / ""visits"" → ""sessions"" column in ""sessions"" table (YES, column name = table name)
- ""sources"" / ""where traffic comes from"" → ""session_source"" and ""session_medium"" columns
- ""bounce rate"" → ""bounce_rate"" column (value is 0-1 RATIO, multiply by 100 for percentage)
- ""pages"" / ""top pages"" → ""page_path"" or ""page_title"" columns
- ""users"" / ""visitors"" → ""total_users"" or ""active_users"" columns
- ""session duration"" → ""average_session_duration"" (in SECONDS — divide by 60 for minutes)
- ""conversions"" → ""conversions"" or ""goal_completions"" column

### Table Relationships
- Typically flat/denormalized tables — JOINs are rare
- Main tables: ""sessions"", ""pages"", ""events"" (check schema for exact names)

### Data Quirks
- **COLUMN AMBIGUITY**: The ""sessions"" table has a ""sessions"" column — always qualify: ""sessions"".""sessions""
- Date column: ""date"" stored as 'YYYYMMDD' STRING format — use CAST or string functions for date filtering
  - Example: WHERE ""date"" >= '20240101' AND ""date"" <= '20240131'
- ""bounce_rate"" is 0-1 (ratio), NOT 0-100 — display as ROUND(""bounce_rate"" * 100, 1) for percentage
- ""average_session_duration"" is in seconds — ROUND(""average_session_duration"" / 60, 1) for minutes
- ""new_users"" vs ""total_users"": new_users are first-time, total_users includes returning

### Common Metrics
- Total sessions: SELECT SUM(""sessions"".""sessions"") FROM ""sessions""
- Traffic sources: SELECT ""session_source"", SUM(""sessions"") FROM ""sessions"" GROUP BY ""session_source"" ORDER BY SUM(""sessions"") DESC
- Bounce rate: SELECT ROUND(AVG(""bounce_rate"") * 100, 1) AS bounce_pct FROM ""sessions""",

        ConnectorType.Notion => @"## DOMAIN KNOWLEDGE — Notion

### CRITICAL LIMITATION
This connector provides **METADATA ONLY** — workspace structure (pages, databases, parent-child relationships, titles, created/edited timestamps).

**You CANNOT see page content, block text, or rich text.** If the user asks about what's written inside a page, explain this limitation clearly:
""I can see your Notion workspace structure (page names, databases, hierarchy, timestamps) but I cannot access the actual content inside pages. This is a metadata-level integration.""

### What You CAN Query
- Page/database titles and hierarchy
- Created and last edited timestamps
- Parent-child relationships between pages
- Database names and structure

### What You CANNOT Query
- Page body content, text blocks, headings
- Database row data / property values
- Comments, mentions, or embedded content",

        ConnectorType.Airtable => @"## DOMAIN KNOWLEDGE — Airtable

### Dynamic Schema
Airtable bases have user-defined tables and columns — there is NO fixed schema. Read the schema section carefully to understand this specific base's structure.

### Data Quirks
- Every table has system columns: ""id"" (record ID) and ""created_time""
- **Multi-select fields** are stored as comma-separated strings — use LIKE '%value%' or string_split for filtering
- **Linked records** (references to other tables) may appear as record IDs or display values depending on sync
- **Attachment fields** contain URLs, not file content
- **Formula/rollup/lookup fields** are computed — treat as read-only values
- Column names are exactly as the user defined them (may contain spaces, special characters)

### Query Tips
- Always check the schema for exact column names — they are user-defined and unpredictable
- Multi-select filter: WHERE ""Tags"" LIKE '%Marketing%'
- Date fields: typically ISO 8601 format ('2024-01-15T10:30:00.000Z')",

        ConnectorType.GoogleSheets => @"## DOMAIN KNOWLEDGE — Google Sheets

### Dynamic Schema
Google Sheets have user-defined columns from the header row — there is NO fixed schema. Read the schema section carefully.

### Data Quirks
- **No ID column** — rows have no unique identifier unless the user created one
- **Dates may be stored as text** — '1/15/2024', 'January 15, 2024', '2024-01-15' are all possible. Use TRY_CAST for safe conversion.
- **Boolean-like values are STRINGS**: 'Yes'/'No', 'TRUE'/'FALSE', 'Y'/'N' — use string comparison, NOT boolean operators
- **Numbers may be stored as text** with formatting: '$1,234.56', '50%' — may need REPLACE and CAST
- **Empty cells** may be NULL or empty string '' — check both: WHERE ""col"" IS NOT NULL AND ""col"" != ''
- Column names come from the header row — may contain spaces, special characters, or be very long

### Query Tips
- Always check schema for exact column names
- Use TRY_CAST(""col"" AS DOUBLE) for numeric operations on potentially text columns
- For date filtering: TRY_CAST(""Date"" AS DATE) to safely handle mixed formats
- Empty check: WHERE ""col"" IS NOT NULL AND TRIM(""col"") != ''",

        _ => ""
    };

    private static string GetConnectorSmartMapping(ConnectorType connectorType) => connectorType switch
    {
        ConnectorType.Shopify => @"""revenue"" → SUM(""total_price"") WHERE ""financial_status""='paid'. ""AOV"" → AVG(""total_price""). ""my""/""our"" → all data. ""repeat customers"" → ""orders_count"">1. Map user language to actual column names.",
        ConnectorType.Stripe => @"""revenue"" → SUM(""amount"") WHERE ""status""='succeeded'. ""MRR"" → SUM(""plan_amount"") for active monthly subs. ""ARR"" → MRR*12. Amounts are already in dollars. Map user language to actual column names.",
        ConnectorType.WooCommerce => @"""revenue"" → SUM(""total"") WHERE ""status"" IN ('wc-processing','wc-completed'). ""AOV"" → AVG(""total""). ""my""/""our"" → all data. Map user language to actual column names.",
        ConnectorType.QuickBooks => @"""revenue"" → ""total_income"" from profit_and_loss. ""expenses"" → ""total_expenses"". ""net income"" → ""net_income"". Do NOT re-aggregate monthly summary rows unless computing period totals. Map user language to actual column names.",
        ConnectorType.HubSpot => @"""pipeline"" → deals grouped by ""deal_stage"". ""win rate"" → closed-won/all-closed*100. ""revenue"" → SUM(""amount"") WHERE stage='closedwon'. Stages are lowercase. Map user language to actual column names.",
        ConnectorType.Salesforce => @"""pipeline"" → opportunities grouped by ""stage_name"". ""win rate"" → Closed Won/all closed*100. ""forecast"" → SUM(""amount""*""probability""/100). Stages are Title Case. Map user language to actual column names.",
        ConnectorType.GoogleAnalytics => @"""traffic"" → ""sessions"" column (qualify as ""sessions"".""sessions"" to avoid ambiguity). ""bounce rate"" is 0-1 ratio (multiply by 100). Duration is in seconds. Dates are 'YYYYMMDD' strings. Map user language to actual column names.",
        ConnectorType.Notion => @"METADATA ONLY — can query page titles, hierarchy, timestamps. Cannot access page content. Map user questions about structure to available columns.",
        ConnectorType.Airtable => @"Dynamic schema — read the schema carefully. Multi-select fields are comma-separated strings. ""id"" and ""created_time"" are system columns. Map user language to the actual column names shown in the schema.",
        ConnectorType.GoogleSheets => @"Dynamic schema — read the schema carefully. No ID column. Dates/booleans/numbers may be stored as text strings. Use TRY_CAST for safe conversion. Map user language to the actual column names shown in the schema.",
        _ => @"""revenue"" → amount/total_price. ""my""/""our"" → all data. Map user language to actual column names."
    };

    private static string BuildSyncedConnectorSystemPrompt(string? schemaContext, string appName, string connectorName, ConnectorType connectorType)
    {
        var domainGuidance = GetConnectorDomainGuidance(connectorType);
        var smartMapping = GetConnectorSmartMapping(connectorType);

        return $@"You are Erao, an expert data analyst specializing in {appName} data. The user's {appName} account ""{connectorName}"" is synced — you have REAL data to query.

**LANGUAGE RULE**: ALWAYS respond in the same language the user writes in. SQL and code blocks stay in English, but all explanatory text must match the user's language.

## 1. RESPONSE FORMAT

Classify the user's intent, then follow the matching format:

**DATA** (DEFAULT — use this for almost everything):
- This includes: ""give me"", ""show me"", ""top 10"", ""how many"", ""compare"", ""best"", ""worst"", rankings, lists, charts, and ANY request that could involve querying the data.
- Start with 1 sentence framing the business question — what the data will reveal about their business, not what you're about to do. Never say 'I'll look at...' or 'Let me query...'.
- THEN include ```sql + ```viz blocks. The ```sql block is MANDATORY — without it, the user sees nothing.
- You MUST write fresh SQL for EVERY request. [DATA_CONTEXT] tags in history are past references only — never mention them.

**SHOW SQL** (""show me the sql"", ""show me sql"", ""give me the query""):
- Write the SQL inside a ```text block (NOT ```sql). Explain what each part does. No execution.

**EXPLANATION** (conceptual questions, follow-ups about previous results):
- Write like a knowledgeable colleague — conversational, clear, concise.
- No SQL blocks. No filler.

**OFF-TOPIC** (greetings, general knowledge, unrelated):
- One sentence decline. Mention what {appName} data is available.

{domainGuidance}

## 2. SQL RULES

A. **Dialect**: DuckDB (PostgreSQL-compatible). Double-quote ALL identifiers: ""table_name"", ""column_name"". Column and table names are CASE-SENSITIVE — use exact names from the schema.
B. **Multi-table**: Data is organized in multiple tables. You can JOIN across tables (e.g., JOIN ""customers"" ON ""orders"".""customer_id"" = ""customers"".""id"").
C. **Data cleaning**: Filter NULL/empty before numeric ops. COALESCE computed scores to 0. Use NULLIF(x, 0) in denominators. ROUND(value, N) works directly. ILIKE for case-insensitive. TRY_CAST() for safe conversion.
D. **Monetary values**: All amounts are in dollars (already converted from cents for Stripe). ROUND to 2 decimal places.
E. **Rankings**: ORDER BY DESC + LIMIT 20 default unless user specifies.
F. **Structure**: Use CTEs for multi-step queries. Use window functions for comparisons. CRITICAL: When a CTE or subquery aliases a column (e.g., SUM(""Revenue"") AS ""Total Revenue""), the outer query MUST reference the alias (""Total Revenue""), NOT the original expression or column name. This applies to SELECT, WHERE, ORDER BY, and HAVING.
G. **Smart mapping**: {smartMapping}

## 3. VISUALIZATION

Output a ```viz block after every ```sql block:

| User asks | chart | group |
|---|---|---|
| ""top 10"", ""best"", ""worst"" | ""bar"" | entity name |
| ""over time"", ""by month"", ""trend"" | ""line"" | date column |
| ""breakdown"", ""distribution"" (2-8 items) | ""pie"" | category |
| ""by category"", ""by status"" | ""bar"" | GROUP BY column |
| ""compare trends"" | ""area"" | date column |
| detailed list, many columns | ""table"" | first column |

Format: ```viz\n{{""chart"":""bar"",""group"":""Column"",""values"":[{{""col"":""Metric"",""agg"":""NONE""}}]}}\n```

## 4. CHECKLIST
1. Table and column names match schema exactly (double-quoted, case-sensitive).
2. All numeric ops preceded by NULL filtering.
3. Rankings: ORDER BY DESC + LIMIT 20.
4. viz group = entity name for rankings, category for aggregations, date for time series.
5. Return ONLY the columns the user asked about. No extra analytical columns (row counts, averages) unless explicitly requested. Keep output clean for non-technical users.
6. Outer query references CTE/subquery column ALIASES, not original column names or expressions.

## 5. CLARIFICATION (use RARELY)

Only if ALL: no matching column, 2+ valid interpretations, no context clues.
```clarification
{{""question"":""..."",""options"":[{{""label"":""..."",""value"":""...""}},{{""label"":""Something else"",""value"":""Let me clarify""}}]}}
```

## Schema (REAL synced data — ONLY these tables/columns exist)

{schemaContext ?? "No schema available."}";
    }

    private static string BuildDocumentSystemPrompt(string? schemaContext, string fileName, int? rowCount, string? parsedContent, FileType fileType)
    {
        var fileTypeLabel = fileType == FileType.Word ? "Word document" : "text file";

        // Extract readable content from parsed data for document context
        var documentContent = ExtractDocumentContent(parsedContent);
        var contentPreview = documentContent.Length > 8000
            ? documentContent.Substring(0, 8000) + "\n\n[... document truncated for context ...]"
            : documentContent;

        var prompt = $@"You are Erao, an expert document analyst. The user uploaded '{fileName}' (a {fileTypeLabel}).

You have TWO capabilities:

## CAPABILITY 1: DOCUMENT Q&A (for questions about the document content)

When the user asks about the document content (""what is this about"", ""summarize"", ""what does section X say"", ""find mentions of..."", ""explain...""), answer directly from the document content below. NO SQL needed.

Write conversationally — short paragraphs, clear, concise. Bold key takeaways. No bullet walls.

## CAPABILITY 2: STRUCTURED QUERIES (for data extraction)

The document content is also stored in a SQLite table called ""data"" with columns: ""section"" (number), ""type"" (heading/paragraph/list/table_row), ""content"" (text).

When the user asks for structured extraction (""list all headings"", ""how many paragraphs"", ""find rows containing X"", ""count sections""), generate SQL:

```sql
SELECT ""content"" FROM ""data"" WHERE ""type"" = 'heading'
```

Rules:
- Table is always ""data"". Double-quote ALL identifiers.
- Use LIKE for text search: WHERE ""content"" LIKE '%keyword%'
- Available types: 'heading', 'paragraph', 'list', 'table_row'
- Include a ```viz block after SQL: {{""chart"":""table"",""group"":""content"",""values"":[{{""col"":""type"",""agg"":""NONE""}}]}}

## HOW TO DECIDE

- Questions about meaning, summary, explanation → CAPABILITY 1 (no SQL)
- Questions about structure, counts, searching, listing → CAPABILITY 2 (SQL)
- If unclear, prefer CAPABILITY 1 (most document questions are about content)

## DOCUMENT CONTENT

```
{contentPreview}
```
";

        if (!string.IsNullOrEmpty(schemaContext))
        {
            prompt += $@"
## Table schema (for structured queries)
{schemaContext}";
        }

        return prompt;
    }

    private static string ExtractDocumentContent(string? parsedContent)
    {
        if (string.IsNullOrEmpty(parsedContent)) return "(no content available)";

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(parsedContent);
            var root = doc.RootElement;

            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                // New format: array of {section, type, content}
                var lines = new List<string>();
                foreach (var row in root.EnumerateArray())
                {
                    var type = row.TryGetProperty("type", out var t) ? t.GetString() : "paragraph";
                    var content = row.TryGetProperty("content", out var c) ? c.GetString() : "";

                    if (string.IsNullOrWhiteSpace(content)) continue;

                    if (type == "heading")
                        lines.Add($"\n## {content}");
                    else if (type == "table_row")
                        lines.Add($"  | {content}");
                    else if (type == "list")
                        lines.Add($"  - {content}");
                    else
                        lines.Add(content);
                }
                return string.Join("\n", lines);
            }
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                // Old format: {sections, fullText, ...}
                if (root.TryGetProperty("fullText", out var fullText))
                    return fullText.GetString() ?? "(no content)";
            }
        }
        catch
        {
            // If parsing fails, try to use raw content
            if (parsedContent.Length < 10000)
                return parsedContent;
        }

        return "(content could not be extracted)";
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

        // Remove clarification code blocks
        content = System.Text.RegularExpressions.Regex.Replace(
            content, @"```clarification[\s\S]*?```", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remove insight/followups code blocks (safety — these come from separate AI call)
        content = System.Text.RegularExpressions.Regex.Replace(
            content, @"```insight[\s\S]*?```", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        content = System.Text.RegularExpressions.Regex.Replace(
            content, @"```followups[\s\S]*?```", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remove unclosed code blocks (AI didn't close with ```)
        content = System.Text.RegularExpressions.Regex.Replace(
            content, @"```(?:sql|json|viz|clarification|insight|followups)[\s\S]*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remove inline viz JSON that leaked without code block wrapping
        content = System.Text.RegularExpressions.Regex.Replace(
            content, @"\{""chart""\s*:\s*""[^""]*""\s*,\s*""group""\s*:[\s\S]*?""agg""\s*:\s*""[^""]*""\s*\}\s*\]\s*\}?\s*\}?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remove empty markdown headers (e.g., "**Top 5 Sales:**" followed by empty line or end)
        // These appear when JSON blocks are stripped but headers remain
        // Only match headers followed by empty line or end, not headers with content after
        content = System.Text.RegularExpressions.Regex.Replace(
            content, @"\*\*[^*]+:\*\*[ \t]*\n(?=\s*\n|\s*$)", "", System.Text.RegularExpressions.RegexOptions.Multiline);

        // Clean up extra whitespace
        content = System.Text.RegularExpressions.Regex.Replace(content, @"\n{3,}", "\n\n");

        return content.Trim();
    }

    private static ClarificationRequest? ExtractClarification(string response)
    {
        try
        {
            var clarStart = response.IndexOf("```clarification", StringComparison.OrdinalIgnoreCase);
            if (clarStart == -1) return null;

            var contentStart = response.IndexOf('\n', clarStart);
            if (contentStart == -1) return null;
            contentStart++;

            var clarEnd = response.IndexOf("```", contentStart);
            if (clarEnd == -1) return null;

            var clarJson = response.Substring(contentStart, clarEnd - contentStart).Trim();
            if (string.IsNullOrEmpty(clarJson)) return null;

            using var doc = System.Text.Json.JsonDocument.Parse(clarJson);
            var root = doc.RootElement;

            var result = new ClarificationRequest();

            if (root.TryGetProperty("question", out var questionProp))
            {
                result.Question = questionProp.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("options", out var optionsProp) &&
                optionsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var opt in optionsProp.EnumerateArray())
                {
                    var option = new ClarificationOption();
                    if (opt.TryGetProperty("label", out var labelProp))
                        option.Label = labelProp.GetString() ?? string.Empty;
                    if (opt.TryGetProperty("value", out var valueProp))
                        option.Value = valueProp.GetString() ?? string.Empty;

                    if (!string.IsNullOrEmpty(option.Label))
                        result.Options.Add(option);
                }
            }

            return result.Options.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasJsonError(string queryResult)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(queryResult);
            if (doc.RootElement.TryGetProperty("error", out var errorProp) &&
                errorProp.ValueKind == System.Text.Json.JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(errorProp.GetString()))
            {
                return true;
            }
        }
        catch
        {
            // Not valid JSON — not a JSON error
        }
        return false;
    }

    private static (bool isEmpty, bool isSuspicious, int rowCount) AnalyzeQueryResult(string? queryResultJson)
    {
        if (string.IsNullOrEmpty(queryResultJson))
            return (false, false, 0);

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(queryResultJson);
            var root = doc.RootElement;

            // Handle multi-table results
            if (root.TryGetProperty("tables", out var tables))
            {
                // Check first table
                if (tables.ValueKind == System.Text.Json.JsonValueKind.Array && tables.GetArrayLength() > 0)
                {
                    var firstTable = tables[0];
                    return AnalyzeResultElement(firstTable);
                }
                return (false, false, 0);
            }

            return AnalyzeResultElement(root);
        }
        catch
        {
            return (false, false, 0);
        }
    }

    private static (bool isEmpty, bool isSuspicious, int rowCount) AnalyzeResultElement(System.Text.Json.JsonElement element)
    {
        int rowCount = 0;

        if (element.TryGetProperty("rowCount", out var rowCountProp))
        {
            rowCount = rowCountProp.GetInt32();
        }
        else if (element.TryGetProperty("rows", out var rowsProp) &&
                 rowsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            rowCount = rowsProp.GetArrayLength();
        }

        if (rowCount == 0)
            return (true, false, 0);

        // Check for suspicious results: all values in a numeric column are NULL
        bool isSuspicious = false;
        if (element.TryGetProperty("rows", out var rows) &&
            element.TryGetProperty("columns", out var columns) &&
            rows.ValueKind == System.Text.Json.JsonValueKind.Array &&
            columns.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var col in columns.EnumerateArray())
            {
                var colName = col.GetString();
                if (string.IsNullOrEmpty(colName)) continue;

                var allNull = true;
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.TryGetProperty(colName, out var val) &&
                        val.ValueKind != System.Text.Json.JsonValueKind.Null)
                    {
                        allNull = false;
                        break;
                    }
                }

                if (allNull && rows.GetArrayLength() > 0)
                {
                    isSuspicious = true;
                    break;
                }
            }
        }

        return (false, isSuspicious, rowCount);
    }

    private static string BuildResultExplanationPrompt(string sqlQuery, string userMessage, string? schemaContext, bool isEmpty)
    {
        var situation = isEmpty
            ? "The query returned 0 rows (empty result)."
            : "The query returned suspicious results (some columns are entirely NULL).";

        var prompt = $@"You are a data analyst explaining query results. {situation}

User asked: ""{userMessage}""
SQL executed: {sqlQuery}

In 1-2 sentences:
1. Explain WHY the result is empty or suspicious (e.g., filter too restrictive, no matching data, column mismatch).
2. Suggest what the user could try instead (e.g., broaden filter, check a different column/table).

Be concise and helpful. Do not include SQL code. Do not use markdown headers.";

        if (!string.IsNullOrEmpty(schemaContext))
        {
            // Include a truncated schema for context (limit to avoid huge prompts)
            var schemaSnippet = schemaContext.Length > 500
                ? schemaContext[..500] + "\n..."
                : schemaContext;
            prompt += $"\n\nSchema context:\n{schemaSnippet}";
        }

        return prompt;
    }

    private static string BuildInsightPrompt(string userQuestion, string sqlQuery, string resultSummary)
    {
        return $@"You are a business analyst explaining query results to a non-technical business owner.

User question: {userQuestion}
SQL executed: {sqlQuery}
Result summary:
{resultSummary}

Instructions:
1. Write 2-3 sentences answering ""so what?"" in plain language. No jargon, no markdown, no bullet points. Focus on the business implication — what this means for their business, not what the numbers are.
2. Suggest exactly 3 short follow-up questions (under 10 words each) the user might naturally ask next.
3. IMPORTANT: Respond in the SAME LANGUAGE as the user question above. If the user wrote in Russian, write insight and follow-ups in Russian. If English, use English.

Format your response EXACTLY like this:
```insight
Your plain-English interpretation here.
```

```followups
Question one?
Question two?
Question three?
```";
    }

    private static string BuildResultSummary(string queryResultJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(queryResultJson);
            var root = doc.RootElement;

            // Handle multi-table format
            if (root.TryGetProperty("tables", out var tablesArray))
            {
                var sb = new System.Text.StringBuilder();
                foreach (var table in tablesArray.EnumerateArray())
                {
                    AppendTableSummary(sb, table);
                    sb.AppendLine();
                }
                return sb.ToString().Trim();
            }

            // Single table format
            var singleSb = new System.Text.StringBuilder();
            AppendTableSummary(singleSb, root);
            return singleSb.ToString().Trim();
        }
        catch
        {
            // Fallback: return truncated raw JSON
            return queryResultJson.Length > 500 ? queryResultJson[..500] + "..." : queryResultJson;
        }
    }

    private static void AppendTableSummary(System.Text.StringBuilder sb, System.Text.Json.JsonElement table)
    {
        if (table.TryGetProperty("columns", out var cols) && cols.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var columnNames = new List<string>();
            foreach (var col in cols.EnumerateArray())
            {
                columnNames.Add(col.GetString() ?? "?");
            }
            sb.AppendLine($"Columns: {string.Join(", ", columnNames)}");
        }

        if (table.TryGetProperty("rows", out var rows) && rows.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var rowCount = rows.GetArrayLength();
            sb.AppendLine($"Total rows: {rowCount}");

            var shown = 0;
            foreach (var row in rows.EnumerateArray())
            {
                if (shown >= 10) break;
                var pairs = new List<string>();
                foreach (var prop in row.EnumerateObject())
                {
                    var val = prop.Value.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.String => prop.Value.GetString(),
                        System.Text.Json.JsonValueKind.Number => prop.Value.GetRawText(),
                        System.Text.Json.JsonValueKind.True => "true",
                        System.Text.Json.JsonValueKind.False => "false",
                        System.Text.Json.JsonValueKind.Null => "null",
                        _ => prop.Value.GetRawText()
                    };
                    pairs.Add($"{prop.Name}={val}");
                }
                sb.AppendLine($"  Row {shown + 1}: {string.Join(", ", pairs)}");
                shown++;
            }
            if (rowCount > 10)
            {
                sb.AppendLine($"  ... ({rowCount - 10} more rows)");
            }
        }
    }

    private static (string? insight, List<string>? followUps) ExtractInsightAndFollowUps(string response)
    {
        string? insight = null;
        List<string>? followUps = null;

        try
        {
            // Extract ```insight block
            var insightStart = response.IndexOf("```insight", StringComparison.OrdinalIgnoreCase);
            if (insightStart != -1)
            {
                var contentStart = response.IndexOf('\n', insightStart);
                if (contentStart != -1)
                {
                    contentStart++;
                    var insightEnd = response.IndexOf("```", contentStart);
                    if (insightEnd != -1)
                    {
                        insight = response.Substring(contentStart, insightEnd - contentStart).Trim();
                        if (string.IsNullOrWhiteSpace(insight)) insight = null;
                    }
                }
            }

            // Extract ```followups block
            var followStart = response.IndexOf("```followups", StringComparison.OrdinalIgnoreCase);
            if (followStart != -1)
            {
                var contentStart = response.IndexOf('\n', followStart);
                if (contentStart != -1)
                {
                    contentStart++;
                    var followEnd = response.IndexOf("```", contentStart);
                    if (followEnd != -1)
                    {
                        var block = response.Substring(contentStart, followEnd - contentStart).Trim();
                        if (!string.IsNullOrWhiteSpace(block))
                        {
                            followUps = block
                                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                .Select(q => q.Trim().TrimStart('-', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ')').Trim())
                                .Where(q => !string.IsNullOrWhiteSpace(q))
                                .Take(3)
                                .ToList();
                            if (followUps.Count == 0) followUps = null;
                        }
                    }
                }
            }

            // Fallback: AI didn't use code blocks — parse plain text
            if (insight == null && followUps == null && !string.IsNullOrWhiteSpace(response))
            {
                // Strip any stray code blocks
                var cleaned = System.Text.RegularExpressions.Regex.Replace(response, @"```[\s\S]*?```", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    var lines = cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToList();

                    // Separate insight lines from follow-up question lines
                    var insightLines = new List<string>();
                    var questionLines = new List<string>();

                    foreach (var line in lines)
                    {
                        // Detect follow-up question lines: start with number/bullet/dash and end with ?
                        var stripped = line.TrimStart('-', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ')', ' ');
                        if (stripped.EndsWith('?') && (line.StartsWith('-') || line.StartsWith('*') || line.StartsWith("1") || line.StartsWith("2") || line.StartsWith("3")))
                        {
                            questionLines.Add(stripped);
                        }
                        // Also catch lines that are just questions (end with ?) after we've found some insight text
                        else if (stripped.EndsWith('?') && insightLines.Count > 0)
                        {
                            questionLines.Add(stripped);
                        }
                        else if (questionLines.Count == 0)
                        {
                            // Skip header-like lines (e.g., "Insight:", "Follow-up questions:")
                            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^(insight|follow[- ]?up|questions?|suggestions?)\s*:?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                                continue;
                            insightLines.Add(line);
                        }
                    }

                    if (insightLines.Count > 0)
                    {
                        insight = string.Join(" ", insightLines);
                        // Clean up any leftover markdown bold/italic
                        insight = insight.Replace("**", "").Replace("__", "");
                        if (string.IsNullOrWhiteSpace(insight)) insight = null;
                    }

                    if (questionLines.Count > 0)
                    {
                        followUps = questionLines.Take(3).ToList();
                    }
                }
            }
        }
        catch
        {
            // Parsing failed — non-critical
        }

        return (insight, followUps);
    }
}
