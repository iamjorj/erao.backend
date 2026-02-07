using Erao.Core.Enums;

namespace Erao.Core.DTOs.Chat;

public class MessageDto
{
    public Guid Id { get; set; }
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? QueryResult { get; set; }
    public int TokensUsed { get; set; }
    public DateTime CreatedAt { get; set; }
}
