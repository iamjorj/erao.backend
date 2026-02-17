using System.Text.Json;
using System.Xml.Linq;
using Erao.Core.DTOs.File;
using Erao.Core.Enums;
using Erao.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Erao.Infrastructure.Services.Parsers;

public class XmlFileParser : IFileParser
{
    private readonly ILogger<XmlFileParser> _logger;
    private const int MaxRowsToProcess = 100000;

    public XmlFileParser(ILogger<XmlFileParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(FileType fileType) => fileType == FileType.Xml;

    public async Task<FileParseResult> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        var result = new FileParseResult();

        try
        {
            var xdoc = await XDocument.LoadAsync(fileStream, LoadOptions.None, cancellationToken);
            var root = xdoc.Root;

            if (root == null)
            {
                result.Success = true;
                result.RowCount = 0;
                result.ParsedContentJson = "[]";
                result.SchemaInfoJson = "[]";
                return result;
            }

            // Find the repeating element group (the most common child element name)
            var childGroups = root.Elements()
                .GroupBy(e => e.Name.LocalName)
                .OrderByDescending(g => g.Count())
                .ToList();

            if (!childGroups.Any())
            {
                result.Success = true;
                result.RowCount = 0;
                result.ParsedContentJson = "[]";
                result.SchemaInfoJson = "[]";
                return result;
            }

            // Use the largest group as rows
            var rowElements = childGroups.First().Take(MaxRowsToProcess).ToList();

            var rows = new List<Dictionary<string, object?>>();
            var allColumns = new List<string>();
            var columnSet = new HashSet<string>();

            foreach (var rowElement in rowElements)
            {
                var row = new Dictionary<string, object?>();

                // Extract attributes
                foreach (var attr in rowElement.Attributes())
                {
                    var key = $"@{attr.Name.LocalName}";
                    row[key] = ParseValue(attr.Value);
                    if (columnSet.Add(key))
                        allColumns.Add(key);
                }

                // Extract child elements
                foreach (var child in rowElement.Elements())
                {
                    var key = child.Name.LocalName;

                    if (child.HasElements)
                    {
                        // Nested element — flatten one level
                        foreach (var nested in child.Elements())
                        {
                            var nestedKey = $"{key}.{nested.Name.LocalName}";
                            row[nestedKey] = ParseValue(nested.Value);
                            if (columnSet.Add(nestedKey))
                                allColumns.Add(nestedKey);
                        }
                    }
                    else
                    {
                        row[key] = ParseValue(child.Value);
                        if (columnSet.Add(key))
                            allColumns.Add(key);
                    }
                }

                // If no child elements and no attributes, use text content
                if (!row.Any() && !string.IsNullOrWhiteSpace(rowElement.Value))
                {
                    row["value"] = ParseValue(rowElement.Value);
                    if (columnSet.Add("value"))
                        allColumns.Add("value");
                }

                rows.Add(row);
            }

            // Normalize — ensure every row has all columns
            foreach (var row in rows)
            {
                foreach (var col in allColumns)
                {
                    if (!row.ContainsKey(col))
                        row[col] = null;
                }
            }

            var columns = allColumns.Select(name => new ColumnInfo
            {
                Name = name,
                DataType = "string",
                IsNullable = true
            }).ToList();

            InferDataTypes(columns, rows);

            result.Data = rows;
            result.Columns = columns;
            result.RowCount = rows.Count;
            result.Success = true;

            result.ParsedContentJson = JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = false });
            result.SchemaInfoJson = JsonSerializer.Serialize(columns, new JsonSerializerOptions { WriteIndented = false });

            _logger.LogInformation("Successfully parsed XML file {FileName}: {RowCount} rows, {ColumnCount} columns",
                fileName, rows.Count, columns.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing XML file {FileName}", fileName);
            result.Success = false;
            result.ErrorMessage = $"Failed to parse XML file: {ex.Message}";
        }

        return result;
    }

    private object? ParseValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();

        if (double.TryParse(trimmed, out var d))
            return d;

        if (bool.TryParse(trimmed, out var b))
            return b;

        return trimmed;
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
