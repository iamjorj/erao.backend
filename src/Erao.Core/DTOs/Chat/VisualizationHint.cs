namespace Erao.Core.DTOs.Chat;

/// <summary>
/// Lightweight visualization hint returned with SQL in a single AI call
/// </summary>
public class VisualizationHint
{
    /// <summary>
    /// Recommended chart type: "bar", "line", "pie", "area", "table"
    /// </summary>
    public string ChartType { get; set; } = "bar";

    /// <summary>
    /// Column to use for X-axis/grouping (usually first categorical column)
    /// </summary>
    public string? GroupByColumn { get; set; }

    /// <summary>
    /// Value columns with aggregation hints
    /// </summary>
    public List<ValueColumnHint> ValueColumns { get; set; } = new();
}

/// <summary>
/// Hint for how to aggregate/display a value column
/// </summary>
public class ValueColumnHint
{
    /// <summary>
    /// Column name
    /// </summary>
    public string Column { get; set; } = string.Empty;

    /// <summary>
    /// Aggregation to apply: "SUM", "AVG", "COUNT", "MIN", "MAX", "NONE"
    /// </summary>
    public string Aggregation { get; set; } = "NONE";
}
