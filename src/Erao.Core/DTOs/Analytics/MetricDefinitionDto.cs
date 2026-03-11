using Erao.Core.Enums;

namespace Erao.Core.DTOs.Analytics;

public class MetricDefinitionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DatasetName { get; set; } = string.Empty;
    public MetricType MetricType { get; set; }
    public string MeasureField { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateMetricDefinitionRequest
{
    public string Name { get; set; } = string.Empty;
    public string DatasetName { get; set; } = string.Empty;
    public MetricType MetricType { get; set; }
    public string MeasureField { get; set; } = string.Empty;
    public string? Description { get; set; }
}
