using System.Text;
using System.Text.Json;
using Erao.Core.DTOs.File;
using Erao.Core.Enums;
using Erao.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Erao.Infrastructure.Services.Parsers;

public class CsvFileParser : IFileParser
{
    private readonly ILogger<CsvFileParser> _logger;
    private const int MaxRowsToProcess = 100000;

    public CsvFileParser(ILogger<CsvFileParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(FileType fileType) => fileType == FileType.Csv;

    public async Task<FileParseResult> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        var result = new FileParseResult();

        try
        {
            using var reader = new StreamReader(fileStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            // Detect delimiter
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

            // Parse header
            var headers = ParseCsvLine(firstLine, delimiter);
            var columnNames = new List<string>();

            for (int i = 0; i < headers.Count; i++)
            {
                var columnName = headers[i].Trim();
                if (string.IsNullOrWhiteSpace(columnName))
                    columnName = $"Column{i + 1}";

                var baseName = columnName;
                var counter = 1;
                while (columnNames.Contains(columnName))
                {
                    columnName = $"{baseName}_{counter++}";
                }
                columnNames.Add(columnName);
            }

            var columns = columnNames.Select(name => new ColumnInfo
            {
                Name = name,
                DataType = "string",
                IsNullable = true
            }).ToList();

            result.Columns = columns;

            // Parse data rows
            var data = new List<Dictionary<string, object?>>();
            var rowCount = 0;
            string? line;

            while ((line = await reader.ReadLineAsync(cancellationToken)) != null && rowCount < MaxRowsToProcess)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var values = ParseCsvLine(line, delimiter);
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

                if (hasData)
                {
                    data.Add(rowData);
                    rowCount++;
                }
            }

            result.Data = data;
            result.RowCount = rowCount;

            // Infer data types
            InferDataTypes(result.Columns, data);

            result.ParsedContentJson = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            result.SchemaInfoJson = JsonSerializer.Serialize(columns, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            result.Success = true;

            _logger.LogInformation("Successfully parsed CSV file {FileName}: {RowCount} rows, {ColumnCount} columns",
                fileName, rowCount, columns.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing CSV file {FileName}", fileName);
            result.Success = false;
            result.ErrorMessage = $"Failed to parse CSV file: {ex.Message}";
        }

        return result;
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

        // Try parse as number
        if (double.TryParse(value, out var doubleValue))
            return doubleValue;

        // Try parse as boolean
        if (bool.TryParse(value, out var boolValue))
            return boolValue;

        // Try parse as date
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
