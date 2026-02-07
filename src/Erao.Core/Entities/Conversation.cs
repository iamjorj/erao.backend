namespace Erao.Core.Entities;

public class Conversation : BaseEntity
{
    public Guid UserId { get; set; }
    public Guid? DatabaseConnectionId { get; set; }
    public Guid? FileDocumentId { get; set; }
    public string Title { get; set; } = string.Empty;

    // Navigation properties
    public virtual User User { get; set; } = null!;
    public virtual DatabaseConnection? DatabaseConnection { get; set; }
    public virtual FileDocument? FileDocument { get; set; }
    public virtual ICollection<Message> Messages { get; set; } = new List<Message>();
}
