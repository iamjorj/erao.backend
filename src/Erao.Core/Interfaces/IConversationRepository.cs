using Erao.Core.Entities;

namespace Erao.Core.Interfaces;

public interface IConversationRepository : IRepository<Conversation>
{
    Task<IEnumerable<Conversation>> GetByUserIdAsync(Guid userId);
    Task<Conversation?> GetWithMessagesAsync(Guid id);
    /// <summary>
    /// Loads conversation with DatabaseConnection and FileDocument, but NO messages.
    /// Use with GetRecentMessagesAsync for chat processing.
    /// </summary>
    Task<Conversation?> GetWithConnectionsAsync(Guid id);
}
