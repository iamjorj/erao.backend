using Erao.Core.Interfaces;
using Erao.Core.Interfaces.Analytics;
using Erao.Infrastructure.Data;
using Erao.Infrastructure.Repositories.Analytics;

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
    private IAppConnectorRepository? _appConnectors;
    private IAnalyticsDatasetRepository? _analyticsDatasets;
    private IAnalyticsRecordRepository? _analyticsRecords;
    private IAnalyticsMetricDefinitionRepository? _analyticsMetricDefinitions;

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
    public IAppConnectorRepository AppConnectors => _appConnectors ??= new AppConnectorRepository(_context);

    // Analytics
    public IAnalyticsDatasetRepository AnalyticsDatasets => _analyticsDatasets ??= new AnalyticsDatasetRepository(_context);
    public IAnalyticsRecordRepository AnalyticsRecords => _analyticsRecords ??= new AnalyticsRecordRepository(_context);
    public IAnalyticsMetricDefinitionRepository AnalyticsMetricDefinitions => _analyticsMetricDefinitions ??= new AnalyticsMetricDefinitionRepository(_context);

    public async Task<int> SaveChangesAsync()
    {
        return await _context.SaveChangesAsync();
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
