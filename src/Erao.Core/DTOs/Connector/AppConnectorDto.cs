namespace Erao.Core.DTOs.Connector;

public class AppConnectorDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ConnectorType { get; set; }
    public bool IsActive { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Sync fields
    public int SyncStatus { get; set; }
    public string? SyncErrorMessage { get; set; }
    public bool HasSyncedData { get; set; }
    public Dictionary<string, long>? TableRowCounts { get; set; }
}
