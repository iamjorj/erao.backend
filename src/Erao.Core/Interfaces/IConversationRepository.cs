using Erao.Core.Entities;

namespace Erao.Core.Interfaces;

public interface IConversationRepository : IRepository<Conversation>
{
    Task<IEnumerable<Conversation>> GetByUserIdAsync(Guid userId);
    Task<Conversation?> GetWithMessagesAsync(Guid id);
}
