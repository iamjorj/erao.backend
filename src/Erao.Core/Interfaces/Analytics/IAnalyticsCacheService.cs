namespace Erao.Core.Interfaces.Analytics;

public interface IAnalyticsCacheService
{
    Task<T?> GetAsync<T>(string cacheKey) where T : class;
    Task SetAsync<T>(string cacheKey, T value, TimeSpan? ttl = null) where T : class;
    void Invalidate(string cacheKeyPrefix);

    /// <summary>
    /// Builds a tenant-safe cache key from the user id and request parameters.
    /// </summary>
    string BuildKey(Guid userId, string operation, object request);
}
