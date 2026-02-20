using Erao.Core.Enums;

namespace Erao.Core.Interfaces;

public interface IConnectorSyncService
{
    Task SyncAsync(Guid connectorId, Guid userId);
    Task<(bool Success, string Message, string? AccountName)> TestGoogleConnectionAsync(
        ConnectorType type, Dictionary<string, string> credentials);
}
