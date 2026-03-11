using Microsoft.EntityFrameworkCore;
using Erao.Core.Entities.Analytics;
using Erao.Core.Interfaces.Analytics;
using Erao.Infrastructure.Data;

namespace Erao.Infrastructure.Repositories.Analytics;

public class AnalyticsMetricDefinitionRepository : Repository<AnalyticsMetricDefinition>, IAnalyticsMetricDefinitionRepository
{
    public AnalyticsMetricDefinitionRepository(EraoDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<AnalyticsMetricDefinition>> GetByDatasetNameAsync(Guid userId, string datasetName)
    {
        return await _dbSet
            .Where(m => m.UserId == userId && m.DatasetName == datasetName)
            .OrderBy(m => m.Name)
            .ToListAsync();
    }

    public async Task<AnalyticsMetricDefinition?> GetByNameAsync(Guid userId, string name, string datasetName)
    {
        return await _dbSet
            .FirstOrDefaultAsync(m => m.UserId == userId && m.Name == name && m.DatasetName == datasetName);
    }
}
