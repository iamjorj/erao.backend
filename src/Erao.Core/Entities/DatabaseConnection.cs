using Erao.Core.Enums;

namespace Erao.Core.Entities;

public class DatabaseConnection : BaseEntity
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DatabaseType DatabaseType { get; set; }
    public string EncryptedHost { get; set; } = string.Empty;
    public string EncryptedPort { get; set; } = string.Empty;
    public string EncryptedDatabaseName { get; set; } = string.Empty;
    public string EncryptedUsername { get; set; } = string.Empty;
    public string EncryptedPassword { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime? LastTestedAt { get; set; }
    public string? SchemaCache { get; set; }

    // Navigation properties
    public virtual User User { get; set; } = null!;
    public virtual ICollection<Conversation> Conversations { get; set; } = new List<Conversation>();
    public virtual ICollection<UsageLog> UsageLogs { get; set; } = new List<UsageLog>();
}
