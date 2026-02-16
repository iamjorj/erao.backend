using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DuckDB.NET.Data;
using Erao.Core.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Erao.Infrastructure.Services;

public class FileQueryService : IFileQueryService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<FileQueryService> _logger;
    private const string TableName = "data";

    // S3/R2 config for DuckDB httpfs
    private readonly string _s3Endpoint;
    private readonly string _s3AccessKey;
    private readonly string _s3SecretKey;
    private readonly string _s3Region;
    private readonly string _s3BucketName;
    private readonly bool _s3UseSSL;

    public FileQueryService(IUnitOfWork unitOfWork, ILogger<FileQueryService> logger, IConfiguration configuration)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;

        // Read S3/R2 credentials from Minio config (same credentials, S3-compatible)
        _s3Endpoint = configuration["Minio:Endpoint"] ?? "localhost:9000";
        _s3AccessKey = configuration["Minio:AccessKey"] ?? "minioadmin";
        _s3SecretKey = configuration["Minio:SecretKey"] ?? "minioadmin";
        _s3Region = configuration["Minio:Region"] ?? "auto";
        _s3BucketName = configuration["Minio:BucketName"] ?? "erao-files";
        _s3UseSSL = configuration.GetValue<bool>("Minio:UseSSL", false);
    }

    /// <summary>
    /// Configures a DuckDB connection with httpfs extension and S3/R2 credentials.
    /// </summary>
    private async Task ConfigureDuckDBForS3Async(DuckDBConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"
            INSTALL httpfs;
            LOAD httpfs;
            SET s3_endpoint = '{_s3Endpoint.Replace("'", "''")}';
            SET s3_access_key_id = '{_s3AccessKey.Replace("'", "''")}';
            SET s3_secret_access_key = '{_s3SecretKey.Replace("'", "''")}';
            SET s3_region = '{_s3Region.Replace("'", "''")}';
            SET s3_url_style = 'path';
            SET s3_use_ssl = {(_s3UseSSL ? "true" : "false")};
        ";
        await cmd.ExecuteNonQueryAsync();
        _logger.LogDebug("DuckDB httpfs configured for S3 endpoint: {Endpoint}", _s3Endpoint);
    }

    /// <summary>
    /// Builds the S3 URL for a Parquet file stored in R2/MinIO.
    /// ParquetStoragePath stores the object key (e.g., "parquet/{userId}/{fileId}.parquet").
    /// </summary>
    private string BuildS3ParquetUrl(string objectKey)
    {
        return $"s3://{_s3BucketName}/{objectKey}";
    }

    // ─── DuckDB Parquet query methods ───────────────────────────────────

    public async Task<string> ExecuteQueryForFileAsync(Guid fileId, string query)
    {
        var file = await _unitOfWork.FileDocuments.GetByIdAsync(fileId);
        if (file == null)
            return JsonSerializer.Serialize(new { error = "File not found", columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 });

        _logger.LogInformation("[DEBUG] ExecuteQueryForFileAsync: FileId={FileId}, UsesParquet={UsesParquet}, ParquetPath={ParquetPath}, RowCount={RowCount}, TotalRowCount={TotalRowCount}",
            fileId, file.UsesParquet, file.ParquetStoragePath ?? "null", file.RowCount, file.TotalRowCount);
        _logger.LogInformation("[DEBUG] Query: {Query}", query);

        // Route to Parquet (DuckDB via S3/R2) or legacy (SQLite) path
        if (file.UsesParquet && !string.IsNullOrEmpty(file.ParquetStoragePath))
        {
            _logger.LogInformation("[DEBUG] Routing to Parquet via S3. ObjectKey={ObjectKey}", file.ParquetStoragePath);
            return await ExecuteQueryViaParquetAsync(file.ParquetStoragePath, query);
        }

        // Legacy SQLite path
        if (string.IsNullOrEmpty(file.ParsedContent) || string.IsNullOrEmpty(file.SchemaInfo))
            return JsonSerializer.Serialize(new { error = "No parsed content available", columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 });

        return await ExecuteQueryAsync(file.ParsedContent, file.SchemaInfo, query);
    }

    public async Task<List<string>> ExecuteQueriesForFileAsync(Guid fileId, List<string> queries)
    {
        var file = await _unitOfWork.FileDocuments.GetByIdAsync(fileId);
        if (file == null)
        {
            var errorJson = JsonSerializer.Serialize(new { error = "File not found", columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 });
            return queries.Select(_ => errorJson).ToList();
        }

        // Route to Parquet (DuckDB) or legacy (SQLite) path
        if (file.UsesParquet && !string.IsNullOrEmpty(file.ParquetStoragePath))
        {
            return await ExecuteQueriesViaParquetAsync(file.ParquetStoragePath, queries);
        }

        // Legacy SQLite path
        if (string.IsNullOrEmpty(file.ParsedContent) || string.IsNullOrEmpty(file.SchemaInfo))
        {
            var errorJson = JsonSerializer.Serialize(new { error = "No parsed content available", columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 });
            return queries.Select(_ => errorJson).ToList();
        }

        return await ExecuteQueriesAsync(file.ParsedContent, file.SchemaInfo, queries);
    }

    private async Task<string> ExecuteQueryViaParquetAsync(string parquetObjectKey, string query)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();

            // Configure httpfs for S3/R2 access
            await ConfigureDuckDBForS3Async(connection);

            // Create a view named "data" pointing to the Parquet file in S3/R2
            var s3Url = BuildS3ParquetUrl(parquetObjectKey);
            await using (var viewCmd = connection.CreateCommand())
            {
                viewCmd.CommandText = $"CREATE VIEW \"data\" AS SELECT * FROM read_parquet('{s3Url.Replace("'", "''")}')";
                await viewCmd.ExecuteNonQueryAsync();
            }

            _logger.LogInformation("DuckDB Parquet view created for S3: {Url}", s3Url);

            return await ExecuteDuckDBQueryAsync(connection, query, stopwatch);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DuckDB S3 Parquet query failed: {Query}", query);
            return JsonSerializer.Serialize(new { error = $"Query error: {ex.Message}", columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 });
        }
    }

    private async Task<List<string>> ExecuteQueriesViaParquetAsync(string parquetObjectKey, List<string> queries)
    {
        var results = new List<string>();

        try
        {
            await using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();

            await ConfigureDuckDBForS3Async(connection);

            var s3Url = BuildS3ParquetUrl(parquetObjectKey);
            await using (var viewCmd = connection.CreateCommand())
            {
                viewCmd.CommandText = $"CREATE VIEW \"data\" AS SELECT * FROM read_parquet('{s3Url.Replace("'", "''")}')";
                await viewCmd.ExecuteNonQueryAsync();
            }

            foreach (var query in queries)
            {
                try
                {
                    results.Add(await ExecuteDuckDBQueryAsync(connection, query, Stopwatch.StartNew()));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DuckDB Parquet query failed: {Query}", query);
                    results.Add(JsonSerializer.Serialize(new { error = $"Query error: {ex.Message}", columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 }));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DuckDB connection failed for Parquet queries");
            var errorJson = JsonSerializer.Serialize(new { error = $"Connection error: {ex.Message}", columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 });
            while (results.Count < queries.Count)
                results.Add(errorJson);
        }

        return results;
    }

    private async Task<string> ExecuteDuckDBQueryAsync(DuckDBConnection connection, string query, Stopwatch stopwatch)
    {
        const int maxResultRows = 100000; // Cap at 100K rows for frontend safety
        const int maxCellLength = 5000;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = query;

        await using var reader = await cmd.ExecuteReaderAsync();

        var columnNames = new List<string>();
        for (int i = 0; i < reader.FieldCount; i++)
        {
            columnNames.Add(reader.GetName(i));
        }

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = true });

        writer.WriteStartObject();

        writer.WritePropertyName("columns");
        writer.WriteStartArray();
        foreach (var col in columnNames)
        {
            writer.WriteStringValue(col);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("rows");
        writer.WriteStartArray();

        var rowCount = 0;
        var truncated = false;

        while (await reader.ReadAsync())
        {
            if (rowCount >= maxResultRows)
            {
                truncated = true;
                break;
            }

            writer.WriteStartObject();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                writer.WritePropertyName(columnNames[i]);

                if (reader.IsDBNull(i))
                {
                    writer.WriteNullValue();
                }
                else
                {
                    var value = reader.GetValue(i);
                    WriteJsonValue(writer, value, maxCellLength);
                }
            }
            writer.WriteEndObject();
            rowCount++;

            if (rowCount % 10000 == 0)
            {
                await writer.FlushAsync();
            }
        }

        writer.WriteEndArray();

        stopwatch.Stop();

        writer.WriteNumber("rowCount", rowCount);
        writer.WriteNumber("executionTimeMs", stopwatch.ElapsedMilliseconds);

        if (truncated)
        {
            writer.WriteBoolean("truncated", true);
            writer.WriteNumber("maxRows", maxResultRows);
        }

        writer.WriteEndObject();
        await writer.FlushAsync();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object value, int maxCellLength)
    {
        switch (value)
        {
            case double d:
                if (double.IsNaN(d) || double.IsInfinity(d))
                    writer.WriteNullValue();
                else if (d == Math.Floor(d) && d >= long.MinValue && d <= long.MaxValue)
                    writer.WriteNumberValue((long)d);
                else
                    writer.WriteNumberValue(Math.Round(d, 2));
                break;
            case float f:
                if (float.IsNaN(f) || float.IsInfinity(f))
                    writer.WriteNullValue();
                else
                    writer.WriteNumberValue(Math.Round(f, 2));
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case int intVal:
                writer.WriteNumberValue(intVal);
                break;
            case short s:
                writer.WriteNumberValue(s);
                break;
            case decimal dec:
                writer.WriteNumberValue(dec);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case string strVal:
                if (strVal.Length > maxCellLength)
                    writer.WriteStringValue(strVal.Substring(0, maxCellLength) + "...");
                else
                    writer.WriteStringValue(strVal);
                break;
            default:
                var strValue = value.ToString() ?? "";
                if (strValue.Length > maxCellLength)
                    writer.WriteStringValue(strValue.Substring(0, maxCellLength) + "...");
                else
                    writer.WriteStringValue(strValue);
                break;
        }
    }

    public string BuildSchemaDescription(string schemaInfoJson, string tableName, int? rowCount, string? sampleDataJson, bool usesParquet)
    {
        // For Parquet files, sampleDataJson is pre-computed (first 100 rows stored at upload time)
        // For legacy files, sampleDataJson is the full ParsedContent
        if (usesParquet && !string.IsNullOrEmpty(sampleDataJson))
        {
            return BuildSchemaDescription(schemaInfoJson, tableName, rowCount, sampleDataJson);
        }
        return BuildSchemaDescription(schemaInfoJson, tableName, rowCount, sampleDataJson);
    }

    // ─── DuckDB-aware preview/stats for Parquet files ────────────────────

    public async Task<string> ExecuteQueryAsync(string parsedContentJson, string schemaInfoJson, string query)
    {
        var stopwatch = Stopwatch.StartNew();

        // In-memory SQLite - lives only for this request
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        // Performance pragmas
        await using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA temp_store=MEMORY;";
            await pragmaCmd.ExecuteNonQueryAsync();
        }

        // Parse schema
        var columns = ParseSchema(schemaInfoJson);
        if (columns.Count == 0)
        {
            return JsonSerializer.Serialize(new { error = "No schema information available", columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 });
        }

        // Create table
        await CreateTableAsync(connection, columns);

        // Load data in batches
        var rowsInserted = await LoadDataAsync(connection, parsedContentJson, columns);
        _logger.LogInformation("Loaded {RowCount} rows into SQLite in {ElapsedMs}ms", rowsInserted, stopwatch.ElapsedMilliseconds);

        // Enable read-only mode before executing user query
        await using (var roCmd = connection.CreateCommand())
        {
            roCmd.CommandText = "PRAGMA query_only = ON;";
            await roCmd.ExecuteNonQueryAsync();
        }

        // Execute the user's query
        try
        {
            var result = await ExecuteSqlAsync(connection, query, stopwatch);
            return result;
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "SQLite query failed: {Query}", query);
            return JsonSerializer.Serialize(new { error = $"Query error: {ex.Message}", columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 });
        }
    }

    public async Task<List<string>> ExecuteQueriesAsync(string parsedContentJson, string schemaInfoJson, List<string> queries)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA temp_store=MEMORY;";
            await pragmaCmd.ExecuteNonQueryAsync();
        }

        var columns = ParseSchema(schemaInfoJson);
        if (columns.Count == 0)
        {
            var errorJson = JsonSerializer.Serialize(new { error = "No schema information available", columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 });
            return queries.Select(_ => errorJson).ToList();
        }

        await CreateTableAsync(connection, columns);
        var rowsInserted = await LoadDataAsync(connection, parsedContentJson, columns);
        _logger.LogInformation("Loaded {RowCount} rows into SQLite in {ElapsedMs}ms for {QueryCount} queries", rowsInserted, stopwatch.ElapsedMilliseconds, queries.Count);

        // Enable read-only mode before executing user queries
        await using (var roCmd = connection.CreateCommand())
        {
            roCmd.CommandText = "PRAGMA query_only = ON;";
            await roCmd.ExecuteNonQueryAsync();
        }

        var results = new List<string>();
        foreach (var query in queries)
        {
            try
            {
                results.Add(await ExecuteSqlAsync(connection, query, Stopwatch.StartNew()));
            }
            catch (SqliteException ex)
            {
                _logger.LogWarning(ex, "SQLite query failed: {Query}", query);
                results.Add(JsonSerializer.Serialize(new { error = $"Query error: {ex.Message}", columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 }));
            }
        }

        return results;
    }

    public string BuildSchemaDescription(string schemaInfoJson, string tableName, int? rowCount)
    {
        var columns = ParseSchema(schemaInfoJson);
        if (columns.Count == 0) return "No schema available.";

        var lines = new List<string>
        {
            $"Table: {tableName} ({(rowCount.HasValue ? $"{rowCount.Value:N0} rows" : "unknown rows")})",
            "Columns:"
        };

        foreach (var col in columns)
        {
            var sqlType = MapToSqliteType(col.DataType);
            lines.Add($"  - \"{col.Name}\" ({sqlType}{(col.IsNullable ? ", nullable" : "")})");
        }

        return string.Join("\n", lines);
    }

    public string BuildSchemaDescription(string schemaInfoJson, string tableName, int? rowCount, string? parsedContentJson)
    {
        var columns = ParseSchema(schemaInfoJson);
        if (columns.Count == 0) return "No schema available.";

        if (string.IsNullOrEmpty(parsedContentJson))
            return BuildSchemaDescription(schemaInfoJson, tableName, rowCount);

        try
        {
            using var doc = JsonDocument.Parse(parsedContentJson);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                return BuildSchemaDescription(schemaInfoJson, tableName, rowCount);

            var placeholderValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "not mentioned", "n/a", "na", "-", "null", "none", "tbd", "unknown", "not available", "not applicable" };
            var booleanValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "yes", "no", "true", "false", "1", "0" };

            // Analyze ALL columns by scanning actual data
            var maxScan = Math.Min(100, root.GetArrayLength());
            var colInfo = new Dictionary<string, (int numericCount, int boolCount, int placeholderCount, int totalNonNull, HashSet<string> distinct, HashSet<string> placeholders)>();

            foreach (var col in columns)
            {
                colInfo[col.Name] = (0, 0, 0, 0, new HashSet<string>(), new HashSet<string>());
            }

            for (var i = 0; i < maxScan; i++)
            {
                var row = root[i];
                foreach (var col in columns)
                {
                    if (!row.TryGetProperty(col.Name, out var prop)) continue;
                    if (prop.ValueKind == JsonValueKind.Null) continue;

                    var info = colInfo[col.Name];
                    info.totalNonNull++;

                    var val = prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
                    if (string.IsNullOrWhiteSpace(val)) continue;

                    if (info.distinct.Count < 20)
                        info.distinct.Add(val);

                    if (prop.ValueKind == JsonValueKind.Number)
                    {
                        info.numericCount++;
                    }
                    else if (prop.ValueKind == JsonValueKind.String)
                    {
                        var trimmed = val.Trim();
                        var cleaned = trimmed.Replace(",", "").Replace("$", "").Replace("€", "").Replace("£", "");
                        if (double.TryParse(cleaned, out _))
                        {
                            info.numericCount++;
                        }
                        else if (booleanValues.Contains(trimmed))
                        {
                            info.boolCount++;
                        }
                        else if (placeholderValues.Contains(trimmed))
                        {
                            info.placeholderCount++;
                            info.placeholders.Add(trimmed);
                        }
                    }
                    else if (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False)
                    {
                        info.boolCount++;
                    }

                    colInfo[col.Name] = info;
                }
            }

            // Build schema with inline type tags
            var sb = new StringBuilder();
            sb.AppendLine($"Table: \"data\" ({(rowCount.HasValue ? $"{rowCount.Value:N0} rows" : "unknown rows")})");
            sb.AppendLine("Columns:");

            foreach (var col in columns)
            {
                var info = colInfo[col.Name];
                var tag = ClassifyColumn(col.Name, info, columns.Count);
                sb.AppendLine($"  - \"{col.Name}\" {tag}");
            }

            // Sample data
            sb.AppendLine();
            sb.AppendLine("Sample data (first 3 rows — understand values, do NOT hardcode in CASE):");

            var sampleCount = Math.Min(3, root.GetArrayLength());
            for (var i = 0; i < sampleCount; i++)
            {
                var row = root[i];
                var values = new List<string>();
                foreach (var col in columns)
                {
                    var val = "null";
                    if (row.TryGetProperty(col.Name, out var prop) && prop.ValueKind != JsonValueKind.Null)
                    {
                        val = prop.ValueKind == JsonValueKind.String ? $"\"{prop.GetString()}\"" : prop.ToString();
                    }
                    values.Add($"{col.Name}={val}");
                }
                sb.AppendLine($"  Row {i + 1}: {string.Join(", ", values)}");
            }

            // Add scoring summary — concentrated list so AI can't miss scoreable columns
            var numericCols = new List<string>();
            var booleanCols = new List<string>();
            var mixedCols = new List<string>();
            var categoryCols = new List<string>();
            var identifierCols = new List<string>();

            foreach (var col in columns)
            {
                var info = colInfo[col.Name];
                var total = info.totalNonNull;
                if (total == 0) continue;

                var nonPlaceholder = total - info.placeholderCount;
                if (nonPlaceholder == 0) continue;

                // Check identifier first — ID/name columns should never be scored even if numeric
                var nameLower = col.Name.ToLowerInvariant();
                if (nameLower.Contains("id") || nameLower.Contains("name") || nameLower.Contains("email"))
                {
                    identifierCols.Add($"\"{col.Name}\"");
                }
                else if (info.numericCount > nonPlaceholder * 0.7)
                {
                    if (info.placeholderCount > 0)
                        mixedCols.Add($"\"{col.Name}\"");
                    else
                        numericCols.Add($"\"{col.Name}\"");
                }
                else if (info.boolCount > nonPlaceholder * 0.7)
                {
                    booleanCols.Add($"\"{col.Name}\"");
                }
                else
                {
                    if (info.distinct.Count >= 15)
                        identifierCols.Add($"\"{col.Name}\"");
                    else
                        categoryCols.Add($"\"{col.Name}\"");
                }
            }

            sb.AppendLine();
            if (numericCols.Count > 0)
                sb.AppendLine($"Scoreable NUMERIC: {string.Join(", ", numericCols)}");
            if (mixedCols.Count > 0)
                sb.AppendLine($"Scoreable MIXED (has placeholders): {string.Join(", ", mixedCols)}");
            if (booleanCols.Count > 0)
                sb.AppendLine($"Scoreable BOOLEAN: {string.Join(", ", booleanCols)}");
            if (identifierCols.Count > 0)
                sb.AppendLine($"IDENTIFIER (not for scoring): {string.Join(", ", identifierCols)}");
            if (categoryCols.Count > 0)
                sb.AppendLine($"CATEGORY (group by only): {string.Join(", ", categoryCols)}");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build enhanced schema description");
            return BuildSchemaDescription(schemaInfoJson, tableName, rowCount);
        }
    }

    private static string ClassifyColumn(string colName, (int numericCount, int boolCount, int placeholderCount, int totalNonNull, HashSet<string> distinct, HashSet<string> placeholders) info, int totalColumns)
    {
        var total = info.totalNonNull;
        if (total == 0) return "(EMPTY)";

        var nonPlaceholder = total - info.placeholderCount;
        if (nonPlaceholder == 0) return "(ALL PLACEHOLDERS — skip)";

        // Mostly numeric values (with possible placeholders)
        if (info.numericCount > nonPlaceholder * 0.7)
        {
            if (info.placeholderCount > 0)
            {
                return $"(NUMERIC with placeholders: {string.Join(", ", info.placeholders.Select(p => $"\"{p}\""))} — filter placeholders, CAST(REPLACE(col, ',', '') AS REAL), use in scoring)";
            }
            return "(NUMERIC — use in scoring)";
        }

        // Mostly boolean values
        if (info.boolCount > nonPlaceholder * 0.7)
        {
            return "(BOOLEAN — use as flag in scoring)";
        }

        // Check if it looks like an identifier (high cardinality, likely unique, or has id/name in the column name)
        var nameLower = colName.ToLowerInvariant();
        if (nameLower.Contains("id") || nameLower.Contains("name") || nameLower.Contains("email") || info.distinct.Count >= 15)
        {
            return "(IDENTIFIER — not for scoring, use in SELECT)";
        }

        // Low cardinality text = category
        if (info.distinct.Count <= 10)
        {
            return $"(CATEGORY: {string.Join(", ", info.distinct.Take(8).Select(v => $"\"{v}\""))} — group by only, not for scoring)";
        }

        return $"(TEXT — {info.distinct.Count}+ distinct values)";
    }

    private List<ColumnDef> ParseSchema(string schemaInfoJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(schemaInfoJson);
            var root = doc.RootElement;

            // Schema is an array of {Name, DataType, IsNullable, MaxLength}
            if (root.ValueKind == JsonValueKind.Array)
            {
                var columns = new List<ColumnDef>();
                foreach (var col in root.EnumerateArray())
                {
                    var name = col.TryGetProperty("Name", out var n) ? n.GetString()
                             : col.TryGetProperty("name", out var n2) ? n2.GetString()
                             : null;
                    var dataType = col.TryGetProperty("DataType", out var dt) ? dt.GetString()
                                 : col.TryGetProperty("dataType", out var dt2) ? dt2.GetString()
                                 : "string";

                    if (!string.IsNullOrEmpty(name))
                    {
                        columns.Add(new ColumnDef
                        {
                            Name = name,
                            DataType = dataType ?? "string",
                            IsNullable = col.TryGetProperty("IsNullable", out var isn) ? isn.GetBoolean()
                                       : col.TryGetProperty("isNullable", out var isn2) && isn2.GetBoolean()
                        });
                    }
                }
                return columns;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse schema info JSON");
        }

        return new List<ColumnDef>();
    }

    private static async Task CreateTableAsync(SqliteConnection connection, List<ColumnDef> columns)
    {
        var columnDefs = columns.Select(c =>
        {
            var sqlType = MapToSqliteType(c.DataType);
            return $"\"{c.Name}\" {sqlType}";
        });

        var sql = $"CREATE TABLE \"{TableName}\" ({string.Join(", ", columnDefs)})";

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> LoadDataAsync(SqliteConnection connection, string parsedContentJson, List<ColumnDef> columns)
    {
        using var doc = JsonDocument.Parse(parsedContentJson);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array) return 0;

        var rowCount = 0;

        // Use a transaction for bulk insert performance
        await using var transaction = await connection.BeginTransactionAsync();

        // Build parameterized INSERT
        var paramNames = columns.Select((_, i) => $"@p{i}").ToList();
        var insertSql = $"INSERT INTO \"{TableName}\" ({string.Join(", ", columns.Select(c => $"\"{c.Name}\""))}) VALUES ({string.Join(", ", paramNames)})";

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = insertSql;
        cmd.Transaction = (SqliteTransaction?)transaction;

        // Pre-create parameters
        var parameters = new SqliteParameter[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            parameters[i] = cmd.CreateParameter();
            parameters[i].ParameterName = paramNames[i];
            cmd.Parameters.Add(parameters[i]);
        }

        // Prepare the statement once for faster repeated execution
        await cmd.PrepareAsync();

        foreach (var row in root.EnumerateArray())
        {
            for (int i = 0; i < columns.Count; i++)
            {
                var colName = columns[i].Name;
                if (row.TryGetProperty(colName, out var val) && val.ValueKind != JsonValueKind.Null)
                {
                    parameters[i].Value = ConvertJsonValue(val, columns[i].DataType);
                }
                else
                {
                    parameters[i].Value = DBNull.Value;
                }
            }

            await cmd.ExecuteNonQueryAsync();
            rowCount++;
        }

        await transaction.CommitAsync();
        return rowCount;
    }

    private static object ConvertJsonValue(JsonElement val, string dataType)
    {
        return dataType.ToLowerInvariant() switch
        {
            "number" or "double" or "float" or "decimal" or "integer" or "int" =>
                val.ValueKind == JsonValueKind.Number ? val.GetDouble()
                : double.TryParse(val.GetString(), out var d) ? d
                : (object)val.ToString(),

            "boolean" or "bool" =>
                val.ValueKind == JsonValueKind.True || val.ValueKind == JsonValueKind.False
                    ? val.GetBoolean() ? 1 : 0
                    : (object)val.ToString(),

            _ => val.ToString()
        };
    }

    private async Task<string> ExecuteSqlAsync(SqliteConnection connection, string query, Stopwatch stopwatch)
    {
        const int maxResultRows = 100000; // Support up to 100k rows
        const int maxCellLength = 5000;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = query;

        await using var reader = await cmd.ExecuteReaderAsync();

        var columnNames = new List<string>();
        for (int i = 0; i < reader.FieldCount; i++)
        {
            columnNames.Add(reader.GetName(i));
        }

        // Use streaming JSON writer to avoid building huge object in memory
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = true });

        writer.WriteStartObject();

        // Write columns
        writer.WritePropertyName("columns");
        writer.WriteStartArray();
        foreach (var col in columnNames)
        {
            writer.WriteStringValue(col);
        }
        writer.WriteEndArray();

        // Write rows array - streaming
        writer.WritePropertyName("rows");
        writer.WriteStartArray();

        var rowCount = 0;
        var truncated = false;

        while (await reader.ReadAsync())
        {
            if (rowCount >= maxResultRows)
            {
                truncated = true;
                break;
            }

            writer.WriteStartObject();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                writer.WritePropertyName(columnNames[i]);

                if (reader.IsDBNull(i))
                {
                    writer.WriteNullValue();
                }
                else
                {
                    var value = reader.GetValue(i);

                    // Handle different types
                    if (value is double d)
                    {
                        if (double.IsNaN(d) || double.IsInfinity(d))
                            writer.WriteNullValue();
                        else if (d == Math.Floor(d) && d >= long.MinValue && d <= long.MaxValue)
                            writer.WriteNumberValue((long)d);
                        else
                            writer.WriteNumberValue(Math.Round(d, 2));
                    }
                    else if (value is long l)
                    {
                        writer.WriteNumberValue(l);
                    }
                    else if (value is int intVal)
                    {
                        writer.WriteNumberValue(intVal);
                    }
                    else if (value is string strVal)
                    {
                        if (strVal.Length > maxCellLength)
                            writer.WriteStringValue(strVal.Substring(0, maxCellLength) + "...");
                        else
                            writer.WriteStringValue(strVal);
                    }
                    else
                    {
                        var strValue = value?.ToString() ?? "";
                        if (strValue.Length > maxCellLength)
                            writer.WriteStringValue(strValue.Substring(0, maxCellLength) + "...");
                        else
                            writer.WriteStringValue(strValue);
                    }
                }
            }
            writer.WriteEndObject();
            rowCount++;

            // Flush periodically to avoid huge buffers
            if (rowCount % 10000 == 0)
            {
                await writer.FlushAsync();
            }
        }

        writer.WriteEndArray();

        stopwatch.Stop();

        // Write metadata
        writer.WriteNumber("rowCount", rowCount);
        writer.WriteNumber("executionTimeMs", stopwatch.ElapsedMilliseconds);

        if (truncated)
        {
            writer.WriteBoolean("truncated", true);
            writer.WriteNumber("maxRows", maxResultRows);
        }

        writer.WriteEndObject();
        await writer.FlushAsync();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string MapToSqliteType(string dataType)
    {
        return dataType.ToLowerInvariant() switch
        {
            "number" or "double" or "float" or "decimal" => "REAL",
            "integer" or "int" => "INTEGER",
            "boolean" or "bool" => "INTEGER",
            "date" or "datetime" => "TEXT",
            _ => "TEXT"
        };
    }

    public async Task<string> GetPreviewDataAsync(Guid fileId, int limit = 50)
    {
        var file = await _unitOfWork.FileDocuments.GetByIdAsync(fileId);
        if (file == null)
            return JsonSerializer.Serialize(new { columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 });

        // Parquet path
        if (file.UsesParquet && !string.IsNullOrEmpty(file.ParquetStoragePath))
        {
            var query = $"SELECT * FROM \"data\" LIMIT {Math.Min(limit, 100)}";
            return await ExecuteQueryViaParquetAsync(file.ParquetStoragePath, query);
        }

        // Legacy SQLite path
        if (string.IsNullOrEmpty(file.ParsedContent))
            return JsonSerializer.Serialize(new { columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 });

        var sqliteQuery = $"SELECT * FROM {TableName} LIMIT {Math.Min(limit, 100)}";
        return await ExecuteQueryAsync(file.ParsedContent, file.SchemaInfo ?? "[]", sqliteQuery);
    }

    public async Task<string> GetColumnStatsAsync(Guid fileId, string columnName)
    {
        var file = await _unitOfWork.FileDocuments.GetByIdAsync(fileId);
        if (file == null)
            return JsonSerializer.Serialize(new { error = "File not found" });

        var safeColumn = new string(columnName.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == ' ').ToArray());
        var quotedColumn = $"\"{safeColumn}\"";

        // Parquet path (DuckDB)
        if (file.UsesParquet && !string.IsNullOrEmpty(file.ParquetStoragePath))
        {
            return await GetColumnStatsViaDuckDBAsync(file, columnName, safeColumn, quotedColumn);
        }

        // Legacy SQLite path
        if (string.IsNullOrEmpty(file.ParsedContent))
            return JsonSerializer.Serialize(new { error = "No parsed content available" });

        var stopwatch = Stopwatch.StartNew();

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var columns = ParseSchema(file.SchemaInfo ?? "[]");
        if (columns.Count == 0)
        {
            return JsonSerializer.Serialize(new { error = "No schema available" });
        }

        await CreateTableAsync(connection, columns);
        var rowsInserted = await LoadDataAsync(connection, file.ParsedContent, columns);

        // Get statistics
        var statsQuery = $@"
            SELECT
                COUNT(*) as total_count,
                COUNT(*) - COUNT({quotedColumn}) as null_count,
                COUNT(DISTINCT {quotedColumn}) as unique_count,
                MIN({quotedColumn}) as min_value,
                MAX({quotedColumn}) as max_value,
                AVG(CASE WHEN typeof({quotedColumn}) IN ('integer', 'real') THEN CAST({quotedColumn} AS REAL) ELSE NULL END) as avg_value
            FROM {TableName}";

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = statsQuery;
        await using var reader = await cmd.ExecuteReaderAsync();

        var result = new Dictionary<string, object?>();
        if (await reader.ReadAsync())
        {
            var totalCount = reader.GetInt64(0);
            var nullCount = reader.GetInt64(1);

            result["columnName"] = columnName;
            result["dataType"] = columns.FirstOrDefault(c => c.Name == columnName)?.DataType ?? "unknown";
            result["totalCount"] = totalCount;
            result["nullCount"] = nullCount;
            result["nullPercentage"] = totalCount > 0 ? Math.Round((double)nullCount / totalCount * 100, 2) : 0;
            result["uniqueCount"] = reader.GetInt64(2);
            result["minValue"] = reader.IsDBNull(3) ? null : reader.GetValue(3);
            result["maxValue"] = reader.IsDBNull(4) ? null : reader.GetValue(4);
            result["avgValue"] = reader.IsDBNull(5) ? null : Math.Round(reader.GetDouble(5), 2);
        }

        // Get sample values
        var sampleQuery = $"SELECT DISTINCT {quotedColumn} FROM {TableName} WHERE {quotedColumn} IS NOT NULL LIMIT 10";
        await using var sampleCmd = connection.CreateCommand();
        sampleCmd.CommandText = sampleQuery;
        await using var sampleReader = await sampleCmd.ExecuteReaderAsync();

        var samples = new List<object?>();
        while (await sampleReader.ReadAsync())
        {
            samples.Add(sampleReader.IsDBNull(0) ? null : sampleReader.GetValue(0));
        }
        result["sampleValues"] = samples;

        return JsonSerializer.Serialize(result);
    }

    private async Task<string> GetColumnStatsViaDuckDBAsync(Core.Entities.FileDocument file, string columnName, string safeColumn, string quotedColumn)
    {
        try
        {
            await using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();

            await ConfigureDuckDBForS3Async(connection);

            var s3Url = BuildS3ParquetUrl(file.ParquetStoragePath!);
            await using (var viewCmd = connection.CreateCommand())
            {
                viewCmd.CommandText = $"CREATE VIEW \"data\" AS SELECT * FROM read_parquet('{s3Url.Replace("'", "''")}')";
                await viewCmd.ExecuteNonQueryAsync();
            }

            var columns = ParseSchema(file.SchemaInfo ?? "[]");
            var result = new Dictionary<string, object?>();

            var statsQuery = $@"
                SELECT
                    COUNT(*) as total_count,
                    COUNT(*) - COUNT({quotedColumn}) as null_count,
                    COUNT(DISTINCT {quotedColumn}) as unique_count,
                    MIN({quotedColumn}) as min_value,
                    MAX({quotedColumn}) as max_value,
                    AVG(TRY_CAST({quotedColumn} AS DOUBLE)) as avg_value
                FROM ""data""";

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = statsQuery;
            await using var reader = await cmd.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                var totalCount = Convert.ToInt64(reader.GetValue(0));
                var nullCount = Convert.ToInt64(reader.GetValue(1));

                result["columnName"] = columnName;
                result["dataType"] = columns.FirstOrDefault(c => c.Name == columnName)?.DataType ?? "unknown";
                result["totalCount"] = totalCount;
                result["nullCount"] = nullCount;
                result["nullPercentage"] = totalCount > 0 ? Math.Round((double)nullCount / totalCount * 100, 2) : 0;
                result["uniqueCount"] = Convert.ToInt64(reader.GetValue(2));
                result["minValue"] = reader.IsDBNull(3) ? null : reader.GetValue(3);
                result["maxValue"] = reader.IsDBNull(4) ? null : reader.GetValue(4);
                result["avgValue"] = reader.IsDBNull(5) ? null : Math.Round(Convert.ToDouble(reader.GetValue(5)), 2);
            }

            // Get sample values
            var sampleQuery = $"SELECT DISTINCT {quotedColumn} FROM \"data\" WHERE {quotedColumn} IS NOT NULL LIMIT 10";
            await using var sampleCmd = connection.CreateCommand();
            sampleCmd.CommandText = sampleQuery;
            await using var sampleReader = await sampleCmd.ExecuteReaderAsync();

            var samples = new List<object?>();
            while (await sampleReader.ReadAsync())
            {
                samples.Add(sampleReader.IsDBNull(0) ? null : sampleReader.GetValue(0));
            }
            result["sampleValues"] = samples;

            return JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DuckDB column stats failed for {Column}", columnName);
            return JsonSerializer.Serialize(new { error = $"Stats error: {ex.Message}" });
        }
    }

    public async Task<string> GetSchemaAsync(Guid fileId)
    {
        var file = await _unitOfWork.FileDocuments.GetByIdAsync(fileId);
        if (file == null)
        {
            return "[]";
        }
        return file.SchemaInfo ?? "[]";
    }

    public async Task<string> GetFileStatsAsync(Guid fileId)
    {
        var file = await _unitOfWork.FileDocuments.GetByIdAsync(fileId);
        if (file == null)
            return JsonSerializer.Serialize(new { rowCount = 0, columns = Array.Empty<object>() });

        // Parquet path (DuckDB)
        if (file.UsesParquet && !string.IsNullOrEmpty(file.ParquetStoragePath))
        {
            return await GetFileStatsViaDuckDBAsync(file);
        }

        // Legacy SQLite path
        if (string.IsNullOrEmpty(file.ParsedContent))
            return JsonSerializer.Serialize(new { rowCount = 0, columns = Array.Empty<object>() });

        var columns = ParseSchema(file.SchemaInfo ?? "[]");

        // Count rows
        long rowCount = 0;
        try
        {
            using var doc = JsonDocument.Parse(file.ParsedContent);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                rowCount = doc.RootElement.GetArrayLength();
            }
        }
        catch
        {
            // Ignore parse errors
        }

        // Calculate column stats
        var columnStats = new List<object>();
        if (rowCount > 0)
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            await CreateTableAsync(connection, columns);
            await LoadDataAsync(connection, file.ParsedContent, columns);

            foreach (var col in columns.Take(10))
            {
                try
                {
                    var quotedCol = $"\"{col.Name}\"";
                    var query = $"SELECT COUNT(*) - COUNT({quotedCol}) as null_count FROM {TableName}";

                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = query;
                    var nullCount = (long)(await cmd.ExecuteScalarAsync() ?? 0);

                    columnStats.Add(new
                    {
                        name = col.Name,
                        dataType = col.DataType,
                        nullPercentage = rowCount > 0 ? Math.Round((double)nullCount / rowCount * 100, 2) : 0
                    });
                }
                catch
                {
                    columnStats.Add(new { name = col.Name, dataType = col.DataType, nullPercentage = 0 });
                }
            }
        }

        return JsonSerializer.Serialize(new
        {
            rowCount,
            columnCount = columns.Count,
            columns = columnStats
        });
    }

    private async Task<string> GetFileStatsViaDuckDBAsync(Core.Entities.FileDocument file)
    {
        try
        {
            await using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();

            await ConfigureDuckDBForS3Async(connection);

            var s3Url = BuildS3ParquetUrl(file.ParquetStoragePath!);
            await using (var viewCmd = connection.CreateCommand())
            {
                viewCmd.CommandText = $"CREATE VIEW \"data\" AS SELECT * FROM read_parquet('{s3Url.Replace("'", "''")}')";
                await viewCmd.ExecuteNonQueryAsync();
            }

            // Get row count
            long rowCount;
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM \"data\"";
                rowCount = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
            }

            var columns = ParseSchema(file.SchemaInfo ?? "[]");
            var columnStats = new List<object>();

            foreach (var col in columns.Take(10))
            {
                try
                {
                    var quotedCol = $"\"{col.Name}\"";
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"SELECT COUNT(*) - COUNT({quotedCol}) FROM \"data\"";
                    var nullCount = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);

                    columnStats.Add(new
                    {
                        name = col.Name,
                        dataType = col.DataType,
                        nullPercentage = rowCount > 0 ? Math.Round((double)nullCount / rowCount * 100, 2) : 0
                    });
                }
                catch
                {
                    columnStats.Add(new { name = col.Name, dataType = col.DataType, nullPercentage = 0 });
                }
            }

            return JsonSerializer.Serialize(new
            {
                rowCount,
                columnCount = columns.Count,
                columns = columnStats
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DuckDB file stats failed for {FileId}", file.Id);
            return JsonSerializer.Serialize(new { rowCount = file.TotalRowCount ?? 0, columns = Array.Empty<object>() });
        }
    }

    private class ColumnDef
    {
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = "string";
        public bool IsNullable { get; set; }
    }
}
