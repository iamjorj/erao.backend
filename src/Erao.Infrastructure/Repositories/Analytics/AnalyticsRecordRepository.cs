using Erao.Core.Entities.Analytics;
using Erao.Core.Interfaces.Analytics;
using Erao.Infrastructure.Data;

namespace Erao.Infrastructure.Repositories.Analytics;

public class AnalyticsRecordRepository : Repository<AnalyticsRecord>, IAnalyticsRecordRepository
{
    public AnalyticsRecordRepository(EraoDbContext context) : base(context)
    {
    }

    public async Task AddRangeAsync(IEnumerable<AnalyticsRecord> records)
    {
        await _dbSet.AddRangeAsync(records);
    }

    public IQueryable<AnalyticsRecord> Query(Guid userId)
    {
        return _dbSet.Where(r => r.UserId == userId);
    }
}
