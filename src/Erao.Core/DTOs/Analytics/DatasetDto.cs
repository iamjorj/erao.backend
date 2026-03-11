namespace Erao.Core.DTOs.Analytics;

public class DatasetDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public int RecordCount { get; set; }
    public DateTime? LastIngestedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
