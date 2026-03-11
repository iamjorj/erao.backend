namespace Erao.Core.DTOs.Analytics;

public class DatasetIngestionRequest
{
    public string DatasetName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public List<IngestionRecordDto> Records { get; set; } = new();
}

public class IngestionRecordDto
{
    public DateTime OccurredAt { get; set; }
    public Dictionary<string, string> Dimensions { get; set; } = new();
    public Dictionary<string, decimal> Measures { get; set; } = new();
}
