using System.Globalization;
using System.Text;
using System.Text.Json;
using DuckDB.NET.Data;
using Erao.Core.Interfaces;
using ExcelDataReader;

namespace Erao.Infrastructure.Services;

public class ParquetConversionService : IParquetConversionService
{
    public async Task<ParquetConversionResult> ConvertCsvToParquetAsync(Stream csvStream, string outputPath)
    {
        string? tempCsvPath = null;
        try
        {
            tempCsvPath = Path.GetTempFileName() + ".csv";

            await using (var fileStream = File.Create(tempCsvPath))
            {
                await csvStream.CopyToAsync(fileStream);
            }

            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);

            var (hasHeader, delimiter, columnCount) = await DetectCsvStructureAsync(tempCsvPath);

            await using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var escapedCsv = EscapePath(tempCsvPath);
            var escapedParquet = EscapePath(outputPath);

            var delimStr = delimiter == '\t' ? "\\t" : delimiter.ToString();
            string headerOption;
            if (hasHeader)
            {
                headerOption = "header=true";
            }
            else
            {
                var names = string.Join(", ", Enumerable.Range(1, columnCount).Select(i => $"'Column{i}'"));
                headerOption = $"header=false, names=[{names}]";
            }

            var copySql = $"COPY (SELECT * FROM read_csv('{escapedCsv}', {headerOption}, delim='{delimStr}', quote='\"', escape='\"', sample_size=20000, null_padding=true, ignore_errors=true, strict_mode=false, max_line_size=10000000)) TO '{escapedParquet}' (FORMAT PARQUET, COMPRESSION ZSTD)";
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = copySql;
                await cmd.ExecuteNonQueryAsync();
            }

