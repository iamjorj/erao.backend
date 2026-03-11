namespace Erao.Core.Entities.Analytics;

/// <summary>
/// A single analytics data point. Uses JSONB columns for flexible
/// dimensions (string key-value pairs) and measures (numeric key-value pairs).
/// This avoids a rigid column schema and allows different dataset types
/// to store heterogeneous dimensions/measures without schema migrations.
/// </summary>
public class AnalyticsRecord : BaseEntity
{
    public Guid DatasetId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>
    /// The business timestamp of this data point (when the event occurred).
    /// </summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>
    /// Categorical/textual dimensions stored as JSONB.
    /// Example: {"region":"US","channel":"web","product":"premium"}
    /// </summary>
    public Dictionary<string, string> Dimensions { get; set; } = new();

    /// <summary>
    /// Numeric measures stored as JSONB.
    /// Example: {"revenue":149.99,"quantity":3,"discount":10.0}
    /// </summary>
    public Dictionary<string, decimal> Measures { get; set; } = new();

    // Navigation
    public AnalyticsDataset Dataset { get; set; } = null!;
}
