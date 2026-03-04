namespace Erao.Core.Helpers;

public static class TokenEstimator
{
    /// <summary>
    /// Approximate tokens per message overhead (role, delimiters, etc.)
    /// </summary>
    private const int MessageOverhead = 4;

    /// <summary>
    /// Estimates token count for a string using chars / 4.0 heuristic.
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (int)Math.Ceiling(text.Length / 4.0);
    }

    /// <summary>
    /// Estimates total tokens for a chat message (content + per-message overhead).
    /// </summary>
    public static int EstimateMessageTokens(string? content)
    {
        return EstimateTokens(content) + MessageOverhead;
    }
}
