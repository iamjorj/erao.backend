namespace Erao.Core.DTOs.Analytics;

public class AnalyticsQueryResponse
{
    public List<AnalyticsRecordDto> Records { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}

public class AnalyticsRecordDto
{
    public Guid Id { get; set; }
    public string DatasetName { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
    public Dictionary<string, string> Dimensions { get; set; } = new();
    public Dictionary<string, decimal> Measures { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}
