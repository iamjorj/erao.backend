using System.Text.Json;
using ClosedXML.Excel;
using Erao.Core.DTOs.File;
using Erao.Core.Enums;
using Erao.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Erao.Infrastructure.Services.Parsers;

public class ExcelFileParser : IFileParser
{
    private readonly ILogger<ExcelFileParser> _logger;
    private const int MaxRowsToProcess = 100000; // Limit for very large files

    public ExcelFileParser(ILogger<ExcelFileParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(FileType fileType) => fileType == FileType.Excel;

    public async Task<FileParseResult> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        var result = new FileParseResult();

        try
        {
            using var workbook = new XLWorkbook(fileStream);
            var worksheet = workbook.Worksheets.First();

            // Get the used range
            var usedRange = worksheet.RangeUsed();
            if (usedRange == null)
            {
                result.Success = true;
                result.RowCount = 0;
                result.ParsedContentJson = "[]";
                result.SchemaInfoJson = "[]";
                return result;
            }

            var firstRow = usedRange.FirstRow();
            var lastRow = usedRange.LastRow();
            var firstColumn = usedRange.FirstColumn();
            var lastColumn = usedRange.LastColumn();

            // Extract column headers (first row)
            var columns = new List<ColumnInfo>();
            var columnNames = new List<string>();

            for (int col = firstColumn.ColumnNumber(); col <= lastColumn.ColumnNumber(); col++)
            {
                var cell = worksheet.Cell(firstRow.RowNumber(), col);
                var columnName = cell.GetString();

                if (string.IsNullOrWhiteSpace(columnName))
                    columnName = $"Column{col}";

                // Ensure unique column names
                var baseName = columnName;
                var counter = 1;
                while (columnNames.Contains(columnName))
                {
                    columnName = $"{baseName}_{counter++}";
                }

                columnNames.Add(columnName);
                columns.Add(new ColumnInfo
                {
                    Name = columnName,
                    DataType = "string", // Will be inferred from data
                    IsNullable = true
                });
            }

            result.Columns = columns;

            // Extract data rows
            var data = new List<Dictionary<string, object?>>();
            var rowCount = 0;
            var startDataRow = firstRow.RowNumber() + 1; // Skip header row

            for (int row = startDataRow; row <= lastRow.RowNumber() && rowCount < MaxRowsToProcess; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var rowData = new Dictionary<string, object?>();
                var hasData = false;

                for (int col = 0; col < columnNames.Count; col++)
                {
                    var cell = worksheet.Cell(row, firstColumn.ColumnNumber() + col);
                    var value = GetCellValue(cell);
                    rowData[columnNames[col]] = value;

                    if (value != null)
                        hasData = true;
                }

                if (hasData)
                {
                    data.Add(rowData);
                    rowCount++;
                }
            }

            result.Data = data;
            result.RowCount = rowCount;

            // Infer data types from actual data
            InferDataTypes(result.Columns, data);

            // Serialize to JSON
            result.ParsedContentJson = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            result.SchemaInfoJson = JsonSerializer.Serialize(columns, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            result.Success = true;

            _logger.LogInformation("Successfully parsed Excel file {FileName}: {RowCount} rows, {ColumnCount} columns",
                fileName, rowCount, columns.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing Excel file {FileName}", fileName);
            result.Success = false;
            result.ErrorMessage = $"Failed to parse Excel file: {ex.Message}";
        }

        return result;
    }

    private object? GetCellValue(IXLCell cell)
    {
        if (cell.IsEmpty())
            return null;

        return cell.DataType switch
        {
            XLDataType.Boolean => cell.GetBoolean(),
            XLDataType.Number => cell.GetDouble(),
            XLDataType.DateTime => cell.GetDateTime().ToString("yyyy-MM-dd HH:mm:ss"),
            XLDataType.TimeSpan => cell.GetTimeSpan().ToString(),
            _ => cell.GetString()
        };
    }

    private void InferDataTypes(List<ColumnInfo> columns, List<Dictionary<string, object?>> data)
    {
        foreach (var column in columns)
        {
            var values = data
                .Select(row => row.GetValueOrDefault(column.Name))
                .Where(v => v != null)
                .Take(100) // Sample first 100 non-null values
                .ToList();

            if (!values.Any())
            {
                column.DataType = "string";
                column.IsNullable = true;
                continue;
            }

            // Check if all values are of the same type
            var firstValue = values.First();
            if (firstValue is double || firstValue is int || firstValue is long)
            {
                column.DataType = "number";
            }
            else if (firstValue is bool)
            {
                column.DataType = "boolean";
            }
            else if (firstValue is DateTime)
            {
                column.DataType = "datetime";
            }
            else
            {
                column.DataType = "string";
            }

            column.IsNullable = data.Any(row => row.GetValueOrDefault(column.Name) == null);
        }
    }
}
