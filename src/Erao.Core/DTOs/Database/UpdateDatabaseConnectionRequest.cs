using Erao.Core.Enums;

namespace Erao.Core.DTOs.Database;

public class UpdateDatabaseConnectionRequest
{
    public string? Name { get; set; }
    public DatabaseType? DatabaseType { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? DatabaseName { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}
