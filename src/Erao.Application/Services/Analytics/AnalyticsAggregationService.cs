using Erao.Core.DTOs.Analytics;
using Erao.Core.Entities.Analytics;
using Erao.Core.Enums;
using Erao.Core.Interfaces.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Erao.Application.Services.Analytics;

public class AnalyticsAggregationService : IAnalyticsAggregationService
{
    private readonly IAnalyticsRecordRepository _recordRepo;
    private readonly IAnalyticsCacheService _cache;
    private readonly ILogger<AnalyticsAggregationService> _logger;

    public AnalyticsAggregationService(
        IAnalyticsRecordRepository recordRepo,
        IAnalyticsCacheService cache,
        ILogger<AnalyticsAggregationService> logger)
    {
        _recordRepo = recordRepo;
        _cache = cache;
        _logger = logger;
    }

    public async Task<AggregationResponse> AggregateAsync(Guid userId, AggregationRequest request)
    {
        var cacheKey = _cache.BuildKey(userId, "aggregate", request);
        var cached = await _cache.GetAsync<AggregationResponse>(cacheKey);
        if (cached != null)
            return cached;

        var query = _recordRepo.Query(userId);

        var filter = request.Filter ?? new AnalyticsFilterModel();
        filter.DatasetName = request.DatasetName;
        query = AnalyticsQueryService.ApplyFilters(query, filter);

        var records = await query.ToListAsync();

        records = AnalyticsQueryService.ApplyDimensionFilters(
            records,
            filter.DimensionFilters,
            r => r.Dimensions);

        var groups = GroupRecords(records, request.Period, request.GroupByDimensions);

        var buckets = new List<AggregationBucket>();
        foreach (var group in groups)
        {
            var bucket = new AggregationBucket
            {
                PeriodKey = group.PeriodKey,
                DimensionValues = group.DimensionValues,
                RecordCount = group.Records.Count,
                MetricValues = new Dictionary<string, decimal?>()
            };

            foreach (var metric in request.Metrics)
            {
                bucket.MetricValues[metric.Name] = ComputeMetric(metric, group.Records);
            }

            buckets.Add(bucket);
        }

        // Sort buckets
        buckets = request.SortDirection.ToLowerInvariant() == "desc"
            ? buckets.OrderByDescending(b => b.PeriodKey ?? "").ToList()
            : buckets.OrderBy(b => b.PeriodKey ?? "").ToList();

        var response = new AggregationResponse
        {
            DatasetName = request.DatasetName,
            RecordsEvaluated = records.Count,
            Buckets = buckets
        };

        await _cache.SetAsync(cacheKey, response);
        return response;
    }

    private static List<RecordGroup> GroupRecords(
        List<AnalyticsRecord> records,
        AggregationPeriod? period,
        List<string>? groupByDimensions)
    {
        var grouped = records.GroupBy(r =>
        {
            var periodKey = period.HasValue ? GetPeriodKey(r.OccurredAt, period.Value) : null;
            var dimKey = GetDimensionGroupKey(r.Dimensions, groupByDimensions);
            return $"{periodKey}||{dimKey}";
        });

        return grouped.Select(g =>
        {
            var first = g.First();
            var periodKey = period.HasValue ? GetPeriodKey(first.OccurredAt, period.Value) : null;
            var dimValues = ExtractDimensionValues(first.Dimensions, groupByDimensions);

            return new RecordGroup
            {
                PeriodKey = periodKey,
                DimensionValues = dimValues,
                Records = g.ToList()
            };
        }).ToList();
    }

    private static string GetPeriodKey(DateTime date, AggregationPeriod period)
    {
        return period switch
        {
            AggregationPeriod.Day => date.ToString("yyyy-MM-dd"),
            AggregationPeriod.Week => $"{date:yyyy}-W{GetIsoWeek(date):D2}",
            AggregationPeriod.Month => date.ToString("yyyy-MM"),
            AggregationPeriod.Quarter => $"{date.Year}-Q{(date.Month - 1) / 3 + 1}",
            AggregationPeriod.Year => date.Year.ToString(),
            _ => date.ToString("yyyy-MM-dd")
        };
    }

    private static int GetIsoWeek(DateTime date)
    {
        return System.Globalization.ISOWeek.GetWeekOfYear(date);
    }

    private static string GetDimensionGroupKey(
        Dictionary<string, string> dimensions,
        List<string>? groupByDimensions)
    {
        if (groupByDimensions == null || groupByDimensions.Count == 0)
            return string.Empty;

        return string.Join("|", groupByDimensions.Select(d =>
            dimensions.TryGetValue(d, out var val) ? val : ""));
    }

    private static Dictionary<string, string> ExtractDimensionValues(
        Dictionary<string, string> dimensions,
        List<string>? groupByDimensions)
    {
        if (groupByDimensions == null || groupByDimensions.Count == 0)
            return new Dictionary<string, string>();

        var result = new Dictionary<string, string>();
        foreach (var dim in groupByDimensions)
        {
            result[dim] = dimensions.TryGetValue(dim, out var val) ? val : "";
        }
        return result;
    }

    private static decimal? ComputeMetric(AggregationMetric metric, List<AnalyticsRecord> records)
    {
        if (metric.MetricType == MetricType.Count)
            return records.Count;

        if (string.IsNullOrWhiteSpace(metric.MeasureField))
            return null;

        var values = records
            .Where(r => r.Measures.ContainsKey(metric.MeasureField))
            .Select(r => r.Measures[metric.MeasureField])
            .ToList();

        if (values.Count == 0)
            return metric.MetricType == MetricType.Sum ? 0m : null;

        return metric.MetricType switch
        {
            MetricType.Sum => values.Sum(),
            MetricType.Average => Math.Round(values.Average(), 4),
            MetricType.Min => values.Min(),
            MetricType.Max => values.Max(),
            _ => values.Sum()
        };
    }

    private class RecordGroup
    {
        public string? PeriodKey { get; set; }
        public Dictionary<string, string> DimensionValues { get; set; } = new();
        public List<AnalyticsRecord> Records { get; set; } = new();
    }
}
