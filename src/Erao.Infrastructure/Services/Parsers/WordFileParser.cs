using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Erao.Core.DTOs.File;
using Erao.Core.Enums;
using Erao.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Erao.Infrastructure.Services.Parsers;

public class WordFileParser : IFileParser
{
    private readonly ILogger<WordFileParser> _logger;
    private const int MaxCharacters = 5000000; // 5MB text limit

    public WordFileParser(ILogger<WordFileParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(FileType fileType) => fileType == FileType.Word;

    public async Task<FileParseResult> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        var result = new FileParseResult();

        try
        {
            // Copy stream to memory for OpenXml processing
            using var memoryStream = new MemoryStream();
            await fileStream.CopyToAsync(memoryStream, cancellationToken);
            memoryStream.Position = 0;

            using var wordDoc = WordprocessingDocument.Open(memoryStream, false);
            var body = wordDoc.MainDocumentPart?.Document.Body;

            if (body == null)
            {
                result.Success = true;
                result.RowCount = 0;
                result.ParsedContentJson = "[]";
                result.SchemaInfoJson = "[]";
                return result;
            }

            // First pass: check if document has tables with data
            var tables = body.Elements<Table>().ToList();
            var paragraphs = body.Elements<Paragraph>().ToList();
            var hasDataTables = tables.Any(t => t.Elements<TableRow>().Count() > 1);

            // If document has a significant data table, extract it as proper tabular data
            if (hasDataTables)
            {
                var tableResult = ExtractLargestTable(tables, fileName);
                if (tableResult != null && tableResult.RowCount > 0)
                {
                    // Also store the full text as a metadata field for document Q&A
                    var fullText = ExtractFullText(body, cancellationToken);
                    tableResult.Data.ForEach(row => row["_document_text"] = null); // placeholder

                    // Actually, don't pollute table data. Just return clean table.
                    result = tableResult;
                    _logger.LogInformation("Parsed Word file {FileName} as table: {RowCount} rows, {ColCount} columns",
                        fileName, result.RowCount, result.Columns.Count);
                    return result;
                }
            }

            // No significant tables — extract document content as rows
            var rows = new List<Dictionary<string, object?>>();
            var sectionNumber = 0;
            var totalCharacters = 0;

            foreach (var element in body.Elements())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (totalCharacters >= MaxCharacters)
                {
                    _logger.LogWarning("Document {FileName} exceeded maximum character limit", fileName);
                    break;
                }

                if (element is Paragraph paragraph)
                {
                    var text = GetParagraphText(paragraph);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sectionNumber++;
                        rows.Add(new Dictionary<string, object?>
                        {
                            ["section"] = sectionNumber,
                            ["type"] = GetParagraphType(paragraph),
                            ["content"] = text
                        });
                        totalCharacters += text.Length;
                    }
                }
                else if (element is Table table)
                {
                    // Inline table rows into the document flow
                    var tableRows = table.Elements<TableRow>().ToList();
                    if (tableRows.Count > 0)
                    {
                        sectionNumber++;
                        foreach (var row in tableRows)
                        {
                            var cells = row.Elements<TableCell>().Select(cell =>
                            {
                                var cellText = new StringBuilder();
                                foreach (var para in cell.Elements<Paragraph>())
                                {
                                    if (cellText.Length > 0) cellText.Append(" ");
                                    cellText.Append(GetParagraphText(para));
                                }
                                return cellText.ToString();
                            }).ToList();

                            var rowContent = string.Join(" | ", cells);
                            rows.Add(new Dictionary<string, object?>
                            {
                                ["section"] = sectionNumber,
                                ["type"] = "table_row",
                                ["content"] = rowContent
                            });
                            totalCharacters += rowContent.Length;
                        }
                    }
                }
            }

            var columns = new List<ColumnInfo>
            {
                new() { Name = "section", DataType = "number", IsNullable = false },
                new() { Name = "type", DataType = "string", IsNullable = false },
                new() { Name = "content", DataType = "string", IsNullable = false }
            };

            result.Columns = columns;
            result.Data = rows;
            result.RowCount = rows.Count;
            result.Success = true;

