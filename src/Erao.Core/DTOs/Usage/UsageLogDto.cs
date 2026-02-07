namespace Erao.Core.DTOs.Usage;

public class UsageLogDto
{
    public Guid Id { get; set; }
    public string QueryType { get; set; } = string.Empty;
    public int QueriesCount { get; set; } = 1; // Each log entry represents 1 query
    public int ExecutionTimeMs { get; set; }
    public string? DatabaseConnectionName { get; set; }
    public DateTime CreatedAt { get; set; }
}
