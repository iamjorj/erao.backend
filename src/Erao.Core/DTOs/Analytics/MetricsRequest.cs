using Erao.Core.Enums;

namespace Erao.Core.DTOs.Analytics;

public class MetricsRequest
{
    public string DatasetName { get; set; } = string.Empty;

    /// <summary>
    /// Metrics to compute. If empty, all active metric definitions
    /// for the dataset are used.
    /// </summary>
    public List<MetricCalculation> Metrics { get; set; } = new();

    public AnalyticsFilterModel? Filter { get; set; }
}

public class MetricCalculation
{
    /// <summary>
    /// A label for this metric result (e.g. "total_revenue").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    public MetricType MetricType { get; set; }

    /// <summary>
    /// The measure field key to operate on. Not required for Count.
    /// </summary>
    public string? MeasureField { get; set; }
}
