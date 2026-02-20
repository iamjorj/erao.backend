namespace Erao.Core.Interfaces;

public interface IConnectorSyncService
{
    Task SyncAsync(Guid connectorId, Guid userId);
}
