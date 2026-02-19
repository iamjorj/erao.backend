using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DuckDB.NET.Data;
using Erao.Core.Interfaces;
using ExcelDataReader;
using Microsoft.Extensions.Logging;

namespace Erao.Infrastructure.Services;

public class ParquetConversionService : IParquetConversionService
{
    private readonly ILogger<ParquetConversionService> _logger;

    public ParquetConversionService(ILogger<ParquetConversionService> logger)
    {
        _logger = logger;
    }

    public async Task<ParquetConversionResult> ConvertCsvToParquetAsync(Stream csvStream, string outputPath)
    {
        string? tempCsvPath = null;
        var sw = Stopwatch.StartNew();
        try
        {
            // Save stream to temp file (DuckDB reads from file path)
            tempCsvPath = Path.GetTempFileName() + ".csv";

            await using (var fileStream = File.Create(tempCsvPath))
            {
                await csvStream.CopyToAsync(fileStream);
            }

            _logger.LogInformation("[TIMING] CSV stream → temp file: {Ms}ms", sw.ElapsedMilliseconds);

            // Ensure output directory exists
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);

            // Use DuckDB to convert CSV → Parquet
            await using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var escapedCsv = EscapePath(tempCsvPath);
            var escapedParquet = EscapePath(outputPath);

            // COPY CSV to Parquet with ZSTD compression
            // sample_size=20000 is enough for accurate type inference — avoids full-file scan
            // Use explicit delim/quote/escape to avoid auto-detection failures on unusual data
            var copySql = $"COPY (SELECT * FROM read_csv('{escapedCsv}', header=true, delim=',', quote='\"', escape='\"', sample_size=20000, null_padding=true, ignore_errors=true, strict_mode=false, max_line_size=10000000)) TO '{escapedParquet}' (FORMAT PARQUET, COMPRESSION ZSTD)";
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = copySql;
                await cmd.ExecuteNonQueryAsync();
            }

            _logger.LogInformation("[TIMING] DuckDB CSV → Parquet: {Ms}ms", sw.ElapsedMilliseconds);

