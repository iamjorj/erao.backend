using Erao.Core.DTOs.Analytics;

namespace Erao.Core.Interfaces.Analytics;

public interface IAnalyticsIngestionService
{
    Task<DatasetIngestionResponse> IngestAsync(Guid userId, DatasetIngestionRequest request);
    Task<IEnumerable<DatasetDto>> GetDatasetsAsync(Guid userId);
    Task<DatasetDto?> GetDatasetAsync(Guid userId, string datasetName);
    Task<bool> DeleteDatasetAsync(Guid userId, string datasetName);
}
