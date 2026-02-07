namespace Erao.Core.DTOs.Chat;

public class ChatResponse
{
    public MessageDto UserMessage { get; set; } = null!;
    public MessageDto AssistantMessage { get; set; } = null!;
    public string? QueryResult { get; set; }
    public int TokensUsed { get; set; }
}
