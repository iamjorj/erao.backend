using Erao.Core.Entities.Analytics;

namespace Erao.Core.Interfaces.Analytics;

public interface IAnalyticsMetricDefinitionRepository : IRepository<AnalyticsMetricDefinition>
{
    Task<IEnumerable<AnalyticsMetricDefinition>> GetByDatasetNameAsync(Guid userId, string datasetName);
    Task<AnalyticsMetricDefinition?> GetByNameAsync(Guid userId, string name, string datasetName);
}
