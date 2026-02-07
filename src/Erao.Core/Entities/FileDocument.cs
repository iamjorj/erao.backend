using Erao.Core.Enums;

namespace Erao.Core.Entities;

public class FileDocument : BaseEntity
{
    public Guid UserId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public FileType FileType { get; set; }
    public long FileSizeBytes { get; set; }
    public string? StoragePath { get; set; }

    // Parsed content stored as JSON for querying
    public string? ParsedContent { get; set; }

    // Schema/structure information (column headers for Excel, etc.)
    public string? SchemaInfo { get; set; }

    // Row count for tabular data
    public int? RowCount { get; set; }

    // Processing status
    public FileProcessingStatus Status { get; set; } = FileProcessingStatus.Pending;
    public string? ErrorMessage { get; set; }

    // Navigation properties
    public virtual User User { get; set; } = null!;
    public virtual ICollection<Conversation> Conversations { get; set; } = new List<Conversation>();
}

public enum FileProcessingStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}
