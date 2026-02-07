using Microsoft.EntityFrameworkCore;
using Erao.Core.Entities;
using Erao.Core.Interfaces;
using Erao.Infrastructure.Data;

namespace Erao.Infrastructure.Repositories;

public class MessageRepository : Repository<Message>, IMessageRepository
{
    public MessageRepository(EraoDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<Message>> GetByConversationIdAsync(Guid conversationId)
    {
        return await _dbSet
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();
    }
}
