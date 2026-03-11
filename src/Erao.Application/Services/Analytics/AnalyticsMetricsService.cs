using Erao.Core.DTOs.Analytics;
using Erao.Core.Entities.Analytics;
using Erao.Core.Enums;
using Erao.Core.Interfaces;
using Erao.Core.Interfaces.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Erao.Application.Services.Analytics;

public class AnalyticsMetricsService : IAnalyticsMetricsService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAnalyticsRecordRepository _recordRepo;
    private readonly IAnalyticsMetricDefinitionRepository _metricDefRepo;
    private readonly IAnalyticsCacheService _cache;
    private readonly ILogger<AnalyticsMetricsService> _logger;

    public AnalyticsMetricsService(
        IUnitOfWork unitOfWork,
        IAnalyticsRecordRepository recordRepo,
        IAnalyticsMetricDefinitionRepository metricDefRepo,
        IAnalyticsCacheService cache,
        ILogger<AnalyticsMetricsService> logger)
    {
        _unitOfWork = unitOfWork;
        _recordRepo = recordRepo;
        _metricDefRepo = metricDefRepo;
        _cache = cache;
        _logger = logger;
    }

    public async Task<MetricsResponse> CalculateAsync(Guid userId, MetricsRequest request)
    {
        var cacheKey = _cache.BuildKey(userId, "metrics", request);
        var cached = await _cache.GetAsync<MetricsResponse>(cacheKey);
        if (cached != null)
            return cached;

        var query = _recordRepo.Query(userId);

        // Apply dataset name filter at minimum
        var filter = request.Filter ?? new AnalyticsFilterModel();
        filter.DatasetName = request.DatasetName;
        query = AnalyticsQueryService.ApplyFilters(query, filter);

        var records = await query.ToListAsync();

        // Apply dimension filters in-memory
        records = AnalyticsQueryService.ApplyDimensionFilters(
            records,
            filter.DimensionFilters,
            r => r.Dimensions);

        // Determine which metrics to calculate
        var calculations = request.Metrics;
        if (calculations.Count == 0)
        {
            // Fall back to saved metric definitions
            var defs = await _metricDefRepo.GetByDatasetNameAsync(userId, request.DatasetName);
            calculations = defs.Where(d => d.IsActive).Select(d => new MetricCalculation
            {
                Name = d.Name,
                MetricType = d.MetricType,
                MeasureField = d.MeasureField
            }).ToList();
        }

        var results = calculations.Select(calc => ComputeMetric(calc, records)).ToList();

        var response = new MetricsResponse
        {
            DatasetName = request.DatasetName,
            RecordsEvaluated = records.Count,
            DateFrom = filter.DateFrom,
            DateTo = filter.DateTo,
            Results = results
        };

        await _cache.SetAsync(cacheKey, response);
        return response;
    }

    public async Task<MetricDefinitionDto> CreateDefinitionAsync(Guid userId, CreateMetricDefinitionRequest request)
    {
        var entity = new AnalyticsMetricDefinition
        {
            UserId = userId,
            Name = request.Name,
            DatasetName = request.DatasetName,
            MetricType = request.MetricType,
            MeasureField = request.MeasureField,
            Description = request.Description,
            IsActive = true
        };

        await _metricDefRepo.AddAsync(entity);
        await _unitOfWork.SaveChangesAsync();

        return MapToDto(entity);
    }

    public async Task<IEnumerable<MetricDefinitionDto>> GetDefinitionsAsync(Guid userId, string datasetName)
    {
        var defs = await _metricDefRepo.GetByDatasetNameAsync(userId, datasetName);
        return defs.Select(MapToDto);
    }

    public async Task<bool> DeleteDefinitionAsync(Guid userId, Guid definitionId)
    {
        var def = await _metricDefRepo.GetByIdAsync(definitionId);
        if (def == null || def.UserId != userId)
            return false;

        await _metricDefRepo.DeleteAsync(def);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    private static MetricResult ComputeMetric(MetricCalculation calc, List<AnalyticsRecord> records)
    {
        var values = ExtractValues(records, calc.MeasureField);

        decimal? result = calc.MetricType switch
        {
            MetricType.Count => records.Count,
            MetricType.Sum => values.Count > 0 ? values.Sum() : 0m,
            MetricType.Average => values.Count > 0 ? values.Average() : null,
            MetricType.Min => values.Count > 0 ? values.Min() : null,
            MetricType.Max => values.Count > 0 ? values.Max() : null,
            MetricType.Rate => ComputeRate(records, calc.MeasureField),
            MetricType.Ratio => ComputeRatio(values),
            MetricType.Growth => ComputeGrowth(records, calc.MeasureField),
            _ => null
        };

        return new MetricResult
        {
            Name = calc.Name,
            MetricType = calc.MetricType,
            MeasureField = calc.MeasureField,
            Value = result
        };
    }

    private static List<decimal> ExtractValues(List<AnalyticsRecord> records, string? measureField)
    {
        if (string.IsNullOrWhiteSpace(measureField))
            return new List<decimal>();

        return records
            .Where(r => r.Measures.ContainsKey(measureField))
            .Select(r => r.Measures[measureField])
            .ToList();
    }

    /// <summary>
    /// Rate: total measure value divided by the number of distinct days in the dataset.
    /// </summary>
    private static decimal? ComputeRate(List<AnalyticsRecord> records, string? measureField)
    {
        if (string.IsNullOrWhiteSpace(measureField) || records.Count == 0)
            return null;

        var values = ExtractValues(records, measureField);
        if (values.Count == 0) return null;

        var distinctDays = records.Select(r => r.OccurredAt.Date).Distinct().Count();
        return distinctDays > 0 ? values.Sum() / distinctDays : null;
    }

    /// <summary>
    /// Ratio: sum of the measure divided by the record count (essentially a weighted average).
    /// </summary>
    private static decimal? ComputeRatio(List<decimal> values)
    {
        return values.Count > 0 ? values.Sum() / values.Count : null;
    }

    /// <summary>
    /// Growth: (latest period value - earliest period value) / earliest period value * 100.
    /// Compares the sum of the first half of records to the second half by time order.
    /// </summary>
    private static decimal? ComputeGrowth(List<AnalyticsRecord> records, string? measureField)
    {
        if (string.IsNullOrWhiteSpace(measureField) || records.Count < 2)
            return null;

        var ordered = records.OrderBy(r => r.OccurredAt).ToList();
        var mid = ordered.Count / 2;

        var firstHalf = ordered.Take(mid)
            .Where(r => r.Measures.ContainsKey(measureField))
            .Sum(r => r.Measures[measureField]);

        var secondHalf = ordered.Skip(mid)
            .Where(r => r.Measures.ContainsKey(measureField))
            .Sum(r => r.Measures[measureField]);

        if (firstHalf == 0) return null;
        return Math.Round((secondHalf - firstHalf) / firstHalf * 100, 2);
    }

    private static MetricDefinitionDto MapToDto(AnalyticsMetricDefinition d)
    {
        return new MetricDefinitionDto
        {
            Id = d.Id,
            Name = d.Name,
            DatasetName = d.DatasetName,
            MetricType = d.MetricType,
            MeasureField = d.MeasureField,
            Description = d.Description,
            IsActive = d.IsActive,
            CreatedAt = d.CreatedAt
        };
    }
}
