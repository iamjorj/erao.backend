namespace Erao.Core.DTOs.Chat;

public class UpdateConversationRequest
{
    public string? Title { get; set; }
    public string? CustomInstructions { get; set; }
    public string? ContextSummary { get; set; }
}
