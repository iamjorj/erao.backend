using System.Text.Json;
using Erao.Core.DTOs.File;
using Erao.Core.Enums;
using Erao.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Erao.Infrastructure.Services.Parsers;

public class JsonFileParser : IFileParser
{
    private readonly ILogger<JsonFileParser> _logger;
    private const int MaxRowsToProcess = 100000;

    public JsonFileParser(ILogger<JsonFileParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(FileType fileType) => fileType == FileType.Json;

    public async Task<FileParseResult> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        var result = new FileParseResult();

        try
        {
            using var reader = new StreamReader(fileStream);
            var jsonContent = await reader.ReadToEndAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(jsonContent))
            {
                result.Success = true;
                result.RowCount = 0;
                result.ParsedContentJson = "[]";
                result.SchemaInfoJson = "[]";
                return result;
            }

            List<Dictionary<string, object?>> rows;

            // Try standard JSON first, fall back to NDJSON (newline-delimited JSON)
            try
            {
                using var doc = JsonDocument.Parse(jsonContent);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    rows = FlattenArray(root);
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    var arrayProp = FindLargestArrayProperty(root);
                    if (arrayProp.HasValue)
                    {
                        rows = FlattenArray(arrayProp.Value);
                    }
                    else
                    {
                        var flatRow = new Dictionary<string, object?>();
                        FlattenObject(root, "", flatRow);
                        rows = new List<Dictionary<string, object?>> { flatRow };
                    }
                }
                else
                {
                    rows = new List<Dictionary<string, object?>>
                    {
                        new() { { "value", GetJsonValue(root) } }
                    };
                }
            }
            catch (JsonException)
            {
                // Concatenated JSON / NDJSON: multiple JSON objects not wrapped in array
                _logger.LogInformation("Standard JSON parse failed for {FileName}, trying concatenated JSON format", fileName);
                rows = ParseConcatenatedJson(jsonContent);
            }

            // Limit rows
            if (rows.Count > MaxRowsToProcess)
                rows = rows.Take(MaxRowsToProcess).ToList();

            // Collect all unique column names across all rows
            var allColumns = new LinkedHashSet<string>();
            foreach (var row in rows)
            {
                foreach (var key in row.Keys)
                    allColumns.Add(key);
            }

            // Build column info with type inference
            var columns = allColumns.Select(name => new ColumnInfo
            {
                Name = name,
                DataType = "string",
                IsNullable = true
            }).ToList();

            InferDataTypes(columns, rows);

            // Normalize rows — ensure every row has all columns
            foreach (var row in rows)
            {
                foreach (var col in allColumns)
                {
                    if (!row.ContainsKey(col))
                        row[col] = null;
                }
            }

            result.Data = rows;
            result.Columns = columns;
            result.RowCount = rows.Count;
            result.Success = true;

            result.ParsedContentJson = JsonSerializer.Serialize(rows, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            result.SchemaInfoJson = JsonSerializer.Serialize(columns, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            _logger.LogInformation("Successfully parsed JSON file {FileName}: {RowCount} rows, {ColumnCount} columns",
                fileName, rows.Count, columns.Count);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Invalid JSON in file {FileName}", fileName);
            result.Success = false;
            result.ErrorMessage = $"Invalid JSON format: {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing JSON file {FileName}", fileName);
            result.Success = false;
            result.ErrorMessage = $"Failed to parse JSON file: {ex.Message}";
        }

        return result;
    }

    private List<Dictionary<string, object?>> ParseConcatenatedJson(string content)
    {
        var rows = new List<Dictionary<string, object?>>();

        // Strategy: find complete JSON objects by tracking brace depth
        int i = 0;
        while (i < content.Length && rows.Count < MaxRowsToProcess)
        {
            // Find next '{'
            while (i < content.Length && content[i] != '{') i++;
            if (i >= content.Length) break;

            int start = i;
            int depth = 0;
            bool inString = false;
            bool escaped = false;

            for (; i < content.Length; i++)
            {
                char c = content[i];

                if (escaped) { escaped = false; continue; }
                if (c == '\\' && inString) { escaped = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;

                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        // Found complete object
                        var objectStr = content.Substring(start, i - start + 1);
                        i++;
                        try
                        {
                            using var objDoc = JsonDocument.Parse(objectStr);
                            var element = objDoc.RootElement;
                            if (element.ValueKind == JsonValueKind.Object)
                            {
                                var flatRow = new Dictionary<string, object?>();
                                FlattenObject(element, "", flatRow);
                                rows.Add(flatRow);
                            }
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogDebug("Skipping malformed JSON object at position {Pos}: {Error}", start, ex.Message);
                        }
                        break; // break inner for-loop, outer while-loop continues
                    }
                }
            }
        }

        _logger.LogInformation("Parsed concatenated JSON: {RowCount} objects extracted", rows.Count);
        return rows;
    }

    private List<Dictionary<string, object?>> FlattenArray(JsonElement array)
    {
        var rows = new List<Dictionary<string, object?>>();

        foreach (var element in array.EnumerateArray())
        {
            if (rows.Count >= MaxRowsToProcess)
                break;

            if (element.ValueKind == JsonValueKind.Object)
            {
                var flatRow = new Dictionary<string, object?>();
                FlattenObject(element, "", flatRow);
                rows.Add(flatRow);
            }
            else
            {
                // Array of primitives
                rows.Add(new Dictionary<string, object?> { { "value", GetJsonValue(element) } });
            }
        }

        return rows;
    }

    private void FlattenObject(JsonElement obj, string prefix, Dictionary<string, object?> row, int depth = 0)
    {
        if (depth > 5) return; // Prevent deeply nested explosion

        foreach (var prop in obj.EnumerateObject())
        {
            var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";

            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    FlattenObject(prop.Value, key, row, depth + 1);
                    break;

                case JsonValueKind.Array:
                    // For arrays inside objects, check if it's an array of primitives
                    var arrayValues = new List<string>();
                    var isSimple = true;
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object || item.ValueKind == JsonValueKind.Array)
                        {
                            isSimple = false;
                            break;
                        }
                        arrayValues.Add(item.ToString());
                    }

                    if (isSimple)
                    {
                        row[key] = string.Join(", ", arrayValues);
                    }
                    else
                    {
                        row[key] = prop.Value.GetRawText();
                    }
                    break;

                default:
                    row[key] = GetJsonValue(prop.Value);
                    break;
            }
        }
    }

    private JsonElement? FindLargestArrayProperty(JsonElement obj)
    {
        JsonElement? largest = null;
        int maxLength = 0;

        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                var length = prop.Value.GetArrayLength();
                if (length > maxLength)
                {
                    maxLength = length;
                    largest = prop.Value;
                }
            }
        }

        // Only use the array if it has at least 1 element
        return maxLength > 0 ? largest : null;
    }

    private object? GetJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
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

            // Check all sampled values for consistent type
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

    /// <summary>
    /// Maintains insertion order like LinkedHashSet in Java.
    /// </summary>
    private class LinkedHashSet<T> : IEnumerable<T> where T : notnull
    {
        private readonly HashSet<T> _set = new();
        private readonly List<T> _list = new();

        public void Add(T item)
        {
            if (_set.Add(item))
                _list.Add(item);
        }

        public IEnumerator<T> GetEnumerator() => _list.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
