using Erao.Core.Enums;

namespace Erao.Core.Entities;

public class AppConnector : BaseEntity
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public ConnectorType ConnectorType { get; set; }
    public string EncryptedCredentials { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime? LastSyncedAt { get; set; }
    public string? SchemaContext { get; set; }

    // Navigation properties
    public virtual User User { get; set; } = null!;
    public virtual ICollection<Conversation> Conversations { get; set; } = new List<Conversation>();
}
