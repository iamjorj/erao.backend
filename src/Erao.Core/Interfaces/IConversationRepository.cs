using Erao.Core.Entities;
using Erao.Core.Enums;

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
    /// <summary>
    /// Finds the most recent conversation for a specific data source (userId + sourceType + sourceId).
    /// Returns null if no conversation exists for this source.
    /// </summary>
    Task<Conversation?> FindBySourceAsync(Guid userId, DataSourceType sourceType, Guid sourceId);
}
