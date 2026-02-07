using Microsoft.EntityFrameworkCore;
using Erao.Core.Entities;
using Erao.Core.Interfaces;
using Erao.Infrastructure.Data;

namespace Erao.Infrastructure.Repositories;

public class DatabaseConnectionRepository : Repository<DatabaseConnection>, IDatabaseConnectionRepository
{
    public DatabaseConnectionRepository(EraoDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<DatabaseConnection>> GetByUserIdAsync(Guid userId)
    {
        return await _dbSet
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync();
    }

    public async Task<DatabaseConnection?> GetByIdWithUserAsync(Guid id)
    {
        return await _dbSet
            .Include(d => d.User)
            .FirstOrDefaultAsync(d => d.Id == id);
    }
}
