using Erao.Core.Entities;

namespace Erao.Core.Interfaces;

public interface IConnectorQueryService
{
    Task<string> ExecuteQueryForConnectorAsync(Guid connectorId, string query);
    Task<List<string>> ExecuteQueriesForConnectorAsync(Guid connectorId, List<string> queries);
    string BuildConnectorSchemaDescription(AppConnector connector);
}
