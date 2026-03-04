using Erao.Core.Entities;

namespace Erao.Core.Interfaces;

public interface IMessageRepository : IRepository<Message>
{
    Task<IEnumerable<Message>> GetByConversationIdAsync(Guid conversationId);
    /// <summary>
    /// Gets the most recent N messages for a conversation, ordered oldest-first.
    /// Only loads Content, Role, QueryResult — not the full entity graph.
    /// </summary>
    Task<List<Message>> GetRecentAsync(Guid conversationId, int count);
    Task<int> GetCountAsync(Guid conversationId);
}
