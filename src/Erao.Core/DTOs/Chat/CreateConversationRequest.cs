namespace Erao.Core.DTOs.Chat;

public class CreateConversationRequest
{
    public string? Title { get; set; }
    public Guid? DatabaseConnectionId { get; set; }
    public Guid? FileDocumentId { get; set; }
}
