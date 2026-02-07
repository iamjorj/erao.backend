namespace Erao.Core.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IUserRepository Users { get; }
    IDatabaseConnectionRepository DatabaseConnections { get; }
    IConversationRepository Conversations { get; }
    IMessageRepository Messages { get; }
    IUsageLogRepository UsageLogs { get; }
    IFileDocumentRepository FileDocuments { get; }
    Task<int> SaveChangesAsync();
}
