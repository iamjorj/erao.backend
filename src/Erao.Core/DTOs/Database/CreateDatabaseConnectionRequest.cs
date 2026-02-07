using Erao.Core.Enums;

namespace Erao.Core.DTOs.Database;

public class CreateDatabaseConnectionRequest
{
    public string Name { get; set; } = string.Empty;
    public DatabaseType DatabaseType { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string DatabaseName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
