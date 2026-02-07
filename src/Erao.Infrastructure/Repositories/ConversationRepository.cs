using Microsoft.EntityFrameworkCore;
using Erao.Core.Entities;
using Erao.Core.Interfaces;
using Erao.Infrastructure.Data;

namespace Erao.Infrastructure.Repositories;

public class ConversationRepository : Repository<Conversation>, IConversationRepository
{
    public ConversationRepository(EraoDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<Conversation>> GetByUserIdAsync(Guid userId)
    {
        return await _dbSet
            .Include(c => c.DatabaseConnection)
            .Include(c => c.FileDocument)
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync();
    }

    public async Task<Conversation?> GetWithMessagesAsync(Guid id)
    {
        return await _dbSet
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
            .Include(c => c.DatabaseConnection)
            .Include(c => c.FileDocument)
            .FirstOrDefaultAsync(c => c.Id == id);
    }
}
