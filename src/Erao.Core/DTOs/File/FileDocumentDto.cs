using Erao.Core.Entities;
using Erao.Core.Enums;

namespace Erao.Core.DTOs.File;

public class FileDocumentDto
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public FileType FileType { get; set; }
    public long FileSizeBytes { get; set; }
    public int? RowCount { get; set; }
    public FileProcessingStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string>? Columns { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class FileUploadResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public FileDocumentDto? File { get; set; }
}

public class FileListResponse
{
    public List<FileDocumentDto> Files { get; set; } = new();
    public int TotalCount { get; set; }
}

public class FileContentResponse
{
    public Guid FileId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public FileType FileType { get; set; }
    public List<string> Columns { get; set; } = new();
    public List<Dictionary<string, object?>> Data { get; set; } = new();
    public int TotalRows { get; set; }
    public int PageSize { get; set; }
    public int CurrentPage { get; set; }
}

public class FileQueryRequest
{
    public Guid FileId { get; set; }
    public string Query { get; set; } = string.Empty;
}

public class FileSchemaResponse
{
    public Guid FileId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public FileType FileType { get; set; }
    public List<ColumnInfo> Columns { get; set; } = new();
    public int TotalRows { get; set; }
    public string? SampleData { get; set; }
}

public class ColumnInfo
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public int? MaxLength { get; set; }
}