            long rowCount;
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM read_parquet('{escapedParquet}')";
                var result = await cmd.ExecuteScalarAsync();
                rowCount = result != null ? Convert.ToInt64(result) : 0;
            }

            var schemaJson = await GetParquetSchemaAsync(outputPath, connection);
            var sampleJson = await GetSampleDataAsync(outputPath, 100, connection);

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
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            tempCsvPath = Path.GetTempFileName() + ".csv";
            var config = new ExcelReaderConfiguration { LeaveOpen = true };

            // Pass 1: scan ALL sheets to identify data sheets
            var sheetInfos = new List<(int index, string name, int colCount)>();
            {
                using var scanReader = ExcelReaderFactory.CreateReader(excelStream, config);
                var sheetIndex = 0;
                do
                {
                    var cols = scanReader.FieldCount;
                    var name = scanReader.Name ?? $"Sheet{sheetIndex + 1}";
                    if (cols >= 2)
                        sheetInfos.Add((sheetIndex, name, cols));
                    sheetIndex++;
                } while (scanReader.NextResult());
            }

            if (sheetInfos.Count == 0)
                return new ParquetConversionResult { Success = false, ErrorMessage = "Excel file has no data" };

            // Find all sheets with the same (best) column count → merge them
            var bestColCount = sheetInfos.Max(s => s.colCount);
            var sheetsToProcess = sheetInfos.Where(s => s.colCount == bestColCount).ToList();
            var addSheetColumn = sheetsToProcess.Count > 1;
            var processIndices = new HashSet<int>(sheetsToProcess.Select(s => s.index));

            // Pass 2: read matching sheets and write combined CSV
            excelStream.Position = 0;
            using (var reader = ExcelReaderFactory.CreateReader(excelStream, config))
            {
                await using var csvWriter = new StreamWriter(tempCsvPath, false, Encoding.UTF8, bufferSize: 65536);
                var sb = new StringBuilder(65536);
                var dataRowCount = 0;
                var headerWritten = false;
                var sheetIndex = 0;

                do
                {
                    if (!processIndices.Contains(sheetIndex)) { sheetIndex++; continue; }

                    var sheetName = sheetsToProcess.First(s => s.index == sheetIndex).name;
                    var fieldCount = reader.FieldCount;
                    if (fieldCount == 0) { sheetIndex++; continue; }

                    // Read first 20 rows for header detection
                    var headerCandidates = new List<string[]>();
                    for (var i = 0; i < 20 && reader.Read(); i++)
                    {
                        var row = new string[fieldCount];
                        for (var c = 0; c < fieldCount; c++)
                            row[c] = reader.IsDBNull(c) ? "" : FormatCellValue(reader, c);
                        headerCandidates.Add(row);
                    }

                    if (headerCandidates.Count == 0) { sheetIndex++; continue; }

                    // Pick candidate header row (most unique non-empty values)
                    var headerRowIndex = 0;
                    var bestUnique = 0;
                    for (var i = 0; i < headerCandidates.Count; i++)
                    {
                        var unique = headerCandidates[i]
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
                        if (unique > bestUnique && unique >= 2) { bestUnique = unique; headerRowIndex = i; }
                    }

                    // Verify with DetectHasHeader heuristic
                    bool hasHeader = true;
                    if (headerCandidates.Count > headerRowIndex + 1)
                    {
                        var candidateHeader = headerCandidates[headerRowIndex].ToList();
                        var candidateData = headerCandidates
                            .Skip(headerRowIndex + 1).Take(10)
                            .Select(r => r.ToList()).ToList();
                        hasHeader = DetectHasHeader(candidateHeader, candidateData);
                    }

                    string[] headers;
                    int dataStartIndex;
                    if (hasHeader)
                    {
                        headers = headerCandidates[headerRowIndex];
                        dataStartIndex = headerRowIndex + 1;
                    }
                    else
                    {
                        headers = Enumerable.Range(1, fieldCount).Select(i => $"Column{i}").ToArray();
                        dataStartIndex = 0;
                    }

                    // Write CSV header (once, from the first sheet)
                    if (!headerWritten)
                    {
                        if (addSheetColumn)
                            await csvWriter.WriteAsync("Sheet,");
                        for (var c = 0; c < fieldCount; c++)
                        {
                            if (c > 0) await csvWriter.WriteAsync(',');
                            var h = string.IsNullOrWhiteSpace(headers[c]) ? $"Column{c + 1}" : headers[c].Trim();
                            await csvWriter.WriteAsync(CsvEscape(h));
                        }
                        await csvWriter.WriteLineAsync();
                        headerWritten = true;
                    }

                    // Write buffered data rows
                    for (var i = dataStartIndex; i < headerCandidates.Count; i++)
                    {
                        WriteDataRow(sb, headerCandidates[i], fieldCount, addSheetColumn ? sheetName : null, ref dataRowCount);
                    }

                    // Stream remaining rows
                    while (reader.Read())
                    {
                        var row = new string[fieldCount];
                        var hasData = false;
                        for (var c = 0; c < fieldCount; c++)
                        {
                            if (reader.IsDBNull(c))
                                row[c] = "";
                            else
                            {
                                row[c] = FormatCellValue(reader, c);
                                hasData = true;
                            }
                        }
                        if (hasData)
                            WriteDataRow(sb, row, fieldCount, addSheetColumn ? sheetName : null, ref dataRowCount);

                        if (dataRowCount % 5000 == 0 && sb.Length > 0)
                        {
                            await csvWriter.WriteAsync(sb);
                            sb.Clear();
                        }
                    }

                    if (sb.Length > 0) { await csvWriter.WriteAsync(sb); sb.Clear(); }
                    sheetIndex++;
                } while (reader.NextResult());
            }

            // DuckDB: CSV → Parquet
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

            var schemaJson = await GetParquetSchemaAsync(outputPath, connection);
            var sampleJson = await GetSampleDataAsync(outputPath, 100, connection);

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

    private static void WriteDataRow(StringBuilder sb, string[] values, int fieldCount, string? sheetName, ref int rowCount)
    {
        var rowStart = sb.Length;
        var hasData = false;

        if (sheetName != null)
        {
            sb.Append(CsvEscape(sheetName));
            sb.Append(',');
        }

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
            try { File.Delete(parquetPath); } catch { /* best effort */ }
        }
        return Task.CompletedTask;
    }

    // ─── CSV structure detection ──────────────────────────────────────────

    private async Task<(bool hasHeader, char delimiter, int columnCount)> DetectCsvStructureAsync(string csvPath)
    {
        var lines = new List<string>();
        using (var reader = new StreamReader(csvPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            for (int i = 0; i < 20; i++)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(line);
            }
        }

        if (lines.Count == 0)
            return (true, ',', 0);

        var delimiter = DetectDelimiter(lines[0]);
        var firstRow = ParseCsvLine(lines[0], delimiter);
        var columnCount = firstRow.Count;

        if (lines.Count <= 1)
            return (true, delimiter, columnCount);

        if (firstRow.Count <= 1 && lines.Count > 1)
        {
            var secondDelimiter = DetectDelimiter(lines[1]);
            var secondRow = ParseCsvLine(lines[1], secondDelimiter);
            if (secondRow.Count > firstRow.Count)
            {
                delimiter = secondDelimiter;
                firstRow = secondRow;
                columnCount = secondRow.Count;
                lines = lines.Skip(1).ToList();
            }
        }

        var dataRows = lines.Skip(1).Select(l => ParseCsvLine(l, delimiter)).ToList();
        var hasHeader = DetectHasHeader(firstRow, dataRows);

        return (hasHeader, delimiter, columnCount);
    }

    private static bool DetectHasHeader(List<string> firstRow, List<List<string>> dataRows)
    {
        if (dataRows.Count == 0) return true;

        int colCount = firstRow.Count;
        int headerLikeScore = 0;
        int dataLikeScore = 0;

        for (int col = 0; col < colCount; col++)
        {
            string headerVal = firstRow[col].Trim();

            bool headerIsNumeric = double.TryParse(headerVal,
                NumberStyles.Any, CultureInfo.InvariantCulture, out _);

            int numericDataCount = 0;
            int totalDataCount = 0;
            foreach (var row in dataRows)
            {
                if (col < row.Count && !string.IsNullOrWhiteSpace(row[col]))
                {
                    totalDataCount++;
                    if (double.TryParse(row[col].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                        numericDataCount++;
                }
            }

            double numericRate = totalDataCount > 0 ? (double)numericDataCount / totalDataCount : 0;

            if (headerIsNumeric && numericRate > 0.5)
                dataLikeScore++;
            else if (!headerIsNumeric && numericRate > 0.5)
                headerLikeScore++;
            else if (headerIsNumeric && numericRate <= 0.5)
                dataLikeScore++;
            else
            {
                bool appearsInData = dataRows.Any(r => col < r.Count &&
                    string.Equals(r[col].Trim(), headerVal, StringComparison.OrdinalIgnoreCase));
                if (appearsInData)
                    dataLikeScore++;
                else
                    headerLikeScore++;
            }
        }

        var nonEmpty = firstRow
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim().ToLowerInvariant())
            .ToList();
        if (nonEmpty.Count > 3 && nonEmpty.Distinct().Count() < nonEmpty.Count * 0.7)
            dataLikeScore += 3;

        return headerLikeScore >= dataLikeScore;
    }

    private static char DetectDelimiter(string line)
    {
        var delimiters = new[] { ',', ';', '\t', '|' };
        var maxCount = 0;
        var bestDelimiter = ',';

        foreach (var d in delimiters)
        {
            var count = line.Count(c => c == d);
            if (count > maxCount)
            {
                maxCount = count;
                bestDelimiter = d;
            }
        }

        return bestDelimiter;
    }

    private static List<string> ParseCsvLine(string line, char delimiter)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == delimiter && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        values.Add(current.ToString());
        return values;
    }

    // ─── Parquet helpers ──────────────────────────────────────────────────

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
        return path.Replace("\\", "/").Replace("'", "''");
    }
}
