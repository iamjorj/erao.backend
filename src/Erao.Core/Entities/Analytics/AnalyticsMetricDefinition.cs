using Erao.Core.Enums;

namespace Erao.Core.Entities.Analytics;

/// <summary>
/// A reusable metric definition that specifies how to compute a metric
/// from analytics records. Allows users to define named metrics once
/// and reference them in queries without respecifying calculation logic.
/// </summary>
public class AnalyticsMetricDefinition : BaseEntity
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DatasetName { get; set; } = string.Empty;
    public MetricType MetricType { get; set; }

    /// <summary>
    /// The key in the Measures dictionary to operate on.
    /// For Count metric type this can be empty (counts records).
    /// </summary>
    public string MeasureField { get; set; } = string.Empty;

    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    // Navigation
    public User User { get; set; } = null!;
}
