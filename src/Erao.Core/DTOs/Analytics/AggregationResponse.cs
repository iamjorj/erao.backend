namespace Erao.Core.DTOs.Analytics;

public class AggregationResponse
{
    public string DatasetName { get; set; } = string.Empty;
    public int RecordsEvaluated { get; set; }
    public List<AggregationBucket> Buckets { get; set; } = new();
}

public class AggregationBucket
{
    /// <summary>
    /// The time period key (e.g. "2025-01", "2025-W03").
    /// Null when aggregation is only by dimensions.
    /// </summary>
    public string? PeriodKey { get; set; }

    /// <summary>
    /// Dimension values for this group.
    /// </summary>
    public Dictionary<string, string> DimensionValues { get; set; } = new();

    /// <summary>
    /// Computed metric values keyed by metric name.
    /// </summary>
    public Dictionary<string, decimal?> MetricValues { get; set; } = new();

    public int RecordCount { get; set; }
}
