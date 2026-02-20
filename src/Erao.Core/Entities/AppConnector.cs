using Erao.Core.Enums;

namespace Erao.Core.Entities;

public class AppConnector : BaseEntity
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public ConnectorType ConnectorType { get; set; }
    public string EncryptedCredentials { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime? LastSyncedAt { get; set; }
    public string? SchemaContext { get; set; }

    // Sync fields — populated after data sync
    public ConnectorSyncStatus SyncStatus { get; set; } = ConnectorSyncStatus.Idle;
    public string? SyncErrorMessage { get; set; }
    public string? ParquetStoragePaths { get; set; }  // JSON: {"orders":"connector-parquet/uid/cid/orders.parquet",...}
    public string? SchemaInfo { get; set; }            // JSON: {"orders":"[{Name,DataType,IsNullable},...]",...}
    public string? SampleDataJson { get; set; }        // JSON: {"orders":"[{row1},{row2},...]",...}
    public string? TableRowCounts { get; set; }        // JSON: {"orders":15234,"customers":892,...}

    // Navigation properties
    public virtual User User { get; set; } = null!;
    public virtual ICollection<Conversation> Conversations { get; set; } = new List<Conversation>();
}
