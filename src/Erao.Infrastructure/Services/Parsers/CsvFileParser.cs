using System.Globalization;
using System.Text;
using System.Text.Json;
using Erao.Core.DTOs.File;
using Erao.Core.Enums;
using Erao.Core.Interfaces;

namespace Erao.Infrastructure.Services.Parsers;

public class CsvFileParser : IFileParser
{
    private const int MaxRowsToProcess = 100000;

    public bool CanParse(FileType fileType) => fileType == FileType.Csv;

    public async Task<FileParseResult> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        var result = new FileParseResult();

        try
        {
            using var reader = new StreamReader(fileStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var firstLine = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(firstLine))
            {
                result.Success = true;
                result.RowCount = 0;
                result.ParsedContentJson = "[]";
                result.SchemaInfoJson = "[]";
                return result;
            }

            var delimiter = DetectDelimiter(firstLine);
            var firstRowFields = ParseCsvLine(firstLine, delimiter);

            // If first line has only 1 field, it might be a title row — peek at next line
            string? secondLine = null;
            if (firstRowFields.Count <= 1)
            {
                secondLine = await reader.ReadLineAsync(cancellationToken);
                if (!string.IsNullOrEmpty(secondLine))
                {
                    var nextDelimiter = DetectDelimiter(secondLine);
                    var nextFields = ParseCsvLine(secondLine, nextDelimiter);
                    if (nextFields.Count > firstRowFields.Count)
                    {
                        firstRowFields = nextFields;
                        delimiter = nextDelimiter;
                        secondLine = null;
                    }
                }
            }

            // Buffer next ~15 data lines to detect if first row is a header or data
            var bufferedLines = new List<string>();
            if (secondLine != null && !string.IsNullOrWhiteSpace(secondLine))
                bufferedLines.Add(secondLine);

            for (int i = bufferedLines.Count; i < 15; i++)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) break;
                if (!string.IsNullOrWhiteSpace(line))
                    bufferedLines.Add(line);
            }

            var bufferedRows = bufferedLines.Select(l => ParseCsvLine(l, delimiter)).ToList();
            var hasHeader = DetectHasHeader(firstRowFields, bufferedRows);

            List<string> columnNames;
            List<List<string>> initialDataRows;

            if (hasHeader)
            {
                columnNames = BuildColumnNames(firstRowFields);
                initialDataRows = bufferedRows;
            }
            else
            {
                columnNames = Enumerable.Range(1, firstRowFields.Count).Select(i => $"Column{i}").ToList();
                initialDataRows = new List<List<string>> { firstRowFields };
                initialDataRows.AddRange(bufferedRows);
            }

            var columns = columnNames.Select(name => new ColumnInfo
            {
                Name = name,
                DataType = "string",
                IsNullable = true
            }).ToList();

            result.Columns = columns;

            var data = new List<Dictionary<string, object?>>();
            var rowCount = 0;

            foreach (var values in initialDataRows)
            {
                if (rowCount >= MaxRowsToProcess) break;
                var rowData = BuildRowData(values, columnNames);
                if (rowData != null)
                {
                    data.Add(rowData);
                    rowCount++;
                }
            }

            string? remainingLine;
            while ((remainingLine = await reader.ReadLineAsync(cancellationToken)) != null && rowCount < MaxRowsToProcess)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(remainingLine)) continue;

                var values = ParseCsvLine(remainingLine, delimiter);
                var rowData = BuildRowData(values, columnNames);
                if (rowData != null)
                {
                    data.Add(rowData);
                    rowCount++;
                }
            }

            result.Data = data;
            result.RowCount = rowCount;

            InferDataTypes(result.Columns, data);

            result.ParsedContentJson = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = false });
            result.SchemaInfoJson = JsonSerializer.Serialize(columns, new JsonSerializerOptions { WriteIndented = false });
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"Failed to parse CSV file: {ex.Message}";
        }

        return result;
    }

    private static List<string> BuildColumnNames(List<string> headerFields)
    {
        var columnNames = new List<string>();
        for (int i = 0; i < headerFields.Count; i++)
        {
            var name = headerFields[i].Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = $"Column{i + 1}";

            var baseName = name;
            var counter = 1;
            while (columnNames.Contains(name))
                name = $"{baseName}_{counter++}";

            columnNames.Add(name);
        }
        return columnNames;
    }

    private Dictionary<string, object?>? BuildRowData(List<string> values, List<string> columnNames)
    {
        var rowData = new Dictionary<string, object?>();
        var hasData = false;

        for (int i = 0; i < columnNames.Count; i++)
        {
            var value = i < values.Count ? values[i].Trim() : null;
            if (!string.IsNullOrEmpty(value))
            {
                rowData[columnNames[i]] = ParseValue(value);
                hasData = true;
            }
            else
            {
                rowData[columnNames[i]] = null;
            }
        }

        return hasData ? rowData : null;
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

    private char DetectDelimiter(string line)
    {
        var delimiters = new[] { ',', ';', '\t', '|' };
        var maxCount = 0;
        var bestDelimiter = ',';

        foreach (var delimiter in delimiters)
        {
            var count = line.Count(c => c == delimiter);
            if (count > maxCount)
            {
                maxCount = count;
                bestDelimiter = delimiter;
            }
        }

        return bestDelimiter;
    }

    private List<string> ParseCsvLine(string line, char delimiter)
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

    private object? ParseValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (double.TryParse(value, out var doubleValue))
            return doubleValue;

        if (bool.TryParse(value, out var boolValue))
            return boolValue;

        if (DateTime.TryParse(value, out var dateValue))
            return dateValue.ToString("yyyy-MM-dd HH:mm:ss");

        return value;
    }

    private void InferDataTypes(List<ColumnInfo> columns, List<Dictionary<string, object?>> data)
    {
        foreach (var column in columns)
        {
            var values = data
                .Select(row => row.GetValueOrDefault(column.Name))
                .Where(v => v != null)
                .Take(100)
                .ToList();

            if (!values.Any())
            {
                column.DataType = "string";
                column.IsNullable = true;
                continue;
            }

            var firstValue = values.First();
            if (firstValue is double)
            {
                column.DataType = "number";
            }
            else if (firstValue is bool)
            {
                column.DataType = "boolean";
            }
            else
            {
                column.DataType = "string";
            }

            column.IsNullable = data.Any(row => row.GetValueOrDefault(column.Name) == null);
        }
    }
}
