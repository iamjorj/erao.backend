using Erao.Core.Entities.Analytics;

namespace Erao.Core.Interfaces.Analytics;

public interface IAnalyticsRecordRepository : IRepository<AnalyticsRecord>
{
    Task AddRangeAsync(IEnumerable<AnalyticsRecord> records);
    IQueryable<AnalyticsRecord> Query(Guid userId);
}