            result.ParsedContentJson = JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = false });
            result.SchemaInfoJson = JsonSerializer.Serialize(columns, new JsonSerializerOptions { WriteIndented = false });

            _logger.LogInformation("Successfully parsed Word file {FileName}: {RowCount} sections, {CharCount} characters",
                fileName, rows.Count, totalCharacters);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing Word file {FileName}", fileName);
            result.Success = false;
            result.ErrorMessage = $"Failed to parse Word file: {ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// Extracts the largest table in the document as proper tabular data.
    /// First row is treated as headers.
    /// </summary>
    private FileParseResult? ExtractLargestTable(List<Table> tables, string fileName)
    {
        Table? largest = null;
        int maxRows = 0;

        foreach (var table in tables)
        {
            var rowCount = table.Elements<TableRow>().Count();
            if (rowCount > maxRows)
            {
                maxRows = rowCount;
                largest = table;
            }
        }

        if (largest == null || maxRows < 2) return null; // Need at least header + 1 data row

        var tableRows = largest.Elements<TableRow>().ToList();
        var headerRow = tableRows[0];
        var headerCells = headerRow.Elements<TableCell>().Select(GetCellText).ToList();

        // Build unique column names
        var columnNames = new List<string>();
        for (int i = 0; i < headerCells.Count; i++)
        {
            var name = string.IsNullOrWhiteSpace(headerCells[i]) ? $"Column{i + 1}" : headerCells[i].Trim();
            var baseName = name;
            var counter = 1;
            while (columnNames.Contains(name))
                name = $"{baseName}_{counter++}";
            columnNames.Add(name);
        }

        var columns = columnNames.Select(n => new ColumnInfo
        {
            Name = n,
            DataType = "string",
            IsNullable = true
        }).ToList();

        // Parse data rows
        var rows = new List<Dictionary<string, object?>>();
        for (int i = 1; i < tableRows.Count; i++)
        {
            var cells = tableRows[i].Elements<TableCell>().Select(GetCellText).ToList();
            var row = new Dictionary<string, object?>();
            var hasData = false;

            for (int j = 0; j < columnNames.Count; j++)
            {
                var value = j < cells.Count ? cells[j].Trim() : null;
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

        // Infer types
        InferDataTypes(columns, rows);

        return new FileParseResult
        {
            Success = true,
            Columns = columns,
            Data = rows,
            RowCount = rows.Count,
            ParsedContentJson = JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = false }),
            SchemaInfoJson = JsonSerializer.Serialize(columns, new JsonSerializerOptions { WriteIndented = false })
        };
    }

    private string ExtractFullText(Body body, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        foreach (var para in body.Elements<Paragraph>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = GetParagraphText(para);
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine(text);
            }
        }
        return sb.ToString();
    }

    private string GetCellText(TableCell cell)
    {
        var sb = new StringBuilder();
        foreach (var para in cell.Elements<Paragraph>())
        {
            if (sb.Length > 0) sb.Append(" ");
            sb.Append(GetParagraphText(para));
        }
        return sb.ToString();
    }

    private string GetParagraphText(Paragraph paragraph)
    {
        var sb = new StringBuilder();

        foreach (var run in paragraph.Elements<Run>())
        {
            foreach (var text in run.Elements<Text>())
            {
                sb.Append(text.Text);
            }
        }

        return sb.ToString().Trim();
    }

    private string GetParagraphType(Paragraph paragraph)
    {
        var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;

        if (string.IsNullOrEmpty(style))
            return "paragraph";

        return style.ToLower() switch
        {
            "heading1" or "title" => "heading",
            "heading2" or "subtitle" => "heading",
            "heading3" => "heading",
            "heading4" => "heading",
            "heading5" => "heading",
            "heading6" => "heading",
            "listparagraph" => "list",
            _ => "paragraph"
        };
    }

    private object? ParseValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        if (double.TryParse(value, out var d)) return d;
        if (bool.TryParse(value, out var b)) return b;

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

            if (allNumbers) column.DataType = "number";
            else if (allBooleans) column.DataType = "boolean";
            else column.DataType = "string";

            column.IsNullable = data.Any(row => row.GetValueOrDefault(column.Name) == null);
        }
    }
}
