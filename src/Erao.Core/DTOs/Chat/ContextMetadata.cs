namespace Erao.Core.DTOs.Chat;

public class ContextMetadata
{
    public int TotalMessages { get; set; }
    public int MessagesInContext { get; set; }
    public bool UsingSummary { get; set; }
    public int SummarizedMessages { get; set; }
    public int EstimatedInputTokens { get; set; }
    public int TokenBudget { get; set; }
    public bool HasCustomInstructions { get; set; }
    public string? ContextSummary { get; set; }
    public int SummarizedMessageCount { get; set; }
}
