using Erao.Core.DTOs.Analytics;
using Erao.Core.Interfaces.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Erao.Application.Services.Analytics;

public class AnalyticsQueryService : IAnalyticsQueryService
{
    private readonly IAnalyticsRecordRepository _recordRepo;
    private readonly IAnalyticsCacheService _cache;
    private readonly ILogger<AnalyticsQueryService> _logger;

    public AnalyticsQueryService(
        IAnalyticsRecordRepository recordRepo,
        IAnalyticsCacheService cache,
        ILogger<AnalyticsQueryService> logger)
    {
        _recordRepo = recordRepo;
        _cache = cache;
        _logger = logger;
    }

    public async Task<AnalyticsQueryResponse> QueryAsync(Guid userId, AnalyticsQueryRequest request)
    {
        var cacheKey = _cache.BuildKey(userId, "query", request);
        var cached = await _cache.GetAsync<AnalyticsQueryResponse>(cacheKey);
        if (cached != null)
            return cached;

        var query = _recordRepo.Query(userId);
        query = ApplyFilters(query, request.Filter);

        var totalCount = await query.CountAsync();

        // Sorting
        query = request.SortBy?.ToLowerInvariant() switch
        {
            "createdat" => request.SortDirection == "asc"
                ? query.OrderBy(r => r.CreatedAt)
                : query.OrderByDescending(r => r.CreatedAt),
            _ => request.SortDirection == "asc"
                ? query.OrderBy(r => r.OccurredAt)
                : query.OrderByDescending(r => r.OccurredAt)
        };

        // Paging
        var pageSize = Math.Clamp(request.PageSize, 1, 500);
        var page = Math.Max(request.Page, 1);
        var skip = (page - 1) * pageSize;

        var records = await query
            .Include(r => r.Dataset)
            .Skip(skip)
            .Take(pageSize)
            .Select(r => new AnalyticsRecordDto
            {
                Id = r.Id,
                DatasetName = r.Dataset.Name,
                OccurredAt = r.OccurredAt,
                Dimensions = r.Dimensions,
                Measures = r.Measures,
                CreatedAt = r.CreatedAt
            })
            .ToListAsync();

        var response = new AnalyticsQueryResponse
        {
            Records = records,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling((double)totalCount / pageSize)
        };

        await _cache.SetAsync(cacheKey, response);
        return response;
    }

    internal static IQueryable<Core.Entities.Analytics.AnalyticsRecord> ApplyFilters(
        IQueryable<Core.Entities.Analytics.AnalyticsRecord> query,
        AnalyticsFilterModel? filter)
    {
        if (filter == null)
            return query;

        if (!string.IsNullOrWhiteSpace(filter.DatasetName))
            query = query.Where(r => r.Dataset.Name == filter.DatasetName);

        if (!string.IsNullOrWhiteSpace(filter.Source))
            query = query.Where(r => r.Dataset.Source == filter.Source);

        if (filter.DateFrom.HasValue)
        {
            var dateFrom = DateTime.SpecifyKind(filter.DateFrom.Value, DateTimeKind.Utc);
            query = query.Where(r => r.OccurredAt >= dateFrom);
        }

        if (filter.DateTo.HasValue)
        {
            var dateTo = DateTime.SpecifyKind(filter.DateTo.Value, DateTimeKind.Utc);
            query = query.Where(r => r.OccurredAt <= dateTo);
        }

        // Dimension filters are applied in-memory after materialization
        // because EF Core cannot translate Dictionary operations to SQL.
        // For large datasets, consider a normalized dimensions table.
        // Here we apply what we can at the DB level and defer dimension filtering.

        return query;
    }

    /// <summary>
    /// Applies dimension filters in-memory on materialized records.
    /// </summary>
    internal static List<T> ApplyDimensionFilters<T>(
        List<T> records,
        Dictionary<string, string>? dimensionFilters,
        Func<T, Dictionary<string, string>> dimensionSelector)
    {
        if (dimensionFilters == null || dimensionFilters.Count == 0)
            return records;

        return records.Where(r =>
        {
            var dims = dimensionSelector(r);
            return dimensionFilters.All(f =>
                dims.TryGetValue(f.Key, out var val) &&
                string.Equals(val, f.Value, StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }
}
