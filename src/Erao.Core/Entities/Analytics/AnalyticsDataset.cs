namespace Erao.Core.Entities.Analytics;

/// <summary>
/// Represents a named analytics dataset owned by a user (tenant).
/// Groups related analytics records under a common name and source.
/// </summary>
public class AnalyticsDataset : BaseEntity
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// Arbitrary metadata stored as JSON (e.g. schema hints, tags, version info).
    /// </summary>
    public string? MetadataJson { get; set; }

    public int RecordCount { get; set; }
    public DateTime? LastIngestedAt { get; set; }

    // Navigation
    public User User { get; set; } = null!;
    public ICollection<AnalyticsRecord> Records { get; set; } = new List<AnalyticsRecord>();
}
