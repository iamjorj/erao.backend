using Erao.Core.Entities.Analytics;

namespace Erao.Core.Interfaces.Analytics;

public interface IAnalyticsDatasetRepository : IRepository<AnalyticsDataset>
{
    Task<AnalyticsDataset?> GetByNameAsync(Guid userId, string datasetName);
    Task<IEnumerable<AnalyticsDataset>> GetByUserIdAsync(Guid userId);
}
