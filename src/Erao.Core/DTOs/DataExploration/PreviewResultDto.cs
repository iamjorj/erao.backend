namespace Erao.Core.DTOs.DataExploration;

public class PreviewResultDto
{
    public List<string> Columns { get; set; } = new();
    public List<Dictionary<string, object?>> Rows { get; set; } = new();
    public int RowCount { get; set; }
    public string? TableName { get; set; }
    public long ExecutionTimeMs { get; set; }
}

public class ColumnStatsDto
{
    public string ColumnName { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public long TotalCount { get; set; }
    public long NullCount { get; set; }
    public double NullPercentage { get; set; }
    public long UniqueCount { get; set; }
    public object? MinValue { get; set; }
    public object? MaxValue { get; set; }
    public double? AvgValue { get; set; }
    public List<object?> SampleValues { get; set; } = new();
}
