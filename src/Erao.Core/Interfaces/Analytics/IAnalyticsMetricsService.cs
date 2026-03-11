using Erao.Core.DTOs.Analytics;

namespace Erao.Core.Interfaces.Analytics;

public interface IAnalyticsMetricsService
{
    Task<MetricsResponse> CalculateAsync(Guid userId, MetricsRequest request);
    Task<MetricDefinitionDto> CreateDefinitionAsync(Guid userId, CreateMetricDefinitionRequest request);
    Task<IEnumerable<MetricDefinitionDto>> GetDefinitionsAsync(Guid userId, string datasetName);
    Task<bool> DeleteDefinitionAsync(Guid userId, Guid definitionId);
}
