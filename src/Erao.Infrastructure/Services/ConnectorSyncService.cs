using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Erao.Core.Enums;
using Erao.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Erao.Infrastructure.Services;

public class ConnectorSyncService : IConnectorSyncService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IEncryptionService _encryptionService;
    private readonly IParquetConversionService _parquetConversionService;
    private readonly IMinioService _minioService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ConnectorSyncService> _logger;

    public ConnectorSyncService(
        IUnitOfWork unitOfWork,
        IEncryptionService encryptionService,
        IParquetConversionService parquetConversionService,
        IMinioService minioService,
        IHttpClientFactory httpClientFactory,
        ILogger<ConnectorSyncService> logger)
    {
        _unitOfWork = unitOfWork;
        _encryptionService = encryptionService;
        _parquetConversionService = parquetConversionService;
        _minioService = minioService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task SyncAsync(Guid connectorId, Guid userId)
    {
        var connector = await _unitOfWork.AppConnectors.GetByIdAsync(connectorId);
        if (connector == null || connector.UserId != userId)
            throw new InvalidOperationException("Connector not found");

        if (connector.SyncStatus == ConnectorSyncStatus.Syncing)
            throw new InvalidOperationException("Sync already in progress");

        // Set syncing status
        connector.SyncStatus = ConnectorSyncStatus.Syncing;
        connector.SyncErrorMessage = null;
        await _unitOfWork.AppConnectors.UpdateAsync(connector);
        await _unitOfWork.SaveChangesAsync();

        try
        {
            // Decrypt credentials
            var credentialsJson = _encryptionService.Decrypt(connector.EncryptedCredentials);
            var credentials = JsonSerializer.Deserialize<Dictionary<string, string>>(credentialsJson)
                ?? throw new InvalidOperationException("Failed to decrypt credentials");

            // Delete old Parquet files from R2 (for re-sync)
            if (!string.IsNullOrEmpty(connector.ParquetStoragePaths))
            {
                await DeleteOldParquetFilesAsync(connector.ParquetStoragePaths);
            }

            var tableNames = GetTableNames(connector.ConnectorType);
            var parquetPaths = new Dictionary<string, string>();
            var schemaInfoMap = new Dictionary<string, string>();
            var sampleDataMap = new Dictionary<string, string>();
            var rowCountMap = new Dictionary<string, long>();

            foreach (var tableName in tableNames)
            {
                _logger.LogInformation("Syncing table {Table} for connector {ConnectorId}", tableName, connectorId);

                // Fetch all records from API
                var records = connector.ConnectorType switch
                {
                    ConnectorType.Shopify => await FetchShopifyTableAsync(credentials, tableName),
                    ConnectorType.Stripe => await FetchStripeTableAsync(credentials, tableName),
                    _ => throw new InvalidOperationException($"Sync not supported for {connector.ConnectorType}")
                };

                if (records.Count == 0)
                {
                    _logger.LogWarning("No records found for {Table}, skipping", tableName);
                    continue;
                }

                _logger.LogInformation("Fetched {Count} records for {Table}", records.Count, tableName);

                // Convert to CSV stream
                using var csvStream = ConvertToCsvStream(records);

                // Convert CSV to Parquet (temp local file)
                var tempParquetPath = Path.Combine(Path.GetTempPath(), $"connector_{connectorId}_{tableName}.parquet");
                var conversionResult = await _parquetConversionService.ConvertCsvToParquetAsync(csvStream, tempParquetPath);

                if (!conversionResult.Success)
                {
                    _logger.LogWarning("Parquet conversion failed for {Table}: {Error}", tableName, conversionResult.ErrorMessage);
                    continue;
                }

                // Upload Parquet to R2
                var objectKey = $"connector-parquet/{userId}/{connectorId}/{tableName}.parquet";
                await using (var parquetStream = File.OpenRead(tempParquetPath))
                {
                    await _minioService.UploadFileAsync(parquetStream, $"{connectorId}/{tableName}.parquet",
                        "application/octet-stream", userId, "connector-parquet");
                }

                // Clean up temp file
                try { File.Delete(tempParquetPath); } catch { /* best effort */ }

                parquetPaths[tableName] = objectKey;
                if (!string.IsNullOrEmpty(conversionResult.SchemaInfoJson))
                    schemaInfoMap[tableName] = conversionResult.SchemaInfoJson;
                if (!string.IsNullOrEmpty(conversionResult.SampleDataJson))
                    sampleDataMap[tableName] = conversionResult.SampleDataJson;
                rowCountMap[tableName] = conversionResult.RowCount;

                _logger.LogInformation("Uploaded {Table}: {Rows} rows", tableName, conversionResult.RowCount);
            }

            // Update connector with sync results
            connector.ParquetStoragePaths = JsonSerializer.Serialize(parquetPaths);
            connector.SchemaInfo = JsonSerializer.Serialize(schemaInfoMap);
            connector.SampleDataJson = JsonSerializer.Serialize(sampleDataMap);
            connector.TableRowCounts = JsonSerializer.Serialize(rowCountMap);
            connector.SyncStatus = ConnectorSyncStatus.Completed;
            connector.LastSyncedAt = DateTime.UtcNow;

            if (parquetPaths.Count == 0)
            {
                var tableList = string.Join(", ", tableNames);
                connector.SyncErrorMessage = $"Your {connector.ConnectorType} account has no data yet. Checked: {tableList}. Add some data and re-sync.";
                _logger.LogWarning("Connector {ConnectorId} sync completed with 0 tables — account is empty", connectorId);
            }
            else
            {
                connector.SyncErrorMessage = null;
            }

            await _unitOfWork.AppConnectors.UpdateAsync(connector);
            await _unitOfWork.SaveChangesAsync();

            _logger.LogInformation("Connector {ConnectorId} sync completed: {TableCount} tables, {TotalRows} total rows",
                connectorId, parquetPaths.Count, rowCountMap.Values.Sum());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connector sync failed for {ConnectorId}", connectorId);
            connector.SyncStatus = ConnectorSyncStatus.Failed;
            connector.SyncErrorMessage = ex.Message;
            await _unitOfWork.AppConnectors.UpdateAsync(connector);
            await _unitOfWork.SaveChangesAsync();
            throw;
        }
    }

    private async Task DeleteOldParquetFilesAsync(string parquetStoragePathsJson)
    {
        try
        {
            var paths = JsonSerializer.Deserialize<Dictionary<string, string>>(parquetStoragePathsJson);
            if (paths == null) return;

            foreach (var (_, objectKey) in paths)
            {
                try
                {
                    await _minioService.DeleteFileAsync(objectKey);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete old Parquet file: {Key}", objectKey);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse old ParquetStoragePaths for cleanup");
        }
    }

    private static List<string> GetTableNames(ConnectorType type) => type switch
    {
        ConnectorType.Shopify => new List<string> { "orders", "products", "customers" },
        ConnectorType.Stripe => new List<string> { "payments", "subscriptions", "customers", "invoices" },
        _ => new List<string>()
    };

    // ─── Shopify API ─────────────────────────────────────────────────────

    private async Task<List<Dictionary<string, object?>>> FetchShopifyTableAsync(
        Dictionary<string, string> credentials, string tableName)
    {
        var storeUrl = credentials.GetValueOrDefault("storeUrl", "")?.Trim().TrimEnd('/') ?? "";
        var apiKey = credentials.GetValueOrDefault("apiKey", "") ?? "";

        if (string.IsNullOrEmpty(storeUrl) || string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("Shopify credentials missing storeUrl or apiKey");

        // Normalize URL
        if (!storeUrl.Contains('.')) storeUrl += ".myshopify.com";
        if (!storeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) storeUrl = "https://" + storeUrl;

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("X-Shopify-Access-Token", apiKey);

        var allRecords = new List<Dictionary<string, object?>>();
        var url = $"{storeUrl}/admin/api/2024-01/{tableName}.json?limit=250";

        while (!string.IsNullOrEmpty(url))
        {
            var response = await client.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Shopify API error for {Table}: HTTP {Status} — {Body}",
                    tableName, (int)response.StatusCode, json.Length > 500 ? json[..500] : json);
                response.EnsureSuccessStatusCode();
            }

            using var doc = JsonDocument.Parse(json);

            // Shopify wraps results: {"orders": [...]} or {"products": [...]}
            if (doc.RootElement.TryGetProperty(tableName, out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                var batchCount = items.GetArrayLength();
                _logger.LogDebug("Shopify {Table}: got {Count} items in batch", tableName, batchCount);
                foreach (var item in items.EnumerateArray())
                {
                    var flat = FlattenShopifyRecord(item, tableName);
                    if (flat != null) allRecords.Add(flat);
                }
            }
            else
            {
                // Log unexpected response shape
                var props = string.Join(", ", doc.RootElement.EnumerateObject().Select(p => p.Name));
                _logger.LogWarning("Shopify {Table}: response missing '{Table}' array. Root properties: [{Props}]",
                    tableName, tableName, props);
                break;
            }

            // Cursor-based pagination via Link header
            url = ExtractShopifyNextUrl(response);

            // Rate limit: 500ms between requests
            if (!string.IsNullOrEmpty(url))
                await Task.Delay(500);
        }

        return allRecords;
    }

    private static Dictionary<string, object?>? FlattenShopifyRecord(JsonElement item, string tableName)
    {
        return tableName switch
        {
            "orders" => new Dictionary<string, object?>
            {
                ["id"] = GetLong(item, "id"),
                ["order_number"] = GetInt(item, "order_number"),
                ["email"] = GetString(item, "email"),
                ["total_price"] = GetDecimal(item, "total_price"),
                ["subtotal_price"] = GetDecimal(item, "subtotal_price"),
                ["total_tax"] = GetDecimal(item, "total_tax"),
                ["total_discounts"] = GetDecimal(item, "total_discounts"),
                ["financial_status"] = GetString(item, "financial_status"),
                ["fulfillment_status"] = GetString(item, "fulfillment_status"),
                ["currency"] = GetString(item, "currency"),
                ["created_at"] = GetString(item, "created_at"),
                ["updated_at"] = GetString(item, "updated_at"),
                ["cancelled_at"] = GetString(item, "cancelled_at"),
                ["customer_id"] = item.TryGetProperty("customer", out var cust) && cust.ValueKind == JsonValueKind.Object
                    ? GetLong(cust, "id") : null
            },
            "products" => new Dictionary<string, object?>
            {
                ["id"] = GetLong(item, "id"),
                ["title"] = GetString(item, "title"),
                ["vendor"] = GetString(item, "vendor"),
                ["product_type"] = GetString(item, "product_type"),
                ["status"] = GetString(item, "status"),
                ["created_at"] = GetString(item, "created_at"),
                ["updated_at"] = GetString(item, "updated_at"),
                ["published_at"] = GetString(item, "published_at")
            },
            "customers" => new Dictionary<string, object?>
            {
                ["id"] = GetLong(item, "id"),
                ["email"] = GetString(item, "email"),
                ["first_name"] = GetString(item, "first_name"),
                ["last_name"] = GetString(item, "last_name"),
                ["orders_count"] = GetInt(item, "orders_count"),
                ["total_spent"] = GetDecimal(item, "total_spent"),
                ["created_at"] = GetString(item, "created_at"),
                ["updated_at"] = GetString(item, "updated_at")
            },
            _ => null
        };
    }

    private static string? ExtractShopifyNextUrl(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var linkValues))
            return null;

        var linkHeader = string.Join(",", linkValues);
        // Parse: <https://store.myshopify.com/admin/api/...?page_info=xxx&limit=250>; rel="next"
        var parts = linkHeader.Split(',');
        foreach (var part in parts)
        {
            if (part.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase))
            {
                var urlStart = part.IndexOf('<');
                var urlEnd = part.IndexOf('>');
                if (urlStart >= 0 && urlEnd > urlStart)
                {
                    return part.Substring(urlStart + 1, urlEnd - urlStart - 1);
                }
            }
        }
        return null;
    }

    // ─── Stripe API ──────────────────────────────────────────────────────

    private async Task<List<Dictionary<string, object?>>> FetchStripeTableAsync(
        Dictionary<string, string> credentials, string tableName)
    {
        var secretKey = credentials.GetValueOrDefault("secretKey", "") ?? "";
        if (string.IsNullOrEmpty(secretKey))
            throw new InvalidOperationException("Stripe credentials missing secretKey");

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);

        // Map table name to Stripe API resource
        var resource = tableName switch
        {
            "payments" => "charges",
            "subscriptions" => "subscriptions",
            "customers" => "customers",
            "invoices" => "invoices",
            _ => throw new InvalidOperationException($"Unknown Stripe table: {tableName}")
        };

        var allRecords = new List<Dictionary<string, object?>>();
        string? startingAfter = null;
        var hasMore = true;

        while (hasMore)
        {
            var url = $"https://api.stripe.com/v1/{resource}?limit=100";
            if (!string.IsNullOrEmpty(startingAfter))
                url += $"&starting_after={startingAfter}";

            var response = await client.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Stripe API error for {Table} ({Resource}): HTTP {Status} — {Body}",
                    tableName, resource, (int)response.StatusCode, json.Length > 500 ? json[..500] : json);
                response.EnsureSuccessStatusCode();
            }

            using var doc = JsonDocument.Parse(json);

            hasMore = doc.RootElement.TryGetProperty("has_more", out var hm) && hm.GetBoolean();

            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array)
            {
                var batchCount = data.GetArrayLength();
                _logger.LogDebug("Stripe {Table} ({Resource}): got {Count} items in batch", tableName, resource, batchCount);
                foreach (var item in data.EnumerateArray())
                {
                    var flat = FlattenStripeRecord(item, tableName);
                    if (flat != null)
                    {
                        allRecords.Add(flat);
                        startingAfter = GetString(item, "id");
                    }
                }
            }
            else
            {
                _logger.LogWarning("Stripe {Table} ({Resource}): response missing 'data' array", tableName, resource);
                break;
            }

            // Rate limit: 500ms between requests
            if (hasMore) await Task.Delay(500);
        }

        return allRecords;
    }

    private static Dictionary<string, object?>? FlattenStripeRecord(JsonElement item, string tableName)
    {
        return tableName switch
        {
            "payments" => new Dictionary<string, object?>
            {
                ["id"] = GetString(item, "id"),
                ["amount"] = CentsToDollars(GetLong(item, "amount")),
                ["currency"] = GetString(item, "currency"),
                ["status"] = GetString(item, "status"),
                ["description"] = GetString(item, "description"),
                ["customer_id"] = GetString(item, "customer"),
                ["payment_method"] = GetNestedString(item, "payment_method_details", "type"),
                ["created_at"] = UnixToIso(GetLong(item, "created")),
                ["receipt_email"] = GetString(item, "receipt_email")
            },
            "subscriptions" => new Dictionary<string, object?>
            {
                ["id"] = GetString(item, "id"),
                ["customer_id"] = GetString(item, "customer"),
                ["status"] = GetString(item, "status"),
                ["plan_id"] = GetNestedString(item, "plan", "id"),
                ["plan_amount"] = CentsToDollars(GetNestedLong(item, "plan", "amount")),
                ["plan_interval"] = GetNestedString(item, "plan", "interval"),
                ["current_period_start"] = UnixToIso(GetLong(item, "current_period_start")),
                ["current_period_end"] = UnixToIso(GetLong(item, "current_period_end")),
                ["cancel_at_period_end"] = GetBool(item, "cancel_at_period_end"),
                ["created_at"] = UnixToIso(GetLong(item, "created"))
            },
            "customers" => new Dictionary<string, object?>
            {
                ["id"] = GetString(item, "id"),
                ["email"] = GetString(item, "email"),
                ["name"] = GetString(item, "name"),
                ["created_at"] = UnixToIso(GetLong(item, "created")),
                ["balance"] = CentsToDollars(GetLong(item, "balance")),
                ["currency"] = GetString(item, "currency"),
                ["delinquent"] = GetBool(item, "delinquent")
            },
            "invoices" => new Dictionary<string, object?>
            {
                ["id"] = GetString(item, "id"),
                ["customer_id"] = GetString(item, "customer"),
                ["subscription_id"] = GetString(item, "subscription"),
                ["amount_due"] = CentsToDollars(GetLong(item, "amount_due")),
                ["amount_paid"] = CentsToDollars(GetLong(item, "amount_paid")),
                ["status"] = GetString(item, "status"),
                ["currency"] = GetString(item, "currency"),
                ["created_at"] = UnixToIso(GetLong(item, "created")),
                ["due_date"] = UnixToIso(GetLong(item, "due_date")),
                ["paid_at"] = UnixToIso(GetLong(item, "status_transitions.paid_at") ?? GetNestedLong(item, "status_transitions", "paid_at"))
            },
            _ => null
        };
    }

    // ─── CSV Conversion ──────────────────────────────────────────────────

    private static MemoryStream ConvertToCsvStream(List<Dictionary<string, object?>> records)
    {
        if (records.Count == 0) return new MemoryStream();

        var headers = records[0].Keys.ToList();
        var sb = new StringBuilder();

        // Header row
        sb.AppendLine(string.Join(",", headers.Select(CsvEscape)));

        // Data rows
        foreach (var record in records)
        {
            var values = headers.Select(h =>
            {
                var val = record.GetValueOrDefault(h);
                return CsvEscape(val?.ToString() ?? "");
            });
            sb.AppendLine(string.Join(",", values));
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return new MemoryStream(bytes);
    }

    private static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('\n') || value.Contains('\r') || value.Contains('"'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    // ─── JSON Helpers ────────────────────────────────────────────────────

    private static string? GetString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString();
        if (el.TryGetProperty(prop, out val) && val.ValueKind != JsonValueKind.Null)
            return val.ToString();
        return null;
    }

    private static long? GetLong(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number) return val.GetInt64();
            if (val.ValueKind == JsonValueKind.String && long.TryParse(val.GetString(), out var l)) return l;
        }
        return null;
    }

    private static int? GetInt(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number) return val.GetInt32();
            if (val.ValueKind == JsonValueKind.String && int.TryParse(val.GetString(), out var i)) return i;
        }
        return null;
    }

    private static decimal? GetDecimal(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number) return val.GetDecimal();
            if (val.ValueKind == JsonValueKind.String && decimal.TryParse(val.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
        }
        return null;
    }

    private static bool? GetBool(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.True) return true;
            if (val.ValueKind == JsonValueKind.False) return false;
        }
        return null;
    }

    private static string? GetNestedString(JsonElement el, string outerProp, string innerProp)
    {
        if (el.TryGetProperty(outerProp, out var outer) && outer.ValueKind == JsonValueKind.Object)
            return GetString(outer, innerProp);
        return null;
    }

    private static long? GetNestedLong(JsonElement el, string outerProp, string innerProp)
    {
        if (el.TryGetProperty(outerProp, out var outer) && outer.ValueKind == JsonValueKind.Object)
            return GetLong(outer, innerProp);
        return null;
    }

    private static decimal? CentsToDollars(long? cents)
    {
        if (cents == null) return null;
        return cents.Value / 100m;
    }

    private static string? UnixToIso(long? unixTimestamp)
    {
        if (unixTimestamp == null || unixTimestamp == 0) return null;
        return DateTimeOffset.FromUnixTimeSeconds(unixTimestamp.Value)
            .UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
