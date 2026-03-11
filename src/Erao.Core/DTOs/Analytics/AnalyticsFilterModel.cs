namespace Erao.Core.DTOs.Analytics;

public class AnalyticsFilterModel
{
    public string? DatasetName { get; set; }
    public string? Source { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }

    /// <summary>
    /// Filter by dimension values. Example: {"region":"US","channel":"web"}
    /// Only records matching ALL specified dimension filters are included.
    /// </summary>
    public Dictionary<string, string>? DimensionFilters { get; set; }

    /// <summary>
    /// Optional list of measure fields to include in results.
    /// If null/empty, all measures are returned.
    /// </summary>
    public List<string>? MeasureFields { get; set; }
}
