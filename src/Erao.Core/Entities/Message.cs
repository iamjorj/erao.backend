using Erao.Core.Enums;

namespace Erao.Core.Entities;

public class Message : BaseEntity
{
    public Guid ConversationId { get; set; }
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? SqlQuery { get; set; }
    public string? QueryResult { get; set; }
    public int TokensUsed { get; set; }

    // Navigation properties
    public virtual Conversation Conversation { get; set; } = null!;
}
