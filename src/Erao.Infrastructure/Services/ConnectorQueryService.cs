using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DuckDB.NET.Data;
using Erao.Core.Entities;
using Erao.Core.Enums;
using Erao.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Erao.Infrastructure.Services;

public class ConnectorQueryService : IConnectorQueryService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<ConnectorQueryService> _logger;

    // S3/R2 config for DuckDB httpfs (same as FileQueryService)
    private readonly string _s3Endpoint;
    private readonly string _s3AccessKey;
    private readonly string _s3SecretKey;
    private readonly string _s3Region;
    private readonly string _s3BucketName;
    private readonly bool _s3UseSSL;

    public ConnectorQueryService(IUnitOfWork unitOfWork, ILogger<ConnectorQueryService> logger, IConfiguration configuration)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;

        _s3Endpoint = configuration["Minio:Endpoint"] ?? "localhost:9000";
        _s3AccessKey = configuration["Minio:AccessKey"] ?? "minioadmin";
        _s3SecretKey = configuration["Minio:SecretKey"] ?? "minioadmin";
        _s3Region = configuration["Minio:Region"] ?? "auto";
        _s3BucketName = configuration["Minio:BucketName"] ?? "erao-files";
        _s3UseSSL = configuration.GetValue<bool>("Minio:UseSSL", false);
    }

    public async Task<string> ExecuteQueryForConnectorAsync(Guid connectorId, string query)
    {
        var connector = await _unitOfWork.AppConnectors.GetByIdAsync(connectorId);
        if (connector == null)
            return JsonSerializer.Serialize(new { error = "Connector not found", columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 });

        if (connector.SyncStatus != ConnectorSyncStatus.Completed || string.IsNullOrEmpty(connector.ParquetStoragePaths))
            return JsonSerializer.Serialize(new { error = "Connector data not synced", columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 });

        var tablePaths = JsonSerializer.Deserialize<Dictionary<string, string>>(connector.ParquetStoragePaths);
        if (tablePaths == null || tablePaths.Count == 0)
            return JsonSerializer.Serialize(new { error = "No synced tables available", columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 });

        try
        {
            await using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();
            await ConfigureDuckDBForS3Async(connection);
            await CreateTableViewsAsync(connection, tablePaths);

            return await ExecuteDuckDBQueryAsync(connection, query, Stopwatch.StartNew());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connector DuckDB query failed: {Query}", query);
            return JsonSerializer.Serialize(new { error = $"Query error: {ex.Message}", columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 });
        }
    }

    public async Task<List<string>> ExecuteQueriesForConnectorAsync(Guid connectorId, List<string> queries)
    {
        var results = new List<string>();

        var connector = await _unitOfWork.AppConnectors.GetByIdAsync(connectorId);
        if (connector == null || connector.SyncStatus != ConnectorSyncStatus.Completed ||
            string.IsNullOrEmpty(connector.ParquetStoragePaths))
        {
            var errorJson = JsonSerializer.Serialize(new { error = "Connector data not synced", columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 });
            return queries.Select(_ => errorJson).ToList();
        }

        var tablePaths = JsonSerializer.Deserialize<Dictionary<string, string>>(connector.ParquetStoragePaths);
        if (tablePaths == null || tablePaths.Count == 0)
        {
            var errorJson = JsonSerializer.Serialize(new { error = "No synced tables", columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 });
            return queries.Select(_ => errorJson).ToList();
        }

        try
        {
            await using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();
            await ConfigureDuckDBForS3Async(connection);
            await CreateTableViewsAsync(connection, tablePaths);

            foreach (var query in queries)
            {
                try
                {
                    results.Add(await ExecuteDuckDBQueryAsync(connection, query, Stopwatch.StartNew()));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Connector DuckDB query failed: {Query}", query);
                    results.Add(JsonSerializer.Serialize(new { error = $"Query error: {ex.Message}", columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 }));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DuckDB connection failed for connector queries");
            var errorJson = JsonSerializer.Serialize(new { error = $"Connection error: {ex.Message}", columns = Array.Empty<string>(), rows = Array.Empty<object>(), rowCount = 0 });
            while (results.Count < queries.Count) results.Add(errorJson);
        }

        return results;
    }

    public string BuildConnectorSchemaDescription(AppConnector connector)
    {
        if (string.IsNullOrEmpty(connector.SchemaInfo) || string.IsNullOrEmpty(connector.TableRowCounts))
            return connector.SchemaContext ?? "No schema available.";

        try
        {
            var schemaMap = JsonSerializer.Deserialize<Dictionary<string, string>>(connector.SchemaInfo);
            var rowCountMap = JsonSerializer.Deserialize<Dictionary<string, long>>(connector.TableRowCounts);
            var sampleMap = !string.IsNullOrEmpty(connector.SampleDataJson)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(connector.SampleDataJson)
                : null;

            if (schemaMap == null || rowCountMap == null)
                return connector.SchemaContext ?? "No schema available.";

            var sb = new StringBuilder();
            sb.AppendLine($"-- {connector.ConnectorType} Synced Data (Live)");
            sb.AppendLine();

            foreach (var (tableName, schemaJson) in schemaMap)
            {
                var rowCount = rowCountMap.GetValueOrDefault(tableName, 0);
                var sampleJson = sampleMap?.GetValueOrDefault(tableName);

                sb.AppendLine(BuildTableSchemaDescription(schemaJson, tableName, rowCount, sampleJson));
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build connector schema description");
            return connector.SchemaContext ?? "No schema available.";
        }
    }

    private static string BuildTableSchemaDescription(string schemaInfoJson, string tableName, long rowCount, string? sampleDataJson)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Table: \"{tableName}\" ({rowCount:N0} rows)");
        sb.AppendLine("Columns:");

        try
        {
            using var doc = JsonDocument.Parse(schemaInfoJson);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array) return sb.ToString();

            foreach (var col in root.EnumerateArray())
            {
                var name = col.TryGetProperty("Name", out var n) ? n.GetString()
                         : col.TryGetProperty("name", out var n2) ? n2.GetString()
                         : null;
                var dataType = col.TryGetProperty("DataType", out var dt) ? dt.GetString()
                             : col.TryGetProperty("dataType", out var dt2) ? dt2.GetString()
                             : "string";
                var isNullable = col.TryGetProperty("IsNullable", out var isn) && isn.GetBoolean();

                if (!string.IsNullOrEmpty(name))
                {
                    sb.AppendLine($"  - \"{name}\" ({dataType}{(isNullable ? ", nullable" : "")})");
                }
            }

            // Add sample data if available
            if (!string.IsNullOrEmpty(sampleDataJson))
            {
                using var sampleDoc = JsonDocument.Parse(sampleDataJson);
                var sampleRoot = sampleDoc.RootElement;

                if (sampleRoot.ValueKind == JsonValueKind.Array && sampleRoot.GetArrayLength() > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Sample data (first 3 rows):");

                    var sampleCount = Math.Min(3, sampleRoot.GetArrayLength());
                    for (var i = 0; i < sampleCount; i++)
                    {
                        var row = sampleRoot[i];
                        var values = new List<string>();
                        foreach (var prop in row.EnumerateObject())
                        {
                            var val = prop.Value.ValueKind == JsonValueKind.String
                                ? $"\"{prop.Value.GetString()}\""
                                : prop.Value.ValueKind == JsonValueKind.Null
                                    ? "null"
                                    : prop.Value.ToString();
                            values.Add($"{prop.Name}={val}");
                        }
                        sb.AppendLine($"  Row {i + 1}: {string.Join(", ", values)}");
                    }
                }
            }
        }
        catch
        {
            // Fallback: just return what we have
        }

        return sb.ToString();
    }

    // ─── DuckDB Helpers ──────────────────────────────────────────────────

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
    }

    private async Task CreateTableViewsAsync(DuckDBConnection connection, Dictionary<string, string> tablePaths)
    {
        foreach (var (tableName, objectKey) in tablePaths)
        {
            var s3Url = $"s3://{_s3BucketName}/{objectKey}";
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"CREATE VIEW \"{tableName}\" AS SELECT * FROM read_parquet('{s3Url.Replace("'", "''")}')";
            await cmd.ExecuteNonQueryAsync();
        }
        _logger.LogDebug("Created {Count} DuckDB views for connector tables", tablePaths.Count);
    }

    private async Task<string> ExecuteDuckDBQueryAsync(DuckDBConnection connection, string query, Stopwatch stopwatch)
    {
        const int maxResultRows = 100000;
        const int maxCellLength = 5000;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = query;

        await using var reader = await cmd.ExecuteReaderAsync();

        var columnNames = new List<string>();
        for (int i = 0; i < reader.FieldCount; i++)
            columnNames.Add(reader.GetName(i));

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = true });

        writer.WriteStartObject();

        writer.WritePropertyName("columns");
        writer.WriteStartArray();
        foreach (var col in columnNames) writer.WriteStringValue(col);
        writer.WriteEndArray();

        writer.WritePropertyName("rows");
        writer.WriteStartArray();

        var rowCount = 0;
        var truncated = false;

        while (await reader.ReadAsync())
        {
            if (rowCount >= maxResultRows) { truncated = true; break; }

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

            if (rowCount % 10000 == 0) await writer.FlushAsync();
        }

        writer.WriteEndArray();
        stopwatch.Stop();

        writer.WriteNumber("rowCount", rowCount);
        writer.WriteNumber("executionTimeMs", stopwatch.ElapsedMilliseconds);
        if (truncated) { writer.WriteBoolean("truncated", true); writer.WriteNumber("maxRows", maxResultRows); }

        writer.WriteEndObject();
        await writer.FlushAsync();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object value, int maxCellLength)
    {
        switch (value)
        {
            case double d:
                if (double.IsNaN(d) || double.IsInfinity(d)) writer.WriteNullValue();
                else if (d == Math.Floor(d) && d >= long.MinValue && d <= long.MaxValue) writer.WriteNumberValue((long)d);
                else writer.WriteNumberValue(Math.Round(d, 2));
                break;
            case float f:
                if (float.IsNaN(f) || float.IsInfinity(f)) writer.WriteNullValue();
                else writer.WriteNumberValue(Math.Round(f, 2));
                break;
            case long l: writer.WriteNumberValue(l); break;
            case int intVal: writer.WriteNumberValue(intVal); break;
            case short s: writer.WriteNumberValue(s); break;
            case decimal dec: writer.WriteNumberValue(dec); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case string strVal:
                writer.WriteStringValue(strVal.Length > maxCellLength ? strVal[..maxCellLength] + "..." : strVal);
                break;
            default:
                var strValue = value.ToString() ?? "";
                writer.WriteStringValue(strValue.Length > maxCellLength ? strValue[..maxCellLength] + "..." : strValue);
                break;
        }
    }
}
