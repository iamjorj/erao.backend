using Microsoft.EntityFrameworkCore;
using Erao.Core.Entities;
using Erao.Core.Interfaces;
using Erao.Infrastructure.Data;

namespace Erao.Infrastructure.Repositories;

public class UsageLogRepository : Repository<UsageLog>, IUsageLogRepository
{
    public UsageLogRepository(EraoDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<UsageLog>> GetByUserIdAsync(Guid userId, DateTime? from = null, DateTime? to = null)
    {
        var query = _dbSet
            .Include(u => u.DatabaseConnection)
            .Where(u => u.UserId == userId);

        if (from.HasValue)
        {
            query = query.Where(u => u.CreatedAt >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(u => u.CreatedAt <= to.Value);
        }

        return await query
            .OrderByDescending(u => u.CreatedAt)
            .ToListAsync();
    }

    public async Task<int> GetTotalTokensUsedAsync(Guid userId, DateTime from, DateTime to)
    {
        return await _dbSet
            .Where(u => u.UserId == userId && u.CreatedAt >= from && u.CreatedAt <= to)
            .SumAsync(u => u.TokensUsed);
    }
}
