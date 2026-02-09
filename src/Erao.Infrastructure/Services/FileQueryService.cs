using System.Diagnostics;
using System.Text.Json;
using Erao.Core.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Erao.Infrastructure.Services;

public class FileQueryService : IFileQueryService
{
    private readonly ILogger<FileQueryService> _logger;
    private const string TableName = "data";

    public FileQueryService(ILogger<FileQueryService> logger)
    {
        _logger = logger;
    }

    public async Task<string> ExecuteQueryAsync(string parsedContentJson, string schemaInfoJson, string query)
    {
        var stopwatch = Stopwatch.StartNew();

        // In-memory SQLite - lives only for this request
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        // Enable WAL mode for better performance
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
        const int maxResultRows = 1000;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = query;

        await using var reader = await cmd.ExecuteReaderAsync();

        var columnNames = new List<string>();
        for (int i = 0; i < reader.FieldCount; i++)
        {
            columnNames.Add(reader.GetName(i));
        }

        var rows = new List<Dictionary<string, object?>>();
        var truncated = false;

        while (await reader.ReadAsync())
        {
            if (rows.Count >= maxResultRows)
            {
                truncated = true;
                break;
            }

            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);

                // Round floating point for cleaner display
                if (value is double d)
                {
                    value = d == Math.Floor(d) ? (object)(long)d : Math.Round(d, 2);
                }

                row[columnNames[i]] = value;
            }
            rows.Add(row);
        }

        stopwatch.Stop();

        var result = new Dictionary<string, object?>
        {
            ["columns"] = columnNames,
            ["rows"] = rows,
            ["rowCount"] = rows.Count,
            ["executionTimeMs"] = stopwatch.ElapsedMilliseconds
        };

        if (truncated)
        {
            result["truncated"] = true;
            result["maxRows"] = maxResultRows;
        }

        return JsonSerializer.Serialize(result);
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

    private class ColumnDef
    {
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = "string";
        public bool IsNullable { get; set; }
    }
}
