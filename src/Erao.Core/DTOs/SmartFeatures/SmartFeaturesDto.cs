namespace Erao.Core.DTOs.SmartFeatures;

public class SuggestedQueryDto
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty; // e.g., "Aggregation", "Trend", "Top N", "Filter"
}

public class InsightDto
{
    public string Type { get; set; } = string.Empty; // e.g., "trend", "outlier", "correlation", "pattern"
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Query { get; set; }
    public string Severity { get; set; } = "info"; // info, warning, important
    public Dictionary<string, object?> Metadata { get; set; } = new();
}

public class SuggestionsRequest
{
    public string? TableName { get; set; }
}

public class InsightsRequest
{
    public string TableName { get; set; } = string.Empty;
}
