using Microsoft.EntityFrameworkCore;
using Erao.Core.Entities;
using Erao.Core.Interfaces;
using Erao.Infrastructure.Data;

namespace Erao.Infrastructure.Repositories;

public class AppConnectorRepository : Repository<AppConnector>, IAppConnectorRepository
{
    public AppConnectorRepository(EraoDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<AppConnector>> GetByUserIdAsync(Guid userId)
    {
        return await _dbSet
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();
    }
}
