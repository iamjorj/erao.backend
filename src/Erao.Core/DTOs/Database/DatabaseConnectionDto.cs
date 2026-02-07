using Erao.Core.Enums;

namespace Erao.Core.DTOs.Database;

public class DatabaseConnectionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DatabaseType DatabaseType { get; set; }
    public bool IsActive { get; set; }
    public DateTime? LastTestedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
