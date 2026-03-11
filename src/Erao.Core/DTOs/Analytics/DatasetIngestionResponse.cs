namespace Erao.Core.DTOs.Analytics;

public class DatasetIngestionResponse
{
    public Guid DatasetId { get; set; }
    public string DatasetName { get; set; } = string.Empty;
    public int RecordsIngested { get; set; }
    public int TotalRecordCount { get; set; }
    public DateTime IngestedAt { get; set; }
}
