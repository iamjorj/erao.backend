using Microsoft.EntityFrameworkCore;
using Erao.Core.Entities;
using Erao.Core.Enums;
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
            .Include(c => c.AppConnector)
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync();
    }

    public async Task<Conversation?> GetWithMessagesAsync(Guid id)
    {
        return await _dbSet
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt).ThenBy(m => m.Role))
            .Include(c => c.DatabaseConnection)
            .Include(c => c.FileDocument)
            .Include(c => c.AppConnector)
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task<Conversation?> GetWithConnectionsAsync(Guid id)
    {
        return await _dbSet
            .Include(c => c.DatabaseConnection)
            .Include(c => c.FileDocument)
            .Include(c => c.AppConnector)
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task<Conversation?> FindBySourceAsync(Guid userId, DataSourceType sourceType, Guid sourceId)
    {
        var query = _dbSet
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt).ThenBy(m => m.Role))
            .Include(c => c.DatabaseConnection)
            .Include(c => c.FileDocument)
            .Include(c => c.AppConnector)
            .Where(c => c.UserId == userId);

        query = sourceType switch
        {
            DataSourceType.Database => query.Where(c => c.DatabaseConnectionId == sourceId),
            DataSourceType.File => query.Where(c => c.FileDocumentId == sourceId),
            DataSourceType.Connector => query.Where(c => c.AppConnectorId == sourceId),
            _ => throw new ArgumentOutOfRangeException(nameof(sourceType))
        };

        return await query
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefaultAsync();
    }
}
