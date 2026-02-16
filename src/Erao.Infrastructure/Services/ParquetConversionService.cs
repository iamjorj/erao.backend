using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using DuckDB.NET.Data;
using Erao.Core.Interfaces;
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
        try
        {
            // Save stream to temp file (DuckDB reads from file path)
            tempCsvPath = Path.GetTempFileName() + ".csv";
            _logger.LogInformation("[DEBUG] CSV stream Position={Position}, CanSeek={CanSeek}, CanRead={CanRead}",
                csvStream.CanSeek ? csvStream.Position : -1, csvStream.CanSeek, csvStream.CanRead);

            await using (var fileStream = File.Create(tempCsvPath))
            {
                await csvStream.CopyToAsync(fileStream);
            }

            var tempFileSize = new FileInfo(tempCsvPath).Length;
            _logger.LogInformation("[DEBUG] Temp CSV file written: {Path}, Size={Size} bytes", tempCsvPath, tempFileSize);

            // Log first few lines of CSV to verify content
            var previewLines = await File.ReadAllLinesAsync(tempCsvPath);
            _logger.LogInformation("[DEBUG] CSV total lines: {LineCount}", previewLines.Length);
            for (var i = 0; i < Math.Min(3, previewLines.Length); i++)
            {
                _logger.LogInformation("[DEBUG] CSV line {Index}: {Line}", i,
                    previewLines[i].Length > 200 ? previewLines[i][..200] + "..." : previewLines[i]);
            }

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
            var copySql = $"COPY (SELECT * FROM read_csv_auto('{escapedCsv}', header=true, all_varchar=false, sample_size=-1)) TO '{escapedParquet}' (FORMAT PARQUET, COMPRESSION ZSTD)";
            _logger.LogInformation("[DEBUG] DuckDB COPY SQL: {Sql}", copySql);
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = copySql;
                await cmd.ExecuteNonQueryAsync();
            }

            // Get row count
            long rowCount;
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM read_parquet('{escapedParquet}')";
                rowCount = (long)(await cmd.ExecuteScalarAsync() ?? 0);
            }
            _logger.LogInformation("[DEBUG] Parquet row count: {RowCount}", rowCount);

            // Log Parquet column names directly
            await using (var descCmd = connection.CreateCommand())
            {
                descCmd.CommandText = $"DESCRIBE SELECT * FROM read_parquet('{escapedParquet}')";
                await using var descReader = await descCmd.ExecuteReaderAsync();
                var colIndex = 0;
                while (await descReader.ReadAsync())
                {
                    _logger.LogInformation("[DEBUG] Parquet column {Index}: name={Name}, type={Type}",
                        colIndex, descReader.GetString(0), descReader.GetString(1));
                    colIndex++;
                }
            }

            // Get schema info
            var schemaJson = await GetParquetSchemaAsync(outputPath, connection);
            _logger.LogInformation("[DEBUG] Schema JSON: {Schema}", schemaJson.Length > 500 ? schemaJson[..500] + "..." : schemaJson);

            // Get sample data (first 100 rows)
            var sampleJson = await GetSampleDataAsync(outputPath, 100, connection);
            _logger.LogInformation("[DEBUG] Sample data length: {Length}, preview: {Preview}",
                sampleJson.Length, sampleJson.Length > 300 ? sampleJson[..300] + "..." : sampleJson);

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
        try
        {
            // Convert Excel → temp CSV using ClosedXML, then CSV → Parquet
            tempCsvPath = Path.GetTempFileName() + ".csv";
            _logger.LogInformation("[DEBUG] Excel stream Position={Position}, CanSeek={CanSeek}, CanRead={CanRead}",
                excelStream.CanSeek ? excelStream.Position : -1, excelStream.CanSeek, excelStream.CanRead);

            using (var workbook = new XLWorkbook(excelStream))
            {
                // Pick the worksheet with the most data (not just the first — could be a cover page)
                var worksheet = workbook.Worksheets.OrderByDescending(ws =>
                {
                    var r = ws.RangeUsed();
                    return r != null ? r.RowCount() * r.ColumnCount() : 0;
                }).First();

                // Use worksheet-level boundaries (more reliable than RangeUsed with merged cells)
                var lastRowUsed = worksheet.LastRowUsed();
                var lastColUsed = worksheet.LastColumnUsed();

                if (lastRowUsed == null || lastColUsed == null)
                {
                    _logger.LogWarning("[DEBUG] Excel has no data (LastRowUsed or LastColumnUsed is null)");
                    return new ParquetConversionResult
                    {
                        Success = false,
                        ErrorMessage = "Excel file has no data"
                    };
                }

                var lastRowNum = lastRowUsed.RowNumber();
                var lastColNum = lastColUsed.ColumnNumber();

                _logger.LogInformation("[DEBUG] Excel LastRowUsed={LastRow}, LastColumnUsed={LastCol}, Worksheet={Name}",
                    lastRowNum, lastColNum, worksheet.Name);

                // Smart header row detection: find the row with the most unique non-empty values
                var headerRowNumber = 1;
                var bestUniqueCount = 0;

                for (var row = 1; row <= Math.Min(10, lastRowNum); row++)
                {
                    var uniqueValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (var col = 1; col <= lastColNum; col++)
                    {
                        var cell = worksheet.Cell(row, col);
                        if (!cell.IsEmpty())
                        {
                            var val = cell.GetString().Trim();
                            if (!string.IsNullOrWhiteSpace(val))
                                uniqueValues.Add(val);
                        }
                    }

                    if (uniqueValues.Count > bestUniqueCount && uniqueValues.Count >= 2)
                    {
                        bestUniqueCount = uniqueValues.Count;
                        headerRowNumber = row;
                    }
                }

                // Determine actual column range from the header row
                var firstColNum = lastColNum;
                var actualLastCol = 1;
                for (var col = 1; col <= lastColNum; col++)
                {
                    if (!worksheet.Cell(headerRowNumber, col).IsEmpty())
                    {
                        if (col < firstColNum) firstColNum = col;
                        if (col > actualLastCol) actualLastCol = col;
                    }
                }
                lastColNum = actualLastCol;

                _logger.LogInformation("[DEBUG] Detected header row={HeaderRow}, columns {FirstCol}-{LastCol} ({Count} cols), bestUnique={Unique}",
                    headerRowNumber, firstColNum, lastColNum, lastColNum - firstColNum + 1, bestUniqueCount);

                await using var csvWriter = new StreamWriter(tempCsvPath, false, Encoding.UTF8);

                // Write header row
                var headerValues = new List<string>();
                for (var col = firstColNum; col <= lastColNum; col++)
                {
                    var cell = worksheet.Cell(headerRowNumber, col);
                    var value = cell.IsEmpty() ? $"Column{col}" : cell.GetString().Trim();
                    if (value.Contains(',') || value.Contains('\n') || value.Contains('"'))
                        value = "\"" + value.Replace("\"", "\"\"") + "\"";
                    headerValues.Add(value);
                }
                await csvWriter.WriteLineAsync(string.Join(",", headerValues));
                _logger.LogInformation("[DEBUG] CSV header: {Header}", string.Join(",", headerValues).Length > 300 ? string.Join(",", headerValues)[..300] + "..." : string.Join(",", headerValues));

                // Write data rows (starting after header)
                var dataRowCount = 0;
                for (var row = headerRowNumber + 1; row <= lastRowNum; row++)
                {
                    var values = new List<string>();
                    var hasData = false;
                    for (var col = firstColNum; col <= lastColNum; col++)
                    {
                        var cell = worksheet.Cell(row, col);
                        var value = cell.IsEmpty() ? "" : cell.GetFormattedString();
                        if (!string.IsNullOrWhiteSpace(value)) hasData = true;
                        if (value.Contains(',') || value.Contains('\n') || value.Contains('"'))
                            value = "\"" + value.Replace("\"", "\"\"") + "\"";
                        values.Add(value);
                    }

                    // Skip completely empty rows
                    if (!hasData) continue;

                    await csvWriter.WriteLineAsync(string.Join(",", values));
                    dataRowCount++;

                    if (dataRowCount <= 2)
                    {
                        _logger.LogInformation("[DEBUG] CSV data row {Row}: {Values}", dataRowCount,
                            string.Join(",", values).Length > 200 ? string.Join(",", values)[..200] + "..." : string.Join(",", values));
                    }
                }

                _logger.LogInformation("[DEBUG] Wrote {DataRows} data rows to CSV", dataRowCount);
            }

            // Now convert the temp CSV to Parquet using the CSV method
            await using var csvStream2 = File.OpenRead(tempCsvPath);
            // We already have the CSV on disk, so use DuckDB directly
            await using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);

            var escapedCsv = EscapePath(tempCsvPath);
            var escapedParquet = EscapePath(outputPath);

            var copySql = $"COPY (SELECT * FROM read_csv_auto('{escapedCsv}', header=true, all_varchar=false, sample_size=-1)) TO '{escapedParquet}' (FORMAT PARQUET, COMPRESSION ZSTD)";
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = copySql;
                await cmd.ExecuteNonQueryAsync();
            }

            long parquetRowCount;
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM read_parquet('{escapedParquet}')";
                parquetRowCount = (long)(await cmd.ExecuteScalarAsync() ?? 0);
            }

            var schemaJson = await GetParquetSchemaAsync(outputPath, connection);
            var sampleJson = await GetSampleDataAsync(outputPath, 100, connection);

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
