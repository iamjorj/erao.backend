using Erao.Core.DTOs.DataExploration;
using Erao.Core.DTOs.SmartFeatures;
using Erao.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Erao.Application.Services;

public class DataExplorationService : IDataExplorationService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IEncryptionService _encryptionService;
    private readonly IDatabaseQueryService _databaseQueryService;
    private readonly IFileQueryService _fileQueryService;
    private readonly IOllamaService _ollamaService;
    private readonly ILogger<DataExplorationService> _logger;

    public DataExplorationService(
        IUnitOfWork unitOfWork,
        IEncryptionService encryptionService,
        IDatabaseQueryService databaseQueryService,
        IFileQueryService fileQueryService,
        IOllamaService ollamaService,
        ILogger<DataExplorationService> logger)
    {
        _unitOfWork = unitOfWork;
        _encryptionService = encryptionService;
        _databaseQueryService = databaseQueryService;
        _fileQueryService = fileQueryService;
        _ollamaService = ollamaService;
        _logger = logger;
    }

    #region Database Preview & Stats

    public async Task<PreviewResultDto> GetDatabaseTablePreviewAsync(Guid databaseId, Guid userId, string tableName, int limit = 50)
    {
        var connection = await GetDatabaseConnectionAsync(databaseId, userId);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Sanitize table name to prevent SQL injection
        var safeTableName = SanitizeIdentifier(tableName);
        var query = $"SELECT * FROM {safeTableName} LIMIT {Math.Min(limit, 100)}";

        // Adjust for SQL Server
        if (connection.DatabaseType == Core.Enums.DatabaseType.SQLServer)
        {
            query = $"SELECT TOP {Math.Min(limit, 100)} * FROM [{safeTableName}]";
        }

        var result = await _databaseQueryService.ExecuteQueryAsync(
            connection.DatabaseType,
            _encryptionService.Decrypt(connection.EncryptedHost),
            int.Parse(_encryptionService.Decrypt(connection.EncryptedPort)),
            _encryptionService.Decrypt(connection.EncryptedDatabaseName),
            _encryptionService.Decrypt(connection.EncryptedUsername),
            _encryptionService.Decrypt(connection.EncryptedPassword),
            query);

        stopwatch.Stop();

        var parsed = JsonSerializer.Deserialize<JsonElement>(result);
        var columns = parsed.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToList();
        var rows = parsed.GetProperty("rows").EnumerateArray()
            .Select(r => r.EnumerateObject().ToDictionary(p => p.Name, p => GetJsonValue(p.Value)))
            .ToList();

        return new PreviewResultDto
        {
            Columns = columns,
            Rows = rows,
            RowCount = rows.Count,
            TableName = tableName,
            ExecutionTimeMs = stopwatch.ElapsedMilliseconds
        };
    }

    public async Task<ColumnStatsDto> GetDatabaseColumnStatsAsync(Guid databaseId, Guid userId, string tableName, string columnName)
    {
        var connection = await GetDatabaseConnectionAsync(databaseId, userId);

        var safeTable = SanitizeIdentifier(tableName);
        var safeColumn = SanitizeIdentifier(columnName);

        // Build stats query based on database type
        var statsQuery = connection.DatabaseType switch
        {
            Core.Enums.DatabaseType.PostgreSQL => $@"
                SELECT
                    COUNT(*) as total_count,
                    COUNT(*) - COUNT({safeColumn}) as null_count,
                    COUNT(DISTINCT {safeColumn}) as unique_count,
                    MIN({safeColumn}::text) as min_value,
                    MAX({safeColumn}::text) as max_value,
                    AVG(CASE WHEN {safeColumn}::text ~ '^[0-9]+\.?[0-9]*$' THEN {safeColumn}::numeric ELSE NULL END) as avg_value
                FROM {safeTable}",
            Core.Enums.DatabaseType.MySQL => $@"
                SELECT
                    COUNT(*) as total_count,
                    SUM(CASE WHEN {safeColumn} IS NULL THEN 1 ELSE 0 END) as null_count,
                    COUNT(DISTINCT {safeColumn}) as unique_count,
                    MIN({safeColumn}) as min_value,
                    MAX({safeColumn}) as max_value,
                    AVG(CASE WHEN {safeColumn} REGEXP '^[0-9]+\\.?[0-9]*$' THEN CAST({safeColumn} AS DECIMAL(20,4)) ELSE NULL END) as avg_value
                FROM {safeTable}",
            Core.Enums.DatabaseType.SQLServer => $@"
                SELECT
                    COUNT(*) as total_count,
                    SUM(CASE WHEN [{safeColumn}] IS NULL THEN 1 ELSE 0 END) as null_count,
                    COUNT(DISTINCT [{safeColumn}]) as unique_count,
                    MIN(CAST([{safeColumn}] AS NVARCHAR(MAX))) as min_value,
                    MAX(CAST([{safeColumn}] AS NVARCHAR(MAX))) as max_value,
                    AVG(TRY_CAST([{safeColumn}] AS FLOAT)) as avg_value
                FROM [{safeTable}]",
            _ => throw new NotSupportedException($"Database type {connection.DatabaseType} not supported for stats")
        };

        var result = await _databaseQueryService.ExecuteQueryAsync(
            connection.DatabaseType,
            _encryptionService.Decrypt(connection.EncryptedHost),
            int.Parse(_encryptionService.Decrypt(connection.EncryptedPort)),
            _encryptionService.Decrypt(connection.EncryptedDatabaseName),
            _encryptionService.Decrypt(connection.EncryptedUsername),
            _encryptionService.Decrypt(connection.EncryptedPassword),
            statsQuery);

        var parsed = JsonSerializer.Deserialize<JsonElement>(result);
        var rows = parsed.GetProperty("rows").EnumerateArray().ToList();

        if (!rows.Any())
        {
            return new ColumnStatsDto { ColumnName = columnName };
        }

        var statsRow = rows[0];
        var totalCount = GetLongValue(statsRow, "total_count");
        var nullCount = GetLongValue(statsRow, "null_count");

        // Get sample values
        var sampleQuery = connection.DatabaseType == Core.Enums.DatabaseType.SQLServer
            ? $"SELECT DISTINCT TOP 10 [{safeColumn}] FROM [{safeTable}] WHERE [{safeColumn}] IS NOT NULL"
            : $"SELECT DISTINCT {safeColumn} FROM {safeTable} WHERE {safeColumn} IS NOT NULL LIMIT 10";

        var sampleResult = await _databaseQueryService.ExecuteQueryAsync(
            connection.DatabaseType,
            _encryptionService.Decrypt(connection.EncryptedHost),
            int.Parse(_encryptionService.Decrypt(connection.EncryptedPort)),
            _encryptionService.Decrypt(connection.EncryptedDatabaseName),
            _encryptionService.Decrypt(connection.EncryptedUsername),
            _encryptionService.Decrypt(connection.EncryptedPassword),
            sampleQuery);

        var sampleParsed = JsonSerializer.Deserialize<JsonElement>(sampleResult);
        var sampleValues = sampleParsed.GetProperty("rows").EnumerateArray()
            .Select(r => r.EnumerateObject().FirstOrDefault().Value)
            .Select(v => GetJsonValue(v))
            .ToList();

        // Get data type from schema
        var schemas = await _databaseQueryService.GetStructuredSchemaAsync(
            connection.DatabaseType,
            _encryptionService.Decrypt(connection.EncryptedHost),
            int.Parse(_encryptionService.Decrypt(connection.EncryptedPort)),
            _encryptionService.Decrypt(connection.EncryptedDatabaseName),
            _encryptionService.Decrypt(connection.EncryptedUsername),
            _encryptionService.Decrypt(connection.EncryptedPassword));

        var dataType = schemas.FirstOrDefault(t => t.Name == tableName)
            ?.Columns.FirstOrDefault(c => c.Name == columnName)?.DataType ?? "unknown";

        return new ColumnStatsDto
        {
            ColumnName = columnName,
            DataType = dataType,
            TotalCount = totalCount,
            NullCount = nullCount,
            NullPercentage = totalCount > 0 ? Math.Round((double)nullCount / totalCount * 100, 2) : 0,
            UniqueCount = GetLongValue(statsRow, "unique_count"),
            MinValue = GetJsonValue(statsRow.GetProperty("min_value")),
            MaxValue = GetJsonValue(statsRow.GetProperty("max_value")),
            AvgValue = GetDoubleValue(statsRow, "avg_value"),
            SampleValues = sampleValues
        };
    }

    #endregion

    #region File Preview & Stats

    public async Task<PreviewResultDto> GetFilePreviewAsync(Guid fileId, Guid userId, int limit = 50)
    {
        var file = await GetFileDocumentAsync(fileId, userId);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await _fileQueryService.GetPreviewDataAsync(file.Id, Math.Min(limit, 100));
        stopwatch.Stop();

        var parsed = JsonSerializer.Deserialize<JsonElement>(result);
        var columns = parsed.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToList();
        var rows = parsed.GetProperty("rows").EnumerateArray()
            .Select(r => r.EnumerateObject().ToDictionary(p => p.Name, p => GetJsonValue(p.Value)))
            .ToList();

        return new PreviewResultDto
        {
            Columns = columns,
            Rows = rows,
            RowCount = rows.Count,
            TableName = file.FileName,
            ExecutionTimeMs = stopwatch.ElapsedMilliseconds
        };
    }

    public async Task<ColumnStatsDto> GetFileColumnStatsAsync(Guid fileId, Guid userId, string columnName)
    {
        var file = await GetFileDocumentAsync(fileId, userId);
        var result = await _fileQueryService.GetColumnStatsAsync(file.Id, columnName);

        var parsed = JsonSerializer.Deserialize<JsonElement>(result);

        return new ColumnStatsDto
        {
            ColumnName = columnName,
            DataType = parsed.TryGetProperty("dataType", out var dt) ? dt.GetString() ?? "unknown" : "unknown",
            TotalCount = parsed.TryGetProperty("totalCount", out var tc) ? tc.GetInt64() : 0,
            NullCount = parsed.TryGetProperty("nullCount", out var nc) ? nc.GetInt64() : 0,
            NullPercentage = parsed.TryGetProperty("nullPercentage", out var np) ? np.GetDouble() : 0,
            UniqueCount = parsed.TryGetProperty("uniqueCount", out var uc) ? uc.GetInt64() : 0,
            MinValue = parsed.TryGetProperty("minValue", out var minV) ? GetJsonValue(minV) : null,
            MaxValue = parsed.TryGetProperty("maxValue", out var maxV) ? GetJsonValue(maxV) : null,
            AvgValue = parsed.TryGetProperty("avgValue", out var avgV) && avgV.ValueKind == JsonValueKind.Number ? avgV.GetDouble() : null,
            SampleValues = parsed.TryGetProperty("sampleValues", out var sv)
                ? sv.EnumerateArray().Select(v => GetJsonValue(v)).ToList()
                : new List<object?>()
        };
    }

    #endregion

    #region Smart Features - Suggestions

    public async Task<List<SuggestedQueryDto>> GetDatabaseSuggestionsAsync(Guid databaseId, Guid userId, string? tableName = null)
    {
        var connection = await GetDatabaseConnectionAsync(databaseId, userId);

        var schemas = await _databaseQueryService.GetStructuredSchemaAsync(
            connection.DatabaseType,
            _encryptionService.Decrypt(connection.EncryptedHost),
            int.Parse(_encryptionService.Decrypt(connection.EncryptedPort)),
            _encryptionService.Decrypt(connection.EncryptedDatabaseName),
            _encryptionService.Decrypt(connection.EncryptedUsername),
            _encryptionService.Decrypt(connection.EncryptedPassword));

        var suggestions = new List<SuggestedQueryDto>();
        var targetTables = tableName != null
            ? schemas.Where(t => t.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase)).ToList()
            : schemas.Take(5).ToList();

        foreach (var table in targetTables)
        {
            // Basic row count
            suggestions.Add(new SuggestedQueryDto
            {
                Title = $"Count {table.Name} records",
                Description = $"Get total number of rows in {table.Name}",
                Query = $"SELECT COUNT(*) as total FROM {table.Name}",
                Category = "Aggregation"
            });

            // Find numeric columns for aggregations
            var numericColumns = table.Columns
                .Where(c => IsNumericType(c.DataType))
                .Take(2)
                .ToList();

            foreach (var col in numericColumns)
            {
                suggestions.Add(new SuggestedQueryDto
                {
                    Title = $"Sum of {col.Name}",
                    Description = $"Calculate total {col.Name} in {table.Name}",
                    Query = $"SELECT SUM({col.Name}) as total_{col.Name} FROM {table.Name}",
                    Category = "Aggregation"
                });
            }

            // Find date columns for trends
            var dateColumns = table.Columns
                .Where(c => IsDateType(c.DataType))
                .Take(1)
                .ToList();

            if (dateColumns.Any() && numericColumns.Any())
            {
                var dateCol = dateColumns[0];
                var numCol = numericColumns[0];
                suggestions.Add(new SuggestedQueryDto
                {
                    Title = $"Monthly {numCol.Name} trend",
                    Description = $"View {numCol.Name} aggregated by month",
                    Query = connection.DatabaseType == Core.Enums.DatabaseType.SQLServer
                        ? $"SELECT YEAR({dateCol.Name}) as year, MONTH({dateCol.Name}) as month, SUM({numCol.Name}) as total FROM {table.Name} GROUP BY YEAR({dateCol.Name}), MONTH({dateCol.Name}) ORDER BY year, month"
                        : $"SELECT DATE_TRUNC('month', {dateCol.Name}) as month, SUM({numCol.Name}) as total FROM {table.Name} GROUP BY DATE_TRUNC('month', {dateCol.Name}) ORDER BY month",
                    Category = "Trend"
                });
            }

            // Top N query
            if (numericColumns.Any())
            {
                var numCol = numericColumns[0];
                var labelCol = table.Columns.FirstOrDefault(c => !IsNumericType(c.DataType) && !IsDateType(c.DataType));
                if (labelCol != null)
                {
                    suggestions.Add(new SuggestedQueryDto
                    {
                        Title = $"Top 10 by {numCol.Name}",
                        Description = $"Find top 10 {labelCol.Name} by {numCol.Name}",
                        Query = connection.DatabaseType == Core.Enums.DatabaseType.SQLServer
                            ? $"SELECT TOP 10 {labelCol.Name}, {numCol.Name} FROM {table.Name} ORDER BY {numCol.Name} DESC"
                            : $"SELECT {labelCol.Name}, {numCol.Name} FROM {table.Name} ORDER BY {numCol.Name} DESC LIMIT 10",
                        Category = "Top N"
                    });
                }
            }
        }

        return suggestions.Take(8).ToList();
    }

    public async Task<List<SuggestedQueryDto>> GetFileSuggestionsAsync(Guid fileId, Guid userId)
    {
        var file = await GetFileDocumentAsync(fileId, userId);
        var suggestions = new List<SuggestedQueryDto>();

        // Use AI to generate suggestions based on file schema
        var schemaJson = await _fileQueryService.GetSchemaAsync(file.Id);

        try
        {
            var prompt = $@"Given this file schema, suggest 5 useful analytical questions a user might want to ask:

Schema: {schemaJson}

Return a JSON array with objects containing: title, description, category (one of: Aggregation, Trend, Filter, Summary)

Example format:
[
  {{""title"": ""Total Sales"", ""description"": ""Sum of all sales amounts"", ""category"": ""Aggregation""}},
]

Return ONLY the JSON array, no other text.";

            var aiResponse = await _ollamaService.GenerateResponseAsync(prompt, "");

            var parsed = JsonSerializer.Deserialize<List<JsonElement>>(aiResponse);
            if (parsed != null)
            {
                foreach (var item in parsed.Take(6))
                {
                    suggestions.Add(new SuggestedQueryDto
                    {
                        Title = item.GetProperty("title").GetString() ?? "",
                        Description = item.GetProperty("description").GetString() ?? "",
                        Query = "", // User asks in natural language
                        Category = item.TryGetProperty("category", out var cat) ? cat.GetString() ?? "Analysis" : "Analysis"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate AI suggestions for file {FileId}", fileId);
            // Return default suggestions
            suggestions.Add(new SuggestedQueryDto
            {
                Title = "Show summary statistics",
                Description = "Get basic statistics for numeric columns",
                Category = "Summary"
            });
        }

        return suggestions;
    }

    #endregion

    #region Smart Features - Insights

    public async Task<List<InsightDto>> GetDatabaseInsightsAsync(Guid databaseId, Guid userId, string tableName)
    {
        var connection = await GetDatabaseConnectionAsync(databaseId, userId);
        var insights = new List<InsightDto>();

        var schemas = await _databaseQueryService.GetStructuredSchemaAsync(
            connection.DatabaseType,
            _encryptionService.Decrypt(connection.EncryptedHost),
            int.Parse(_encryptionService.Decrypt(connection.EncryptedPort)),
            _encryptionService.Decrypt(connection.EncryptedDatabaseName),
            _encryptionService.Decrypt(connection.EncryptedUsername),
            _encryptionService.Decrypt(connection.EncryptedPassword));

        var table = schemas.FirstOrDefault(t => t.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase));
        if (table == null) return insights;

        // Check for high null percentages
        foreach (var column in table.Columns.Where(c => !c.IsPrimaryKey).Take(5))
        {
            try
            {
                var stats = await GetDatabaseColumnStatsAsync(databaseId, userId, tableName, column.Name);
                if (stats.NullPercentage > 20)
                {
                    insights.Add(new InsightDto
                    {
                        Type = "data_quality",
                        Title = $"High null rate in {column.Name}",
                        Description = $"{stats.NullPercentage:F1}% of values are null in column {column.Name}",
                        Severity = stats.NullPercentage > 50 ? "warning" : "info",
                        Metadata = new Dictionary<string, object?>
                        {
                            ["column"] = column.Name,
                            ["nullPercentage"] = stats.NullPercentage
                        }
                    });
                }

                // Check for low cardinality (might be categorical)
                if (stats.UniqueCount > 0 && stats.UniqueCount <= 20 && stats.TotalCount > 100)
                {
                    insights.Add(new InsightDto
                    {
                        Type = "pattern",
                        Title = $"{column.Name} appears categorical",
                        Description = $"Only {stats.UniqueCount} unique values across {stats.TotalCount} rows",
                        Severity = "info",
                        Query = $"SELECT {column.Name}, COUNT(*) as count FROM {tableName} GROUP BY {column.Name} ORDER BY count DESC",
                        Metadata = new Dictionary<string, object?>
                        {
                            ["uniqueCount"] = stats.UniqueCount,
                            ["sampleValues"] = stats.SampleValues
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get stats for column {Column} in table {Table}", column.Name, tableName);
            }
        }

        // Check row count
        if (table.RowCount.HasValue)
        {
            if (table.RowCount == 0)
            {
                insights.Add(new InsightDto
                {
                    Type = "data_quality",
                    Title = "Empty table",
                    Description = $"Table {tableName} has no data",
                    Severity = "warning"
                });
            }
            else if (table.RowCount > 1000000)
            {
                insights.Add(new InsightDto
                {
                    Type = "performance",
                    Title = "Large table",
                    Description = $"Table {tableName} has {table.RowCount:N0} rows. Consider using filters for better performance.",
                    Severity = "info",
                    Metadata = new Dictionary<string, object?> { ["rowCount"] = table.RowCount }
                });
            }
        }

        return insights.Take(10).ToList();
    }

    public async Task<List<InsightDto>> GetFileInsightsAsync(Guid fileId, Guid userId)
    {
        var file = await GetFileDocumentAsync(fileId, userId);
        var insights = new List<InsightDto>();

        try
        {
            var statsJson = await _fileQueryService.GetFileStatsAsync(file.Id);
            var stats = JsonSerializer.Deserialize<JsonElement>(statsJson);

            if (stats.TryGetProperty("rowCount", out var rowCount) && rowCount.GetInt64() == 0)
            {
                insights.Add(new InsightDto
                {
                    Type = "data_quality",
                    Title = "Empty file",
                    Description = "This file contains no data rows",
                    Severity = "warning"
                });
            }

            if (stats.TryGetProperty("columns", out var columns))
            {
                foreach (var col in columns.EnumerateArray())
                {
                    var colName = col.GetProperty("name").GetString();
                    if (col.TryGetProperty("nullPercentage", out var np) && np.GetDouble() > 30)
                    {
                        insights.Add(new InsightDto
                        {
                            Type = "data_quality",
                            Title = $"High null rate in {colName}",
                            Description = $"{np.GetDouble():F1}% of values are missing",
                            Severity = "info"
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to analyze file {FileId} for insights", fileId);
        }

        return insights;
    }

    #endregion

    #region AI-Powered Visualization

    public async Task<VisualizationRecommendationDto> AnalyzeForVisualizationAsync(AnalyzeVisualizationRequest request)
    {
        var result = new VisualizationRecommendationDto();

        try
        {
            // Step 1: Analyze column metadata locally (fast)
            var columnMetadata = AnalyzeColumns(request.Columns, request.SampleRows);
            result.ColumnMetadata = columnMetadata;

            // Step 2: Use AI to get smart recommendations
            var aiRecommendation = await GetAIVisualizationRecommendationAsync(request, columnMetadata);

            // Merge AI recommendations with local analysis
            result.RecommendedChartType = aiRecommendation.RecommendedChartType;
            result.ChartTypeReason = aiRecommendation.ChartTypeReason;
            result.GroupByColumn = aiRecommendation.GroupByColumn;
            result.ValueColumns = aiRecommendation.ValueColumns;
            result.Alternatives = aiRecommendation.Alternatives;
            result.Insights = aiRecommendation.Insights;
            result.ShouldAggregate = aiRecommendation.ShouldAggregate;
            result.SuggestedTitle = aiRecommendation.SuggestedTitle;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI visualization analysis failed, using fallback logic");
            result = GetFallbackRecommendation(request);
        }

        return result;
    }

    private List<ColumnMetadata> AnalyzeColumns(List<string> columns, List<Dictionary<string, object?>> sampleRows)
    {
        var metadata = new List<ColumnMetadata>();

        foreach (var col in columns)
        {
            var values = sampleRows
                .Select(r => r.TryGetValue(col, out var v) ? v : null)
                .Where(v => v != null)
                .ToList();

            var uniqueValues = values.Select(v => v?.ToString() ?? "").Distinct().ToList();
            var nullCount = sampleRows.Count - values.Count;

            // Detect data type
            var dataType = DetectDataType(values);
            var isNumeric = dataType == "number";

            // Detect semantic type
            var semanticType = DetectSemanticType(col, values, dataType);

            // Is categorical? (few unique values relative to total)
            var isCategorical = uniqueValues.Count >= 2 &&
                                uniqueValues.Count <= 30 &&
                                uniqueValues.Count < sampleRows.Count * 0.5;

            metadata.Add(new ColumnMetadata
            {
                Name = col,
                DataType = dataType,
                SemanticType = semanticType,
                UniqueCount = uniqueValues.Count,
                NullCount = nullCount,
                IsNumeric = isNumeric,
                IsCategorical = isCategorical,
                SampleValues = isCategorical ? uniqueValues.Take(10).ToList() : new List<string>()
            });
        }

        return metadata;
    }

    private string DetectDataType(List<object?> values)
    {
        if (!values.Any()) return "string";

        var nonNull = values.Where(v => v != null).ToList();
        if (!nonNull.Any()) return "string";

        // Check if all values are numeric
        var allNumeric = nonNull.All(v =>
        {
            if (v is int or long or float or double or decimal) return true;
            return double.TryParse(v?.ToString(), out _);
        });
        if (allNumeric) return "number";

        // Check if all values are dates
        var allDates = nonNull.All(v =>
        {
            if (v is DateTime) return true;
            return DateTime.TryParse(v?.ToString(), out _);
        });
        if (allDates) return "datetime";

        // Check if all values are booleans
        var allBool = nonNull.All(v =>
        {
            if (v is bool) return true;
            var str = v?.ToString()?.ToLower();
            return str == "true" || str == "false" || str == "yes" || str == "no" || str == "1" || str == "0";
        });
        if (allBool) return "boolean";

        return "string";
    }

    private string DetectSemanticType(string columnName, List<object?> values, string dataType)
    {
        var name = columnName.ToLower();

        // ID patterns
        if (name.EndsWith("_id") || name.EndsWith("id") || name == "id" || name.Contains("identifier"))
            return "id";

        // Name patterns
        if (name.Contains("name") || name.Contains("title") || name.Contains("label"))
            return "name";

        // Category patterns
        if (name.Contains("category") || name.Contains("type") || name.Contains("status") ||
            name.Contains("gender") || name.Contains("class") || name.Contains("group") ||
            name.Contains("department") || name.Contains("region") || name.Contains("country"))
            return "category";

        // Date patterns
        if (name.Contains("date") || name.Contains("time") || name.Contains("created") ||
            name.Contains("updated") || name.Contains("timestamp") || dataType == "datetime")
            return "datetime";

        // Currency patterns
        if (name.Contains("price") || name.Contains("cost") || name.Contains("amount") ||
            name.Contains("salary") || name.Contains("revenue") || name.Contains("total") ||
            name.Contains("fee") || name.Contains("payment") || name.Contains("money"))
            return "currency";

        // Percentage patterns
        if (name.Contains("percent") || name.Contains("rate") || name.Contains("ratio") ||
            name.Contains("pct") || name.Contains("%"))
            return "percentage";

        // Count patterns
        if (name.Contains("count") || name.Contains("quantity") || name.Contains("qty") ||
            name.Contains("number") || name.Contains("num_"))
            return "count";

        // If numeric but not categorized above, it's a measure
        if (dataType == "number")
            return "measure";

        // Default to dimension for strings
        return dataType == "string" ? "dimension" : "text";
    }

    private async Task<VisualizationRecommendationDto> GetAIVisualizationRecommendationAsync(
        AnalyzeVisualizationRequest request,
        List<ColumnMetadata> columnMetadata)
    {
        // Build a concise prompt for the AI
        var columnSummary = string.Join("\n", columnMetadata.Select(c =>
            $"- {c.Name}: {c.SemanticType} ({c.DataType}), {c.UniqueCount} unique values" +
            (c.IsCategorical ? $", categorical: [{string.Join(", ", c.SampleValues.Take(5))}]" : "")));

        var prompt = $@"Analyze this data for visualization and return a JSON recommendation.

COLUMNS:
{columnSummary}

DATA INFO:
- Total rows: {request.TotalRowCount}
- Sample rows: {request.SampleRows.Count}
{(request.UserQuestion != null ? $"- User question: {request.UserQuestion}" : "")}
{(request.SqlQuery != null ? $"- SQL query: {request.SqlQuery}" : "")}

Return ONLY a valid JSON object (no markdown, no explanation) with this exact structure:
{{
  ""recommendedChartType"": ""bar|line|pie|area|table"",
  ""chartTypeReason"": ""brief reason for chart choice"",
  ""groupByColumn"": ""column_name or null"",
  ""shouldAggregate"": true/false,
  ""suggestedTitle"": ""descriptive title for the chart"",
  ""valueColumns"": [
    {{
      ""column"": ""column_name"",
      ""aggregation"": ""COUNT|SUM|AVG|MIN|MAX|NONE"",
      ""reason"": ""why this aggregation"",
      ""displayName"": ""readable name"",
      ""formatHint"": ""number|currency|percentage|decimal""
    }}
  ],
  ""alternatives"": [
    {{
      ""chartType"": ""type"",
      ""groupByColumn"": ""column or null"",
      ""description"": ""what this shows"",
      ""valueColumns"": [""col1"", ""col2""]
    }}
  ],
  ""insights"": [
    {{
      ""type"": ""distribution|trend|outlier|correlation|cardinality|quality"",
      ""description"": ""insight description"",
      ""importance"": ""low|medium|high"",
      ""relatedColumns"": [""col1""]
    }}
  ]
}}

RULES:
1. For categorical data with few unique values (2-10), prefer pie or bar charts
2. For time series or ordered data, prefer line or area charts
3. For comparing multiple measures, prefer bar charts
4. Use aggregation when grouping makes sense (SUM for amounts, AVG for rates, COUNT for IDs)
5. Don't aggregate if data is already aggregated or has unique rows
6. Consider the user's question when choosing visualization
7. Provide 2-3 meaningful alternative visualizations
8. Provide 2-3 data insights (patterns, quality issues, etc.)";

        var aiResponse = await _ollamaService.GenerateResponseAsync(prompt,
            "You are a data visualization expert. Analyze data and recommend optimal chart types and configurations. Return ONLY valid JSON.");

        // Parse the AI response
        try
        {
            // Clean the response (remove markdown code blocks if present)
            var cleanedResponse = aiResponse
                .Replace("```json", "")
                .Replace("```", "")
                .Trim();

            var parsed = JsonSerializer.Deserialize<JsonElement>(cleanedResponse);

            var result = new VisualizationRecommendationDto
            {
                RecommendedChartType = GetStringProperty(parsed, "recommendedChartType", "bar"),
                ChartTypeReason = GetStringProperty(parsed, "chartTypeReason", ""),
                GroupByColumn = GetStringProperty(parsed, "groupByColumn", null),
                ShouldAggregate = GetBoolProperty(parsed, "shouldAggregate", true),
                SuggestedTitle = GetStringProperty(parsed, "suggestedTitle", null),
                ColumnMetadata = columnMetadata
            };

            // Parse value columns
            if (parsed.TryGetProperty("valueColumns", out var valueColumns))
            {
                foreach (var vc in valueColumns.EnumerateArray())
                {
                    result.ValueColumns.Add(new ValueColumnRecommendation
                    {
                        Column = GetStringProperty(vc, "column", ""),
                        Aggregation = GetStringProperty(vc, "aggregation", "COUNT"),
                        Reason = GetStringProperty(vc, "reason", ""),
                        DisplayName = GetStringProperty(vc, "displayName", ""),
                        FormatHint = GetStringProperty(vc, "formatHint", "number")
                    });
                }
            }

            // Parse alternatives
            if (parsed.TryGetProperty("alternatives", out var alternatives))
            {
                foreach (var alt in alternatives.EnumerateArray())
                {
                    var altVis = new AlternativeVisualization
                    {
                        ChartType = GetStringProperty(alt, "chartType", "bar"),
                        GroupByColumn = GetStringProperty(alt, "groupByColumn", null),
                        Description = GetStringProperty(alt, "description", "")
                    };

                    if (alt.TryGetProperty("valueColumns", out var altCols))
                    {
                        altVis.ValueColumns = altCols.EnumerateArray()
                            .Select(c => c.GetString() ?? "")
                            .Where(s => !string.IsNullOrEmpty(s))
                            .ToList();
                    }

                    result.Alternatives.Add(altVis);
                }
            }

            // Parse insights
            if (parsed.TryGetProperty("insights", out var insights))
            {
                foreach (var ins in insights.EnumerateArray())
                {
                    var insight = new DataInsight
                    {
                        Type = GetStringProperty(ins, "type", "pattern"),
                        Description = GetStringProperty(ins, "description", ""),
                        Importance = GetStringProperty(ins, "importance", "medium")
                    };

                    if (ins.TryGetProperty("relatedColumns", out var relCols))
                    {
                        insight.RelatedColumns = relCols.EnumerateArray()
                            .Select(c => c.GetString() ?? "")
                            .Where(s => !string.IsNullOrEmpty(s))
                            .ToList();
                    }

                    result.Insights.Add(insight);
                }
            }

            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse AI response: {Response}", aiResponse);
            throw;
        }
    }

    private VisualizationRecommendationDto GetFallbackRecommendation(AnalyzeVisualizationRequest request)
    {
        var columnMetadata = AnalyzeColumns(request.Columns, request.SampleRows);
        var result = new VisualizationRecommendationDto
        {
            ColumnMetadata = columnMetadata
        };

        // Find best grouping column (categorical)
        var categoryColumn = columnMetadata
            .FirstOrDefault(c => c.IsCategorical && c.SemanticType == "category") ??
            columnMetadata.FirstOrDefault(c => c.IsCategorical);

        // Find numeric columns for values
        var valueColumns = columnMetadata
            .Where(c => c.IsNumeric && c.SemanticType != "id")
            .ToList();

        // Find date column
        var dateColumn = columnMetadata.FirstOrDefault(c => c.SemanticType == "datetime");

        // Determine chart type
        if (categoryColumn != null && categoryColumn.UniqueCount <= 8)
        {
            result.RecommendedChartType = "pie";
            result.ChartTypeReason = $"'{categoryColumn.Name}' has {categoryColumn.UniqueCount} categories, ideal for pie chart";
        }
        else if (dateColumn != null && valueColumns.Any())
        {
            result.RecommendedChartType = "line";
            result.ChartTypeReason = "Time series data detected, showing trend over time";
            result.GroupByColumn = dateColumn.Name;
        }
        else if (categoryColumn != null)
        {
            result.RecommendedChartType = "bar";
            result.ChartTypeReason = $"Comparing values across '{categoryColumn.Name}' categories";
            result.GroupByColumn = categoryColumn.Name;
        }
        else
        {
            result.RecommendedChartType = "bar";
            result.ChartTypeReason = "Default bar chart for data comparison";
        }

        // Set group by column
        result.GroupByColumn ??= categoryColumn?.Name ?? columnMetadata.FirstOrDefault()?.Name;

        // Set value columns with smart aggregations
        foreach (var vc in valueColumns.Take(3))
        {
            var agg = vc.SemanticType switch
            {
                "currency" or "count" => "SUM",
                "percentage" or "measure" => "AVG",
                "id" => "COUNT",
                _ => "SUM"
            };

            result.ValueColumns.Add(new ValueColumnRecommendation
            {
                Column = vc.Name,
                Aggregation = agg,
                Reason = $"{vc.SemanticType} column, using {agg}",
                DisplayName = $"{agg} of {vc.Name}",
                FormatHint = vc.SemanticType == "currency" ? "currency" :
                             vc.SemanticType == "percentage" ? "percentage" : "number"
            });
        }

        // If no value columns, use COUNT
        if (!result.ValueColumns.Any())
        {
            result.ValueColumns.Add(new ValueColumnRecommendation
            {
                Column = "*",
                Aggregation = "COUNT",
                Reason = "Counting records per category",
                DisplayName = "Count",
                FormatHint = "number"
            });
        }

        result.ShouldAggregate = categoryColumn != null;
        result.SuggestedTitle = categoryColumn != null
            ? $"Data by {categoryColumn.Name}"
            : "Data Overview";

        return result;
    }

    private static string? GetStringProperty(JsonElement element, string propertyName, string? defaultValue)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return defaultValue;
    }

    private static bool GetBoolProperty(JsonElement element, string propertyName, bool defaultValue)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
        }
        return defaultValue;
    }

    #endregion

    #region Helper Methods

    private async Task<Core.Entities.DatabaseConnection> GetDatabaseConnectionAsync(Guid databaseId, Guid userId)
    {
        var connection = await _unitOfWork.DatabaseConnections.GetByIdAsync(databaseId);
        if (connection == null || connection.UserId != userId)
        {
            throw new InvalidOperationException("Database connection not found");
        }
        return connection;
    }

    private async Task<Core.Entities.FileDocument> GetFileDocumentAsync(Guid fileId, Guid userId)
    {
        var file = await _unitOfWork.FileDocuments.GetByIdAsync(fileId);
        if (file == null || file.UserId != userId)
        {
            throw new InvalidOperationException("File not found");
        }
        return file;
    }

    private static string SanitizeIdentifier(string identifier)
    {
        // Remove any characters that aren't alphanumeric or underscore
        return new string(identifier.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
    }

    private static object? GetJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => element.ToString()
        };
    }

    private static long GetLongValue(JsonElement row, string propertyName)
    {
        if (row.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number)
            {
                return prop.GetInt64();
            }
            if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out var val))
            {
                return val;
            }
        }
        return 0;
    }

    private static double? GetDoubleValue(JsonElement row, string propertyName)
    {
        if (row.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number)
            {
                return prop.GetDouble();
            }
            if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), out var val))
            {
                return val;
            }
        }
        return null;
    }

    private static bool IsNumericType(string dataType)
    {
        var lower = dataType.ToLower();
        return lower.Contains("int") || lower.Contains("decimal") || lower.Contains("numeric") ||
               lower.Contains("float") || lower.Contains("double") || lower.Contains("money") ||
               lower.Contains("real") || lower.Contains("number");
    }

    private static bool IsDateType(string dataType)
    {
        var lower = dataType.ToLower();
        return lower.Contains("date") || lower.Contains("time") || lower.Contains("timestamp");
    }

    #endregion
}
