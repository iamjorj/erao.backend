using Erao.Core.Interfaces;
using Erao.Infrastructure.Data;

namespace Erao.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly EraoDbContext _context;
    private IUserRepository? _users;
    private IDatabaseConnectionRepository? _databaseConnections;
    private IConversationRepository? _conversations;
    private IMessageRepository? _messages;
    private IUsageLogRepository? _usageLogs;
    private IFileDocumentRepository? _fileDocuments;

    public UnitOfWork(EraoDbContext context)
    {
        _context = context;
    }

    public IUserRepository Users => _users ??= new UserRepository(_context);
    public IDatabaseConnectionRepository DatabaseConnections => _databaseConnections ??= new DatabaseConnectionRepository(_context);
    public IConversationRepository Conversations => _conversations ??= new ConversationRepository(_context);
    public IMessageRepository Messages => _messages ??= new MessageRepository(_context);
    public IUsageLogRepository UsageLogs => _usageLogs ??= new UsageLogRepository(_context);
    public IFileDocumentRepository FileDocuments => _fileDocuments ??= new FileDocumentRepository(_context);

    public async Task<int> SaveChangesAsync()
    {
        return await _context.SaveChangesAsync();
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
