namespace Erao.Core.Entities;

public class Conversation : BaseEntity
{
    public Guid UserId { get; set; }
    public Guid? DatabaseConnectionId { get; set; }
    public Guid? FileDocumentId { get; set; }
    public Guid? AppConnectorId { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>AI-generated summary of compacted older messages</summary>
    public string? ContextSummary { get; set; }

    /// <summary>How many messages the summary covers (for staleness check)</summary>
    public int SummarizedMessageCount { get; set; }

    /// <summary>User-editable per-conversation instructions (max 2000 chars)</summary>
    public string? CustomInstructions { get; set; }

    // Navigation properties
    public virtual User User { get; set; } = null!;
    public virtual DatabaseConnection? DatabaseConnection { get; set; }
    public virtual FileDocument? FileDocument { get; set; }
    public virtual AppConnector? AppConnector { get; set; }
    public virtual ICollection<Message> Messages { get; set; } = new List<Message>();
}