            // Get row count from Parquet metadata (no full scan needed)
            long rowCount;
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM read_parquet('{escapedParquet}')";
                var result = await cmd.ExecuteScalarAsync();
                rowCount = result != null ? Convert.ToInt64(result) : 0;
            }

            // Get schema info
            var schemaJson = await GetParquetSchemaAsync(outputPath, connection);

            // Get sample data (first 100 rows)
            var sampleJson = await GetSampleDataAsync(outputPath, 100, connection);

            _logger.LogInformation("[TIMING] Metadata + schema + sample: {Ms}ms (total)", sw.ElapsedMilliseconds);
            _logger.LogInformation("Converted CSV to Parquet: {RowCount} rows at {Path}", rowCount, outputPath);

            return new ParquetConversionResult
            {
                Success = true,
                ParquetPath = outputPath,
                RowCount = rowCount,
                SchemaInfoJson = schemaJson,
                SampleDataJson = sampleJson
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to convert CSV to Parquet: {OutputPath}", outputPath);
            // Clean up partial Parquet file on failure
            if (File.Exists(outputPath))
            {
                try { File.Delete(outputPath); } catch { /* best effort */ }
            }
            return new ParquetConversionResult
            {
                Success = false,
                ErrorMessage = $"Parquet conversion failed: {ex.Message}"
            };
        }
        finally
        {
            if (tempCsvPath != null && File.Exists(tempCsvPath))
            {
                try { File.Delete(tempCsvPath); } catch { /* best effort */ }
            }
        }
    }

    public async Task<ParquetConversionResult> ConvertExcelToParquetAsync(Stream excelStream, string outputPath)
    {
        string? tempCsvPath = null;
        var sw = Stopwatch.StartNew();
        try
        {
            // Register encoding provider for ExcelDataReader (required for .NET Core)
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            tempCsvPath = Path.GetTempFileName() + ".csv";

            // Single-pass streaming read with ExcelDataReader (no DOM, no stream reset)
            var config = new ExcelReaderConfiguration { LeaveOpen = true };
            using (var reader = ExcelReaderFactory.CreateReader(excelStream, config))
            {
                // Skip sheets with 0 columns (cover pages)
                while (reader.FieldCount == 0 && reader.NextResult()) { }

                var fieldCount = reader.FieldCount;
                if (fieldCount == 0)
                {
                    return new ParquetConversionResult { Success = false, ErrorMessage = "Excel file has no data" };
                }

                // Smart header detection: read first 10 rows, pick the one with most unique non-empty values
                var headerCandidates = new List<string[]>();
                for (var i = 0; i < 10 && reader.Read(); i++)
                {
                    var row = new string[fieldCount];
                    for (var c = 0; c < fieldCount; c++)
                        row[c] = reader.IsDBNull(c) ? "" : FormatCellValue(reader, c);
                    headerCandidates.Add(row);
                }

                if (headerCandidates.Count == 0)
                {
                    return new ParquetConversionResult { Success = false, ErrorMessage = "Excel file has no data" };
                }

                var headerRowIndex = 0;
                var bestUnique = 0;
                for (var i = 0; i < headerCandidates.Count; i++)
                {
                    var unique = headerCandidates[i].Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    if (unique > bestUnique && unique >= 2) { bestUnique = unique; headerRowIndex = i; }
                }

                _logger.LogInformation("[TIMING] ExcelDataReader header scan: {Ms}ms", sw.ElapsedMilliseconds);

                await using var csvWriter = new StreamWriter(tempCsvPath, false, Encoding.UTF8, bufferSize: 65536);

                // Write header
                var headers = headerCandidates[headerRowIndex];
                for (var c = 0; c < fieldCount; c++)
                {
                    if (c > 0) await csvWriter.WriteAsync(',');
                    var h = string.IsNullOrWhiteSpace(headers[c]) ? $"Column{c + 1}" : headers[c].Trim();
                    await csvWriter.WriteAsync(CsvEscape(h));
                }
                await csvWriter.WriteLineAsync();

                // Write buffered rows (after header)
                var sb = new StringBuilder(65536);
                var dataRowCount = 0;
                for (var i = headerRowIndex + 1; i < headerCandidates.Count; i++)
                {
                    AppendCsvRow(sb, headerCandidates[i], fieldCount, ref dataRowCount);
                }

                // Stream remaining rows directly to CSV
                while (reader.Read())
                {
                    var rowStart = sb.Length; // Mark start so we can undo just this row
                    var hasData = false;
                    var first = true;
                    for (var c = 0; c < fieldCount; c++)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        if (reader.IsDBNull(c))
                        {
                            // empty
                        }
                        else
                        {
                            hasData = true;
                            sb.Append(CsvEscape(FormatCellValue(reader, c)));
                        }
                    }

                    if (!hasData) { sb.Length = rowStart; continue; } // Undo only this row, keep prior data
                    sb.Append('\n');
                    dataRowCount++;

                    if (dataRowCount % 5000 == 0)
                    {
                        await csvWriter.WriteAsync(sb);
                        sb.Clear();
                    }
                }

                if (sb.Length > 0) await csvWriter.WriteAsync(sb);

                _logger.LogInformation("[TIMING] Excel → temp CSV: {Ms}ms ({Rows} data rows)", sw.ElapsedMilliseconds, dataRowCount);
            }

            // DuckDB: CSV → Parquet with explicit parsing options (no auto-detect issues)
            await using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);

            var escapedCsv = EscapePath(tempCsvPath);
            var escapedParquet = EscapePath(outputPath);

            var copySql = $"COPY (SELECT * FROM read_csv('{escapedCsv}', header=true, delim=',', quote='\"', escape='\"', sample_size=20000, null_padding=true, ignore_errors=true, strict_mode=false, max_line_size=10000000)) TO '{escapedParquet}' (FORMAT PARQUET, COMPRESSION ZSTD)";
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = copySql;
                await cmd.ExecuteNonQueryAsync();
            }

            long parquetRowCount;
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM read_parquet('{escapedParquet}')";
                var result = await cmd.ExecuteScalarAsync();
                parquetRowCount = result != null ? Convert.ToInt64(result) : 0;
            }

            _logger.LogInformation("[TIMING] DuckDB CSV → Parquet (Excel): {Ms}ms", sw.ElapsedMilliseconds);

            var schemaJson = await GetParquetSchemaAsync(outputPath, connection);
            var sampleJson = await GetSampleDataAsync(outputPath, 100, connection);

            _logger.LogInformation("[TIMING] Total Excel conversion: {Ms}ms", sw.ElapsedMilliseconds);
            _logger.LogInformation("Converted Excel to Parquet: {RowCount} rows at {Path}", parquetRowCount, outputPath);

            return new ParquetConversionResult
            {
                Success = true,
                ParquetPath = outputPath,
                RowCount = parquetRowCount,
                SchemaInfoJson = schemaJson,
                SampleDataJson = sampleJson
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to convert Excel to Parquet: {OutputPath}", outputPath);
            if (File.Exists(outputPath))
            {
                try { File.Delete(outputPath); } catch { /* best effort */ }
            }
            return new ParquetConversionResult
            {
                Success = false,
                ErrorMessage = $"Parquet conversion failed: {ex.Message}"
            };
        }
        finally
        {
            if (tempCsvPath != null && File.Exists(tempCsvPath))
            {
                try { File.Delete(tempCsvPath); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Format a cell value as a clean CSV-compatible string using raw values (no locale formatting).</summary>
    private static string FormatCellValue(IExcelDataReader reader, int col)
    {
        var type = reader.GetFieldType(col);
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
            return reader.GetDouble(col).ToString(CultureInfo.InvariantCulture);
        if (type == typeof(DateTime))
            return reader.GetDateTime(col).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (type == typeof(bool))
            return reader.GetBoolean(col) ? "true" : "false";
        return reader.GetValue(col)?.ToString() ?? "";
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('\n') || value.Contains('\r') || value.Contains('"'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private static void AppendCsvRow(StringBuilder sb, string[] values, int fieldCount, ref int rowCount)
    {
        var rowStart = sb.Length;
        var hasData = false;
        for (var c = 0; c < fieldCount; c++)
        {
            if (c > 0) sb.Append(',');
            var v = c < values.Length ? values[c] : "";
            if (!string.IsNullOrWhiteSpace(v)) hasData = true;
            sb.Append(CsvEscape(v));
        }
        if (!hasData) { sb.Length = rowStart; return; }
        sb.Append('\n');
        rowCount++;
    }

    public async Task<string> GetParquetSchemaAsync(string parquetPath)
    {
        await using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        return await GetParquetSchemaAsync(parquetPath, connection);
    }

    public async Task<string> GetSampleDataAsync(string parquetPath, int count = 100)
    {
        await using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        return await GetSampleDataAsync(parquetPath, count, connection);
    }

    public Task DeleteParquetFileAsync(string parquetPath)
    {
        if (File.Exists(parquetPath))
        {
            try
            {
                File.Delete(parquetPath);
                _logger.LogInformation("Deleted Parquet file: {Path}", parquetPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete Parquet file: {Path}", parquetPath);
            }
        }
        return Task.CompletedTask;
    }

    private static async Task<string> GetParquetSchemaAsync(string parquetPath, DuckDBConnection connection)
    {
        var escapedPath = EscapePath(parquetPath);
        var columns = new List<object>();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DESCRIBE SELECT * FROM read_parquet('{escapedPath}')";
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var colName = reader.GetString(0);
            var colType = reader.GetString(1);

            columns.Add(new
            {
                Name = colName,
                DataType = MapDuckDBTypeToSimple(colType),
                IsNullable = true
            });
        }

        return JsonSerializer.Serialize(columns);
    }

    private static async Task<string> GetSampleDataAsync(string parquetPath, int count, DuckDBConnection connection)
    {
        var escapedPath = EscapePath(parquetPath);
        var rows = new List<Dictionary<string, object?>>();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT * FROM read_parquet('{escapedPath}') LIMIT {count}";
        await using var reader = await cmd.ExecuteReaderAsync();

        var columnNames = new List<string>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columnNames.Add(reader.GetName(i));
        }

        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[columnNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }

        return JsonSerializer.Serialize(rows);
    }

    private static string MapDuckDBTypeToSimple(string duckDbType)
    {
        var upper = duckDbType.ToUpperInvariant();
        if (upper.Contains("INT") || upper == "BIGINT" || upper == "SMALLINT" || upper == "TINYINT" || upper == "HUGEINT")
            return "integer";
        if (upper.Contains("FLOAT") || upper.Contains("DOUBLE") || upper.Contains("DECIMAL") || upper.Contains("NUMERIC"))
            return "number";
        if (upper == "BOOLEAN" || upper == "BOOL")
            return "boolean";
        if (upper.Contains("DATE") || upper.Contains("TIME") || upper.Contains("TIMESTAMP"))
            return "date";
        return "string";
    }

    private static string EscapePath(string path)
    {
        // DuckDB expects forward slashes and single-quote escaping
        return path.Replace("\\", "/").Replace("'", "''");
    }
}
