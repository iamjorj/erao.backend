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

            var documentContent = new List<DocumentSection>();
            var currentSection = new DocumentSection { Type = "paragraph", Content = "" };
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
                        var section = new DocumentSection
                        {
                            Type = GetParagraphType(paragraph),
                            Content = text
                        };
                        documentContent.Add(section);
                        totalCharacters += text.Length;
                    }
                }
                else if (element is Table table)
                {
                    var tableData = ParseTable(table);
                    if (tableData.Any())
                    {
                        var section = new DocumentSection
                        {
                            Type = "table",
                            TableData = tableData
                        };
                        documentContent.Add(section);
                        totalCharacters += JsonSerializer.Serialize(tableData).Length;
                    }
                }
            }

            // Create columns info for document structure
            result.Columns = new List<ColumnInfo>
            {
                new() { Name = "type", DataType = "string", IsNullable = false },
                new() { Name = "content", DataType = "string", IsNullable = true },
                new() { Name = "tableData", DataType = "object", IsNullable = true }
            };

            // Convert to data format
            result.Data = documentContent.Select(section => new Dictionary<string, object?>
            {
                ["type"] = section.Type,
                ["content"] = section.Content,
                ["tableData"] = section.TableData
            }).ToList();

            result.RowCount = documentContent.Count;

            // Create a more readable parsed content
            var parsedContent = new
            {
                sections = documentContent,
                fullText = string.Join("\n\n", documentContent
                    .Where(s => s.Type != "table" && !string.IsNullOrEmpty(s.Content))
                    .Select(s => s.Content)),
                tableCount = documentContent.Count(s => s.Type == "table"),
                paragraphCount = documentContent.Count(s => s.Type == "paragraph"),
                headingCount = documentContent.Count(s => s.Type.StartsWith("heading"))
            };

            result.ParsedContentJson = JsonSerializer.Serialize(parsedContent, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            result.SchemaInfoJson = JsonSerializer.Serialize(new
            {
                documentType = "word",
                sectionCount = documentContent.Count,
                hasTable = documentContent.Any(s => s.Type == "table"),
                characterCount = totalCharacters
            });

            result.Success = true;

            _logger.LogInformation("Successfully parsed Word file {FileName}: {SectionCount} sections, {CharCount} characters",
                fileName, documentContent.Count, totalCharacters);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing Word file {FileName}", fileName);
            result.Success = false;
            result.ErrorMessage = $"Failed to parse Word file: {ex.Message}";
        }

        return result;
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
            "heading1" or "title" => "heading1",
            "heading2" or "subtitle" => "heading2",
            "heading3" => "heading3",
            "heading4" => "heading4",
            "heading5" => "heading5",
            "heading6" => "heading6",
            "listparagraph" => "list",
            _ => "paragraph"
        };
    }

    private List<List<string>> ParseTable(Table table)
    {
        var tableData = new List<List<string>>();

        foreach (var row in table.Elements<TableRow>())
        {
            var rowData = new List<string>();

            foreach (var cell in row.Elements<TableCell>())
            {
                var cellText = new StringBuilder();

                foreach (var para in cell.Elements<Paragraph>())
                {
                    if (cellText.Length > 0)
                        cellText.Append(" ");
                    cellText.Append(GetParagraphText(para));
                }

                rowData.Add(cellText.ToString());
            }

            if (rowData.Any())
                tableData.Add(rowData);
        }

        return tableData;
    }

    private class DocumentSection
    {
        public string Type { get; set; } = string.Empty;
        public string? Content { get; set; }
        public List<List<string>>? TableData { get; set; }
    }
}
