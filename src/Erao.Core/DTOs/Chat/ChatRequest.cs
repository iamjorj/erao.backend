namespace Erao.Core.DTOs.Chat;

public class ChatRequest
{
    public Guid ConversationId { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool ExecuteQuery { get; set; } = true;
}
