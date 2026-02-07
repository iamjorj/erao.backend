namespace Erao.Core.Interfaces;

public interface IOllamaService
{
    Task<string> GenerateSqlFromNaturalLanguageAsync(string naturalLanguage, string schemaContext);
    Task<string> GenerateResponseAsync(string prompt, string? systemPrompt = null);
    Task<(string response, int tokensUsed)> ChatAsync(string userMessage, IEnumerable<(string role, string content)> history, string? schemaContext = null);
}
