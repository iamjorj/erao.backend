namespace Erao.Core.DTOs.DataExploration;

/// <summary>
/// Request to analyze data for optimal visualization
/// </summary>
public class AnalyzeVisualizationRequest
{
    /// <summary>
    /// Column names from the query result
    /// </summary>
    public List<string> Columns { get; set; } = new();

    /// <summary>
    /// Sample rows (first 50-100 rows) for analysis
    /// </summary>
    public List<Dictionary<string, object?>> SampleRows { get; set; } = new();

    /// <summary>
    /// Total number of rows in the result
    /// </summary>
    public int TotalRowCount { get; set; }

    /// <summary>
    /// The SQL query that produced this result (optional, for context)
    /// </summary>
    public string? SqlQuery { get; set; }

    /// <summary>
    /// Natural language question that led to this query (optional)
    /// </summary>
    public string? UserQuestion { get; set; }
}

/// <summary>
/// AI-powered visualization recommendation
/// </summary>
public class VisualizationRecommendationDto
{
    /// <summary>
    /// Primary recommended chart type: "bar", "line", "pie", "area", "table"
    /// </summary>
    public string RecommendedChartType { get; set; } = "bar";

    /// <summary>
    /// Why this chart type was recommended
    /// </summary>
    public string ChartTypeReason { get; set; } = string.Empty;

    /// <summary>
    /// Column to use for grouping/X-axis
    /// </summary>
    public string? GroupByColumn { get; set; }

    /// <summary>
    /// Value columns with their recommended aggregations
    /// </summary>
    public List<ValueColumnRecommendation> ValueColumns { get; set; } = new();

    /// <summary>
    /// Alternative visualization options
    /// </summary>
    public List<AlternativeVisualization> Alternatives { get; set; } = new();

    /// <summary>
    /// Data insights discovered during analysis
    /// </summary>
    public List<DataInsight> Insights { get; set; } = new();

    /// <summary>
    /// Column metadata for frontend
    /// </summary>
    public List<ColumnMetadata> ColumnMetadata { get; set; } = new();

    /// <summary>
    /// Whether data should be aggregated
    /// </summary>
    public bool ShouldAggregate { get; set; }

    /// <summary>
    /// Suggested title for the chart
    /// </summary>
    public string? SuggestedTitle { get; set; }
}

/// <summary>
/// Recommendation for a value column
/// </summary>
public class ValueColumnRecommendation
{
    /// <summary>
    /// Column name
    /// </summary>
    public string Column { get; set; } = string.Empty;

    /// <summary>
    /// Recommended aggregation: "COUNT", "SUM", "AVG", "MIN", "MAX", "NONE"
    /// </summary>
    public string Aggregation { get; set; } = "COUNT";

    /// <summary>
    /// Why this aggregation was chosen
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Display name for the column in charts
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Format hint: "number", "currency", "percentage", "decimal"
    /// </summary>
    public string FormatHint { get; set; } = "number";
}

/// <summary>
/// Alternative visualization suggestion
/// </summary>
public class AlternativeVisualization
{
    /// <summary>
    /// Chart type
    /// </summary>
    public string ChartType { get; set; } = string.Empty;

    /// <summary>
    /// Column to group by
    /// </summary>
    public string? GroupByColumn { get; set; }

    /// <summary>
    /// Description of what this visualization shows
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Value columns to display
    /// </summary>
    public List<string> ValueColumns { get; set; } = new();
}

/// <summary>
/// Insight about the data
/// </summary>
public class DataInsight
{
    /// <summary>
    /// Type: "distribution", "trend", "outlier", "correlation", "cardinality", "quality"
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable insight
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Importance: "low", "medium", "high"
    /// </summary>
    public string Importance { get; set; } = "medium";

    /// <summary>
    /// Related column(s)
    /// </summary>
    public List<string> RelatedColumns { get; set; } = new();
}

/// <summary>
/// Metadata about a column
/// </summary>
public class ColumnMetadata
{
    /// <summary>
    /// Column name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Semantic type: "id", "name", "category", "date", "datetime", "currency", "percentage", "count", "measure", "dimension", "text"
    /// </summary>
    public string SemanticType { get; set; } = "text";

    /// <summary>
    /// Data type: "string", "number", "boolean", "date", "datetime"
    /// </summary>
    public string DataType { get; set; } = "string";

    /// <summary>
    /// Number of unique values
    /// </summary>
    public int UniqueCount { get; set; }

    /// <summary>
    /// Number of null values
    /// </summary>
    public int NullCount { get; set; }

    /// <summary>
    /// Whether this column is suitable for grouping (categorical)
    /// </summary>
    public bool IsCategorical { get; set; }

    /// <summary>
    /// Whether this column is suitable for value/measure
    /// </summary>
    public bool IsNumeric { get; set; }

    /// <summary>
    /// Sample unique values (for categorical columns)
    /// </summary>
    public List<string> SampleValues { get; set; } = new();
}
