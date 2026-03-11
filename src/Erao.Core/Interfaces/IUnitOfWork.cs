using Erao.Core.Interfaces.Analytics;

namespace Erao.Core.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IUserRepository Users { get; }
    IDatabaseConnectionRepository DatabaseConnections { get; }
    IConversationRepository Conversations { get; }
    IMessageRepository Messages { get; }
    IUsageLogRepository UsageLogs { get; }
    IFileDocumentRepository FileDocuments { get; }
    IAppConnectorRepository AppConnectors { get; }

    // Analytics
    IAnalyticsDatasetRepository AnalyticsDatasets { get; }
    IAnalyticsRecordRepository AnalyticsRecords { get; }
    IAnalyticsMetricDefinitionRepository AnalyticsMetricDefinitions { get; }

    Task<int> SaveChangesAsync();
}
