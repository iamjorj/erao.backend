namespace Erao.Core.DTOs.Chat;

public class ConversationDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public Guid? DatabaseConnectionId { get; set; }
    public string? DatabaseConnectionName { get; set; }
    public Guid? FileDocumentId { get; set; }
    public string? FileDocumentName { get; set; }
    public Guid? AppConnectorId { get; set; }
    public string? AppConnectorName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastMessageAt { get; set; }
    public string? CustomInstructions { get; set; }
    public bool HasContextSummary { get; set; }
    public string? ContextSummary { get; set; }
    public int SummarizedMessageCount { get; set; }
    public ContextMetadata? LastContextMetadata { get; set; }
    public List<MessageDto> Messages { get; set; } = new();
}
