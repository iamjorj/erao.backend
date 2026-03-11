using Erao.Core.Enums;

namespace Erao.Core.DTOs.Analytics;

public class MetricsResponse
{
    public string DatasetName { get; set; } = string.Empty;
    public int RecordsEvaluated { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public List<MetricResult> Results { get; set; } = new();
}

public class MetricResult
{
    public string Name { get; set; } = string.Empty;
    public MetricType MetricType { get; set; }
    public string? MeasureField { get; set; }
    public decimal? Value { get; set; }
}
