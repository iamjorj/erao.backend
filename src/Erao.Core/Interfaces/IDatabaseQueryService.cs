using Erao.Core.DTOs.Database;
using Erao.Core.Entities;
using Erao.Core.Enums;

namespace Erao.Core.Interfaces;

public interface IDatabaseQueryService
{
    Task<bool> TestConnectionAsync(DatabaseType dbType, string host, int port, string database, string username, string password);
    Task<string> GetSchemaAsync(DatabaseType dbType, string host, int port, string database, string username, string password);
    Task<List<TableSchema>> GetStructuredSchemaAsync(DatabaseType dbType, string host, int port, string database, string username, string password);
    Task<string> ExecuteQueryAsync(DatabaseType dbType, string host, int port, string database, string username, string password, string query);
}
