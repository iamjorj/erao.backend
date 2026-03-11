using System.Text.Json;
using Erao.Core.DTOs.Analytics;
using Erao.Core.Entities.Analytics;
using Erao.Core.Interfaces;
using Erao.Core.Interfaces.Analytics;
using Microsoft.Extensions.Logging;

namespace Erao.Application.Services.Analytics;

public class AnalyticsIngestionService : IAnalyticsIngestionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAnalyticsDatasetRepository _datasetRepo;
    private readonly IAnalyticsRecordRepository _recordRepo;
    private readonly IAnalyticsCacheService _cache;
    private readonly ILogger<AnalyticsIngestionService> _logger;

    public AnalyticsIngestionService(
        IUnitOfWork unitOfWork,
        IAnalyticsDatasetRepository datasetRepo,
        IAnalyticsRecordRepository recordRepo,
        IAnalyticsCacheService cache,
        ILogger<AnalyticsIngestionService> logger)
    {
        _unitOfWork = unitOfWork;
        _datasetRepo = datasetRepo;
        _recordRepo = recordRepo;
        _cache = cache;
        _logger = logger;
    }

    public async Task<DatasetIngestionResponse> IngestAsync(Guid userId, DatasetIngestionRequest request)
    {
        var dataset = await _datasetRepo.GetByNameAsync(userId, request.DatasetName);

        if (dataset == null)
        {
            dataset = new AnalyticsDataset
            {
                UserId = userId,
                Name = request.DatasetName,
                Source = request.Source,
                Description = request.Description,
                MetadataJson = request.Metadata != null
                    ? JsonSerializer.Serialize(request.Metadata)
                    : null,
                RecordCount = 0
            };
            await _datasetRepo.AddAsync(dataset);
            await _unitOfWork.SaveChangesAsync();
        }
        else
        {
            dataset.Source = request.Source;
            if (request.Description != null)
                dataset.Description = request.Description;
            if (request.Metadata != null)
                dataset.MetadataJson = JsonSerializer.Serialize(request.Metadata);
            await _datasetRepo.UpdateAsync(dataset);
        }

        var records = request.Records.Select(r => new AnalyticsRecord
        {
            DatasetId = dataset.Id,
            UserId = userId,
            OccurredAt = DateTime.SpecifyKind(r.OccurredAt, DateTimeKind.Utc),
            Dimensions = r.Dimensions ?? new Dictionary<string, string>(),
            Measures = r.Measures ?? new Dictionary<string, decimal>()
        }).ToList();

        await _recordRepo.AddRangeAsync(records);

        dataset.RecordCount += records.Count;
        dataset.LastIngestedAt = DateTime.UtcNow;
        await _datasetRepo.UpdateAsync(dataset);

        await _unitOfWork.SaveChangesAsync();

        // Invalidate cached analytics for this user+dataset
        _cache.Invalidate($"{userId}:{request.DatasetName}");

        _logger.LogInformation(
            "Ingested {Count} records into dataset '{Dataset}' for user {UserId}",
            records.Count, request.DatasetName, userId);

        return new DatasetIngestionResponse
        {
            DatasetId = dataset.Id,
            DatasetName = dataset.Name,
            RecordsIngested = records.Count,
            TotalRecordCount = dataset.RecordCount,
            IngestedAt = DateTime.UtcNow
        };
    }

    public async Task<IEnumerable<DatasetDto>> GetDatasetsAsync(Guid userId)
    {
        var datasets = await _datasetRepo.GetByUserIdAsync(userId);
        return datasets.Select(MapToDto);
    }

    public async Task<DatasetDto?> GetDatasetAsync(Guid userId, string datasetName)
    {
        var dataset = await _datasetRepo.GetByNameAsync(userId, datasetName);
        return dataset != null ? MapToDto(dataset) : null;
    }

    public async Task<bool> DeleteDatasetAsync(Guid userId, string datasetName)
    {
        var dataset = await _datasetRepo.GetByNameAsync(userId, datasetName);
        if (dataset == null)
            return false;

        await _datasetRepo.DeleteAsync(dataset);
        await _unitOfWork.SaveChangesAsync();

        _cache.Invalidate($"{userId}:{datasetName}");
        return true;
    }

    private static DatasetDto MapToDto(AnalyticsDataset ds)
    {
        return new DatasetDto
        {
            Id = ds.Id,
            Name = ds.Name,
            Source = ds.Source,
            Description = ds.Description,
            Metadata = !string.IsNullOrEmpty(ds.MetadataJson)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(ds.MetadataJson)
                : null,
            RecordCount = ds.RecordCount,
            LastIngestedAt = ds.LastIngestedAt,
            CreatedAt = ds.CreatedAt
        };
    }
}
