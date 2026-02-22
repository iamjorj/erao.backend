namespace Erao.Core.DTOs.Chat;

public class ChatResponse
{
    public MessageDto UserMessage { get; set; } = null!;
    public MessageDto AssistantMessage { get; set; } = null!;
    public string? QueryResult { get; set; }
    public int TokensUsed { get; set; }

    /// <summary>
    /// AI-generated visualization hint for optimal chart display
    /// </summary>
    public VisualizationHint? VisualizationHint { get; set; }

    /// <summary>
    /// Clarification request when AI needs user to disambiguate intent
    /// </summary>
    public ClarificationRequest? Clarification { get; set; }

    /// <summary>
    /// Plain-English insight interpreting the query results for non-technical users
    /// </summary>
    public string? Insight { get; set; }

    /// <summary>
    /// Suggested follow-up questions the user might want to ask next
    /// </summary>
    public List<string>? FollowUpQuestions { get; set; }
}
