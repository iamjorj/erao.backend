using System.Net.Http.Json;
using System.Text.Json;
using Erao.Core.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Erao.Infrastructure.Services;

public class OllamaService : IOllamaService
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly string _provider; // "ollama" or "openai"
    private readonly string? _apiKey;

    public OllamaService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _baseUrl = configuration["Ollama:BaseUrl"] ?? "http://localhost:11434";
        _model = configuration["Ollama:Model"] ?? "gpt-oss:120b-cloud";
        _apiKey = configuration["Ollama:ApiKey"];

        // Auto-detect provider from URL, or use explicit config
        _provider = configuration["Ollama:Provider"]
            ?? (_baseUrl.Contains("openai.com") || _baseUrl.Contains("openrouter.ai")
                || _baseUrl.Contains("together.xyz") || !string.IsNullOrEmpty(_apiKey)
                ? "openai" : "ollama");

        _httpClient.BaseAddress = new Uri(_baseUrl);
        _httpClient.Timeout = TimeSpan.FromMinutes(5);

        // Set auth header for OpenAI-compatible APIs
        if (!string.IsNullOrEmpty(_apiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        }
    }

    public async Task<string> GenerateSqlFromNaturalLanguageAsync(string naturalLanguage, string schemaContext)
    {
        var systemPrompt = $@"You are an expert SQL query generator. Given a database schema and a natural language question, generate a valid SQL query.

Database Schema:
{schemaContext}

Rules:
1. Only generate SELECT queries unless explicitly asked for modifications
2. Use proper SQL syntax for the given database type
3. Include appropriate JOINs when needed
4. Use parameterized queries where applicable
5. Return ONLY the SQL query, no explanations

If the question cannot be answered with the given schema, respond with: ERROR: [explanation]";

        var (response, _) = await ChatAsync(naturalLanguage, Array.Empty<(string, string)>(), systemPrompt);
        return response;
    }

    public async Task<string> GenerateResponseAsync(string prompt, string? systemPrompt = null)
    {
        var (response, _) = await ChatAsync(prompt, Array.Empty<(string, string)>(), systemPrompt);
        return response;
    }

    public async Task<(string response, int tokensUsed)> ChatAsync(
        string userMessage,
        IEnumerable<(string role, string content)> history,
        string? schemaContext = null)
    {
        try
        {
            if (_provider == "openai")
                return await ChatOpenAIAsync(userMessage, history, schemaContext);
            else
                return await ChatOllamaAsync(userMessage, history, schemaContext);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to get response from AI service", ex);
        }
    }

    private async Task<(string response, int tokensUsed)> ChatOllamaAsync(
        string userMessage,
        IEnumerable<(string role, string content)> history,
        string? schemaContext)
    {
        var messages = BuildMessages(userMessage, history, schemaContext);

        var request = new
        {
            model = _model,
            messages,
            stream = false,
            options = new
            {
                temperature = 0.1,
                num_predict = 16384,
                num_ctx = 32768
            }
        };

        var response = await _httpClient.PostAsJsonAsync("/api/chat", request);
        response.EnsureSuccessStatusCode();

        var jsonResponse = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(jsonResponse);
        var root = doc.RootElement;

        var assistantMessage = root.GetProperty("message").GetProperty("content").GetString() ?? "";

        var tokensUsed = 0;
        if (root.TryGetProperty("eval_count", out var evalCount))
        {
            tokensUsed = evalCount.GetInt32();
        }
        else if (root.TryGetProperty("prompt_eval_count", out var promptEvalCount))
        {
            tokensUsed = promptEvalCount.GetInt32();
            if (root.TryGetProperty("eval_count", out var responseEvalCount))
            {
                tokensUsed += responseEvalCount.GetInt32();
            }
        }

        return (assistantMessage, tokensUsed);
    }

    private async Task<(string response, int tokensUsed)> ChatOpenAIAsync(
        string userMessage,
        IEnumerable<(string role, string content)> history,
        string? schemaContext)
    {
        var messages = BuildMessages(userMessage, history, schemaContext);

        var request = new
        {
            model = _model,
            messages,
            temperature = 0.1,
            max_tokens = 16384
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/chat/completions", request);
        response.EnsureSuccessStatusCode();

        var jsonResponse = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(jsonResponse);
        var root = doc.RootElement;

        var assistantMessage = root
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";

        var tokensUsed = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("total_tokens", out var totalTokens))
            {
                tokensUsed = totalTokens.GetInt32();
            }
        }

        return (assistantMessage, tokensUsed);
    }

    private static List<object> BuildMessages(
        string userMessage,
        IEnumerable<(string role, string content)> history,
        string? schemaContext)
    {
        var messages = new List<object>();

        if (!string.IsNullOrEmpty(schemaContext))
        {
            messages.Add(new { role = "system", content = schemaContext });
        }

        foreach (var (role, content) in history)
        {
            messages.Add(new { role, content });
        }

        messages.Add(new { role = "user", content = userMessage });

        return messages;
    }
}
