using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erao.Core.Interfaces.Analytics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Erao.Infrastructure.Services.Analytics;

public class AnalyticsCacheService : IAnalyticsCacheService
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<AnalyticsCacheService> _logger;
    private readonly TimeSpan _defaultTtl;

    // Track cache keys by prefix so we can invalidate groups of keys
    private readonly ConcurrentDictionary<string, HashSet<string>> _keysByPrefix = new();

    public AnalyticsCacheService(
        IMemoryCache cache,
        IConfiguration configuration,
        ILogger<AnalyticsCacheService> logger)
    {
        _cache = cache;
        _logger = logger;

        var ttlMinutes = configuration.GetValue<int>("Analytics:CacheTtlMinutes", 5);
        _defaultTtl = TimeSpan.FromMinutes(ttlMinutes);
    }

    public Task<T?> GetAsync<T>(string cacheKey) where T : class
    {
        if (_cache.TryGetValue(cacheKey, out T? value))
        {
            _logger.LogDebug("Cache hit for key: {Key}", cacheKey);
            return Task.FromResult(value);
        }

        return Task.FromResult<T?>(null);
    }

    public Task SetAsync<T>(string cacheKey, T value, TimeSpan? ttl = null) where T : class
    {
        var expiry = ttl ?? _defaultTtl;
        _cache.Set(cacheKey, value, expiry);

        // Track the key under its prefix for group invalidation
        var prefix = ExtractPrefix(cacheKey);
        if (!string.IsNullOrEmpty(prefix))
        {
            _keysByPrefix.AddOrUpdate(
                prefix,
                _ => new HashSet<string> { cacheKey },
                (_, set) => { lock (set) { set.Add(cacheKey); } return set; });
        }

        _logger.LogDebug("Cache set for key: {Key}, TTL: {Ttl}", cacheKey, expiry);
        return Task.CompletedTask;
    }

    public void Invalidate(string cacheKeyPrefix)
    {
        if (_keysByPrefix.TryRemove(cacheKeyPrefix, out var keys))
        {
            lock (keys)
            {
                foreach (var key in keys)
                {
                    _cache.Remove(key);
                }
            }

            _logger.LogDebug("Invalidated {Count} cache entries for prefix: {Prefix}", keys.Count, cacheKeyPrefix);
        }
    }

    public string BuildKey(Guid userId, string operation, object request)
    {
        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];

        // Key format: userId:datasetName:operation:hash
        // The userId prefix ensures tenant isolation in the cache.
        return $"{userId}:{operation}:{hash}";
    }

    private static string ExtractPrefix(string cacheKey)
    {
        // Extract "userId:datasetName" or "userId" as prefix
        var parts = cacheKey.Split(':');
        return parts.Length >= 2 ? $"{parts[0]}:{parts[1]}" : parts[0];
    }
}
