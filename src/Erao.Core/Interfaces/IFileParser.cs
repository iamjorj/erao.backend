using Erao.Core.DTOs.File;
using Erao.Core.Enums;

namespace Erao.Core.Interfaces;

public interface IFileParser
{
    bool CanParse(FileType fileType);
    Task<FileParseResult> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default);
}

public class FileParseResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<ColumnInfo> Columns { get; set; } = new();
    public List<Dictionary<string, object?>> Data { get; set; } = new();
    public int RowCount { get; set; }
    public string? ParsedContentJson { get; set; }
    public string? SchemaInfoJson { get; set; }
}
