using Erao.Core.Entities;

namespace Erao.Core.Interfaces;

public interface IUsageLogRepository : IRepository<UsageLog>
{
    Task<IEnumerable<UsageLog>> GetByUserIdAsync(Guid userId, DateTime? from = null, DateTime? to = null);
    Task<int> GetTotalTokensUsedAsync(Guid userId, DateTime from, DateTime to);
}
