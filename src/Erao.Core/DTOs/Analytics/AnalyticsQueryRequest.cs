namespace Erao.Core.DTOs.Analytics;

public class AnalyticsQueryRequest
{
    public AnalyticsFilterModel Filter { get; set; } = new();

    /// <summary>
    /// Page number (1-based). Defaults to 1.
    /// </summary>
    public int Page { get; set; } = 1;

    /// <summary>
    /// Page size. Defaults to 50, max 500.
    /// </summary>
    public int PageSize { get; set; } = 50;

    /// <summary>
    /// Sort field. Supports: "occurredAt", "createdAt".
    /// </summary>
    public string? SortBy { get; set; }

    /// <summary>
    /// Sort direction: "asc" or "desc". Defaults to "desc".
    /// </summary>
    public string SortDirection { get; set; } = "desc";
}
