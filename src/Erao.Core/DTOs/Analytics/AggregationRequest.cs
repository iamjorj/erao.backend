using Erao.Core.Enums;

namespace Erao.Core.DTOs.Analytics;

public class AggregationRequest
{
    public string DatasetName { get; set; } = string.Empty;

    /// <summary>
    /// Time period to group by. Null if only grouping by dimensions.
    /// </summary>
    public AggregationPeriod? Period { get; set; }

    /// <summary>
    /// Dimension keys to group by (e.g. ["region", "channel"]).
    /// </summary>
    public List<string>? GroupByDimensions { get; set; }

    /// <summary>
    /// Aggregations to compute on each group.
    /// </summary>
    public List<AggregationMetric> Metrics { get; set; } = new();

    public AnalyticsFilterModel? Filter { get; set; }

    /// <summary>
    /// Sort direction for the period key: "asc" or "desc". Defaults to "asc".
    /// </summary>
    public string SortDirection { get; set; } = "asc";
}

public class AggregationMetric
{
    public string Name { get; set; } = string.Empty;
    public MetricType MetricType { get; set; }
    public string? MeasureField { get; set; }
}
