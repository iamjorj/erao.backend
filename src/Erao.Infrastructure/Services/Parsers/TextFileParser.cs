using System.Text;
using System.Text.Json;
using Erao.Core.DTOs.File;
using Erao.Core.Enums;
using Erao.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Erao.Infrastructure.Services.Parsers;

public class TextFileParser : IFileParser
{
    private readonly ILogger<TextFileParser> _logger;
    private const int MaxRowsToProcess = 100000;

    public TextFileParser(ILogger<TextFileParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(FileType fileType) => fileType == FileType.Text;

    public async Task<FileParseResult> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        var result = new FileParseResult();

        try
        {
            using var reader = new StreamReader(fileStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var content = await reader.ReadToEndAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(content))
            {
                result.Success = true;
                result.RowCount = 0;
                result.ParsedContentJson = "[]";
                result.SchemaInfoJson = "[]";
                return result;
            }

            // Check if it's TSV (tab-separated) — treat like CSV with tab delimiter
            var isTsv = fileName.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase);
            var lines = content.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            if (lines.Count == 0)
            {
                result.Success = true;
                result.RowCount = 0;
                result.ParsedContentJson = "[]";
                result.SchemaInfoJson = "[]";
                return result;
            }

            // Detect if it's a delimited file (TSV or has consistent tab/pipe separators)
            var firstLine = lines[0];
            char? delimiter = null;

            if (isTsv || firstLine.Contains('\t'))
                delimiter = '\t';
            else if (lines.Count > 1)
            {
                // Check for consistent pipe-delimited format
                var pipeCount = firstLine.Count(c => c == '|');
                if (pipeCount > 0 && lines.Skip(1).Take(5).All(l => l.Count(c => c == '|') == pipeCount))
                    delimiter = '|';
            }

            if (delimiter.HasValue)
            {
                // Parse as delimited tabular data
                return ParseDelimited(lines, delimiter.Value, fileName);
            }

            // Plain text: store each line as a row with a "line" and "content" column
            var rows = new List<Dictionary<string, object?>>();
            for (int i = 0; i < lines.Count && i < MaxRowsToProcess; i++)
            {
                rows.Add(new Dictionary<string, object?>
                {
                    { "line", i + 1 },
                    { "content", lines[i].TrimEnd('\r') }
                });
            }

            var columns = new List<ColumnInfo>
            {
                new() { Name = "line", DataType = "number", IsNullable = false },
                new() { Name = "content", DataType = "string", IsNullable = false }
            };

            result.Data = rows;
            result.Columns = columns;
            result.RowCount = rows.Count;
            result.Success = true;

            result.ParsedContentJson = JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = false });
            result.SchemaInfoJson = JsonSerializer.Serialize(columns, new JsonSerializerOptions { WriteIndented = false });

            _logger.LogInformation("Successfully parsed text file {FileName}: {RowCount} lines", fileName, rows.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing text file {FileName}", fileName);
            result.Success = false;
            result.ErrorMessage = $"Failed to parse text file: {ex.Message}";
        }

        return result;
    }

    private FileParseResult ParseDelimited(List<string> lines, char delimiter, string fileName)
    {
        var result = new FileParseResult();

        var headers = lines[0].TrimEnd('\r').Split(delimiter).Select(h => h.Trim()).ToList();

        // Ensure unique column names
        var columnNames = new List<string>();
        for (int i = 0; i < headers.Count; i++)
        {
            var name = string.IsNullOrWhiteSpace(headers[i]) ? $"Column{i + 1}" : headers[i];
            var baseName = name;
            var counter = 1;
            while (columnNames.Contains(name))
                name = $"{baseName}_{counter++}";
            columnNames.Add(name);
        }

        var columns = columnNames.Select(name => new ColumnInfo
        {
            Name = name,
            DataType = "string",
            IsNullable = true
        }).ToList();

        var rows = new List<Dictionary<string, object?>>();
        for (int i = 1; i < lines.Count && rows.Count < MaxRowsToProcess; i++)
        {
            var values = lines[i].TrimEnd('\r').Split(delimiter);
            var row = new Dictionary<string, object?>();
            var hasData = false;

            for (int j = 0; j < columnNames.Count; j++)
            {
                var value = j < values.Length ? values[j].Trim() : null;
                if (!string.IsNullOrEmpty(value))
                {
                    row[columnNames[j]] = ParseValue(value);
                    hasData = true;
                }
                else
                {
                    row[columnNames[j]] = null;
                }
            }

            if (hasData)
                rows.Add(row);
        }

        InferDataTypes(columns, rows);

        result.Data = rows;
        result.Columns = columns;
        result.RowCount = rows.Count;
        result.Success = true;

        result.ParsedContentJson = JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = false });
        result.SchemaInfoJson = JsonSerializer.Serialize(columns, new JsonSerializerOptions { WriteIndented = false });

        _logger.LogInformation("Successfully parsed delimited text file {FileName}: {RowCount} rows, {ColumnCount} columns",
            fileName, rows.Count, columns.Count);

        return result;
    }

    private object? ParseValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (double.TryParse(value, out var d))
            return d;

        if (bool.TryParse(value, out var b))
            return b;

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

            var allNumbers = values.All(v => v is int or long or double or float or decimal);
            var allBooleans = values.All(v => v is bool);

            if (allNumbers)
                column.DataType = "number";
            else if (allBooleans)
                column.DataType = "boolean";
            else
                column.DataType = "string";

            column.IsNullable = data.Any(row => row.GetValueOrDefault(column.Name) == null);
        }
    }
}
