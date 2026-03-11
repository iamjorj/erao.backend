using Microsoft.EntityFrameworkCore;
using Erao.Core.Entities.Analytics;
using Erao.Core.Interfaces.Analytics;
using Erao.Infrastructure.Data;

namespace Erao.Infrastructure.Repositories.Analytics;

public class AnalyticsDatasetRepository : Repository<AnalyticsDataset>, IAnalyticsDatasetRepository
{
    public AnalyticsDatasetRepository(EraoDbContext context) : base(context)
    {
    }

    public async Task<AnalyticsDataset?> GetByNameAsync(Guid userId, string datasetName)
    {
        return await _dbSet
            .FirstOrDefaultAsync(d => d.UserId == userId && d.Name == datasetName);
    }

    public async Task<IEnumerable<AnalyticsDataset>> GetByUserIdAsync(Guid userId)
    {
        return await _dbSet
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.UpdatedAt)
            .ToListAsync();
    }
}
