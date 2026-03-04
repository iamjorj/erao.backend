using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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

            // Dynamic table discovery for Airtable and Google Sheets
            if (connector.ConnectorType == ConnectorType.Airtable)
                tableNames = await DiscoverAirtableTablesAsync(credentials);
            else if (connector.ConnectorType == ConnectorType.GoogleSheets)
                tableNames = await DiscoverGoogleSheetsTablesAsync(credentials);

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
                    ConnectorType.WooCommerce => await FetchWooCommerceTableAsync(credentials, tableName),
                    ConnectorType.HubSpot => await FetchHubSpotTableAsync(credentials, tableName),
                    ConnectorType.Notion => await FetchNotionTableAsync(credentials, tableName),
                    ConnectorType.Airtable => await FetchAirtableTableAsync(credentials, tableName),
                    ConnectorType.Salesforce => await FetchSalesforceTableAsync(credentials, tableName),
                    ConnectorType.QuickBooks => await FetchQuickBooksTableAsync(credentials, tableName),
                    ConnectorType.GoogleAnalytics => await FetchGoogleAnalyticsTableAsync(credentials, tableName),
                    ConnectorType.GoogleSheets => await FetchGoogleSheetsTableAsync(credentials, tableName),
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
        ConnectorType.WooCommerce => new List<string> { "orders", "products", "customers", "coupons" },
        ConnectorType.HubSpot => new List<string> { "contacts", "deals", "companies", "tickets" },
        ConnectorType.Notion => new List<string> { "databases", "pages" },
        ConnectorType.Salesforce => new List<string> { "leads", "opportunities", "accounts", "cases" },
        ConnectorType.QuickBooks => new List<string> { "invoices", "expenses", "accounts", "profit_and_loss" },
        ConnectorType.GoogleAnalytics => new List<string> { "sessions", "pageviews", "conversions" },
        // Airtable and GoogleSheets use dynamic table discovery — return empty here
        ConnectorType.Airtable => new List<string>(),
        ConnectorType.GoogleSheets => new List<string>(),
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

    // ─── Google Service Account Auth ────────────────────────────────────

    private async Task<string> GetGoogleAccessTokenAsync(string serviceAccountJson)
    {
        using var saDoc = JsonDocument.Parse(serviceAccountJson);
        var root = saDoc.RootElement;
        var clientEmail = root.GetProperty("client_email").GetString()!;
        var privateKeyPem = root.GetProperty("private_key").GetString()!;
        var tokenUri = root.TryGetProperty("token_uri", out var tu)
            ? tu.GetString()! : "https://oauth2.googleapis.com/token";

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var claimSet = JsonSerializer.Serialize(new
        {
            iss = clientEmail,
            scope = "https://www.googleapis.com/auth/analytics.readonly https://www.googleapis.com/auth/spreadsheets.readonly",
            aud = tokenUri,
            iat = now,
            exp = now + 3600
        });
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(claimSet));
        var signingInput = $"{header}.{payload}";

        // Parse PEM private key
        var keyContent = privateKeyPem
            .Replace("-----BEGIN PRIVATE KEY-----", "")
            .Replace("-----END PRIVATE KEY-----", "")
            .Replace("\n", "").Replace("\r", "").Trim();
        var keyBytes = Convert.FromBase64String(keyContent);
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(keyBytes, out _);
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var jwt = $"{signingInput}.{Base64UrlEncode(signature)}";

        // Exchange JWT for access token
        var client = _httpClientFactory.CreateClient();
        var tokenResponse = await client.PostAsync(tokenUri, new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
            new KeyValuePair<string, string>("assertion", jwt)
        }));
        var tokenJson = await tokenResponse.Content.ReadAsStringAsync();
        tokenResponse.EnsureSuccessStatusCode();

        using var tokenDoc = JsonDocument.Parse(tokenJson);
        return tokenDoc.RootElement.GetProperty("access_token").GetString()!;
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).Replace("+", "-").Replace("/", "_").TrimEnd('=');

    public async Task<(bool Success, string Message, string? AccountName)> TestGoogleConnectionAsync(
        ConnectorType type, Dictionary<string, string> credentials)
    {
        try
        {
            var saJson = credentials.GetValueOrDefault("serviceAccountJson", "") ?? "";
            if (string.IsNullOrWhiteSpace(saJson))
                return (false, "Service Account JSON is required", null);

            var accessToken = await GetGoogleAccessTokenAsync(saJson);
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            if (type == ConnectorType.GoogleAnalytics)
            {
                var propertyId = credentials.GetValueOrDefault("propertyId", "") ?? "";
                if (string.IsNullOrWhiteSpace(propertyId))
                    return (false, "Property ID is required", null);

                var resp = await client.GetAsync($"https://analyticsdata.googleapis.com/v1beta/properties/{propertyId}/metadata");
                if (resp.IsSuccessStatusCode)
                    return (true, $"Connected to GA4 property {propertyId}", $"GA4-{propertyId}");
                var body = await resp.Content.ReadAsStringAsync();
                return (false, $"GA4 API returned {(int)resp.StatusCode}: {(body.Length > 200 ? body[..200] : body)}", null);
            }
            else // GoogleSheets
            {
                var spreadsheetId = credentials.GetValueOrDefault("spreadsheetId", "") ?? "";
                if (string.IsNullOrWhiteSpace(spreadsheetId))
                    return (false, "Spreadsheet ID is required", null);

                var resp = await client.GetAsync($"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}?fields=properties.title");
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var title = doc.RootElement.GetProperty("properties").GetProperty("title").GetString();
                    return (true, $"Connected to \"{title}\"", title);
                }
                var errBody = await resp.Content.ReadAsStringAsync();
                return (false, $"Sheets API returned {(int)resp.StatusCode}: {(errBody.Length > 200 ? errBody[..200] : errBody)}", null);
            }
        }
        catch (Exception ex)
        {
            return (false, $"Google auth failed: {ex.Message}", null);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static string SanitizeTableName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(
            name.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_'),
            @"[^a-z0-9_]", "");

    private static string SanitizeColumnName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(
            name.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_'),
            @"[^a-z0-9_]", "");

    private static string[] GetArrayNames(JsonElement parent)
    {
        var names = new List<string>();
        foreach (var prop in parent.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array)
                names.Add(prop.Name);
        }
        return names.ToArray();
    }

    // ─── Dynamic Table Discovery ────────────────────────────────────────

    private async Task<List<string>> DiscoverAirtableTablesAsync(Dictionary<string, string> credentials)
    {
        var token = credentials.GetValueOrDefault("personalAccessToken", "") ?? "";
        var baseId = credentials.GetValueOrDefault("baseId", "") ?? "";

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync($"https://api.airtable.com/v0/meta/bases/{baseId}/tables");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var tables = new List<string>();
        if (doc.RootElement.TryGetProperty("tables", out var tablesArr))
        {
            foreach (var t in tablesArr.EnumerateArray())
            {
                var name = t.GetProperty("name").GetString();
                if (!string.IsNullOrEmpty(name))
                    tables.Add(SanitizeTableName(name));
            }
        }
        _logger.LogInformation("Airtable base {BaseId}: discovered {Count} tables", baseId, tables.Count);
        return tables;
    }

    private async Task<List<string>> DiscoverGoogleSheetsTablesAsync(Dictionary<string, string> credentials)
    {
        var saJson = credentials.GetValueOrDefault("serviceAccountJson", "") ?? "";
        var spreadsheetId = credentials.GetValueOrDefault("spreadsheetId", "") ?? "";
        var accessToken = await GetGoogleAccessTokenAsync(saJson);

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync($"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}?fields=sheets.properties.title");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var tables = new List<string>();
        if (doc.RootElement.TryGetProperty("sheets", out var sheets))
        {
            foreach (var s in sheets.EnumerateArray())
            {
                var title = s.GetProperty("properties").GetProperty("title").GetString();
                if (!string.IsNullOrEmpty(title))
                    tables.Add(SanitizeTableName(title));
            }
        }
        _logger.LogInformation("Google Sheets {SpreadsheetId}: discovered {Count} sheets", spreadsheetId, tables.Count);
        return tables;
    }

    // ─── WooCommerce API ────────────────────────────────────────────────

    private async Task<List<Dictionary<string, object?>>> FetchWooCommerceTableAsync(
        Dictionary<string, string> credentials, string tableName)
    {
        var storeUrl = (credentials.GetValueOrDefault("storeUrl", "") ?? "").Trim().TrimEnd('/');
        var consumerKey = credentials.GetValueOrDefault("consumerKey", "") ?? "";
        var consumerSecret = credentials.GetValueOrDefault("consumerSecret", "") ?? "";

        if (string.IsNullOrEmpty(storeUrl) || string.IsNullOrEmpty(consumerKey))
            throw new InvalidOperationException("WooCommerce credentials missing storeUrl or consumerKey");

        if (!storeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            storeUrl = "https://" + storeUrl;

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        // Use HTTP Basic Auth instead of passing credentials in query string
        var basicAuthValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{consumerKey}:{consumerSecret}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicAuthValue);

        var allRecords = new List<Dictionary<string, object?>>();
        var page = 1;
        var hasMore = true;

        while (hasMore)
        {
            var url = $"{storeUrl}/wp-json/wc/v3/{tableName}?per_page=100&page={page}";

            var response = await client.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("WooCommerce API error for {Table}: HTTP {Status}", tableName, (int)response.StatusCode);
                response.EnsureSuccessStatusCode();
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                break;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var flat = FlattenWooCommerceRecord(item, tableName);
                if (flat != null) allRecords.Add(flat);
            }

            // Check X-WP-TotalPages header
            if (response.Headers.TryGetValues("X-WP-TotalPages", out var totalPagesValues))
            {
                var totalPages = int.Parse(totalPagesValues.First());
                hasMore = page < totalPages;
            }
            else
            {
                hasMore = doc.RootElement.GetArrayLength() == 100;
            }

            page++;
            if (hasMore) await Task.Delay(500);
        }

        return allRecords;
    }

    private static Dictionary<string, object?>? FlattenWooCommerceRecord(JsonElement item, string tableName)
    {
        return tableName switch
        {
            "orders" => new Dictionary<string, object?>
            {
                ["id"] = GetLong(item, "id"),
                ["order_number"] = GetString(item, "number"),
                ["status"] = GetString(item, "status"),
                ["total"] = GetDecimal(item, "total"),
                ["subtotal"] = GetDecimal(item, "subtotal") ?? GetNestedDecimal(item, "subtotal"),
                ["total_tax"] = GetDecimal(item, "total_tax"),
                ["discount_total"] = GetDecimal(item, "discount_total"),
                ["shipping_total"] = GetDecimal(item, "shipping_total"),
                ["payment_method"] = GetString(item, "payment_method_title"),
                ["customer_id"] = GetLong(item, "customer_id"),
                ["billing_email"] = GetNestedString(item, "billing", "email"),
                ["currency"] = GetString(item, "currency"),
                ["date_created"] = GetString(item, "date_created"),
                ["date_modified"] = GetString(item, "date_modified")
            },
            "products" => new Dictionary<string, object?>
            {
                ["id"] = GetLong(item, "id"),
                ["name"] = GetString(item, "name"),
                ["type"] = GetString(item, "type"),
                ["status"] = GetString(item, "status"),
                ["sku"] = GetString(item, "sku"),
                ["price"] = GetDecimal(item, "price"),
                ["regular_price"] = GetDecimal(item, "regular_price"),
                ["sale_price"] = GetDecimal(item, "sale_price"),
                ["stock_quantity"] = GetInt(item, "stock_quantity"),
                ["stock_status"] = GetString(item, "stock_status"),
                ["categories"] = item.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", cats.EnumerateArray().Select(c => GetString(c, "name")).Where(n => n != null))
                    : null,
                ["date_created"] = GetString(item, "date_created")
            },
            "customers" => new Dictionary<string, object?>
            {
                ["id"] = GetLong(item, "id"),
                ["email"] = GetString(item, "email"),
                ["first_name"] = GetString(item, "first_name"),
                ["last_name"] = GetString(item, "last_name"),
                ["orders_count"] = GetInt(item, "orders_count"),
                ["total_spent"] = GetDecimal(item, "total_spent"),
                ["date_created"] = GetString(item, "date_created")
            },
            "coupons" => new Dictionary<string, object?>
            {
                ["id"] = GetLong(item, "id"),
                ["code"] = GetString(item, "code"),
                ["discount_type"] = GetString(item, "discount_type"),
                ["amount"] = GetDecimal(item, "amount"),
                ["usage_count"] = GetInt(item, "usage_count"),
                ["usage_limit"] = GetInt(item, "usage_limit"),
                ["date_created"] = GetString(item, "date_created"),
                ["date_expires"] = GetString(item, "date_expires")
            },
            _ => null
        };
    }

    private static decimal? GetNestedDecimal(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number) return val.GetDecimal();
            if (val.ValueKind == JsonValueKind.String && decimal.TryParse(val.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
        }
        return null;
    }

    // ─── HubSpot API ────────────────────────────────────────────────────

    private async Task<List<Dictionary<string, object?>>> FetchHubSpotTableAsync(
        Dictionary<string, string> credentials, string tableName)
    {
        var token = credentials.GetValueOrDefault("privateAppToken", "") ?? "";
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("HubSpot credentials missing privateAppToken");

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var objectType = tableName; // contacts, deals, companies, tickets
        var properties = GetHubSpotProperties(tableName);
        var propertiesParam = string.Join(",", properties);

        var allRecords = new List<Dictionary<string, object?>>();
        string? after = null;

        do
        {
            var url = $"https://api.hubapi.com/crm/v3/objects/{objectType}?limit=100&properties={propertiesParam}";
            if (!string.IsNullOrEmpty(after)) url += $"&after={after}";

            var response = await client.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("HubSpot API error for {Table}: HTTP {Status}", tableName, (int)response.StatusCode);
                response.EnsureSuccessStatusCode();
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    var flat = FlattenHubSpotRecord(item, tableName);
                    if (flat != null) allRecords.Add(flat);
                }
            }

            // Cursor pagination
            after = null;
            if (doc.RootElement.TryGetProperty("paging", out var paging) &&
                paging.TryGetProperty("next", out var next) &&
                next.TryGetProperty("after", out var afterVal))
            {
                after = afterVal.GetString();
            }

            if (!string.IsNullOrEmpty(after)) await Task.Delay(200);
        } while (!string.IsNullOrEmpty(after));

        return allRecords;
    }

    private static string[] GetHubSpotProperties(string tableName) => tableName switch
    {
        "contacts" => new[] { "email", "firstname", "lastname", "phone", "company", "lifecyclestage", "hs_lead_status", "hs_analytics_source", "createdate" },
        "deals" => new[] { "dealname", "amount", "dealstage", "pipeline", "closedate", "hubspot_owner_id", "createdate" },
        "companies" => new[] { "name", "domain", "industry", "annualrevenue", "numberofemployees", "city", "country", "createdate" },
        "tickets" => new[] { "subject", "hs_pipeline_stage", "hs_ticket_priority", "hs_ticket_category", "createdate", "closed_date" },
        _ => new[] { "createdate" }
    };

    private static Dictionary<string, object?>? FlattenHubSpotRecord(JsonElement item, string tableName)
    {
        var id = GetString(item, "id");
        if (!item.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return null;

        return tableName switch
        {
            "contacts" => new Dictionary<string, object?>
            {
                ["id"] = id,
                ["email"] = GetString(props, "email"),
                ["first_name"] = GetString(props, "firstname"),
                ["last_name"] = GetString(props, "lastname"),
                ["phone"] = GetString(props, "phone"),
                ["company"] = GetString(props, "company"),
                ["lifecycle_stage"] = GetString(props, "lifecyclestage"),
                ["lead_status"] = GetString(props, "hs_lead_status"),
                ["source"] = GetString(props, "hs_analytics_source"),
                ["created_at"] = GetString(props, "createdate")
            },
            "deals" => new Dictionary<string, object?>
            {
                ["id"] = id,
                ["deal_name"] = GetString(props, "dealname"),
                ["amount"] = GetDecimal(props, "amount"),
                ["stage"] = GetString(props, "dealstage"),
                ["pipeline"] = GetString(props, "pipeline"),
                ["close_date"] = GetString(props, "closedate"),
                ["owner_id"] = GetString(props, "hubspot_owner_id"),
                ["created_at"] = GetString(props, "createdate")
            },
            "companies" => new Dictionary<string, object?>
            {
                ["id"] = id,
                ["name"] = GetString(props, "name"),
                ["domain"] = GetString(props, "domain"),
                ["industry"] = GetString(props, "industry"),
                ["annual_revenue"] = GetDecimal(props, "annualrevenue"),
                ["number_of_employees"] = GetInt(props, "numberofemployees"),
                ["city"] = GetString(props, "city"),
                ["country"] = GetString(props, "country"),
                ["created_at"] = GetString(props, "createdate")
            },
            "tickets" => new Dictionary<string, object?>
            {
                ["id"] = id,
                ["subject"] = GetString(props, "subject"),
                ["status"] = GetString(props, "hs_pipeline_stage"),
                ["priority"] = GetString(props, "hs_ticket_priority"),
                ["category"] = GetString(props, "hs_ticket_category"),
                ["created_at"] = GetString(props, "createdate"),
                ["closed_at"] = GetString(props, "closed_date")
            },
            _ => null
        };
    }

    // ─── Notion API ─────────────────────────────────────────────────────

    private async Task<List<Dictionary<string, object?>>> FetchNotionTableAsync(
        Dictionary<string, string> credentials, string tableName)
    {
        var token = credentials.GetValueOrDefault("integrationToken", "") ?? "";
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Notion credentials missing integrationToken");

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("Notion-Version", "2022-06-28");

        var allRecords = new List<Dictionary<string, object?>>();

        if (tableName == "databases")
        {
            string? startCursor = null;
            do
            {
                var body = new Dictionary<string, object> { ["filter"] = new { value = "database", property = "object" }, ["page_size"] = 100 };
                if (!string.IsNullOrEmpty(startCursor)) body["start_cursor"] = startCursor;

                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.notion.com/v1/search")
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
                };
                var response = await client.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                response.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("results", out var results))
                {
                    foreach (var item in results.EnumerateArray())
                    {
                        allRecords.Add(FlattenNotionDatabase(item));
                    }
                }

                startCursor = doc.RootElement.TryGetProperty("has_more", out var hm) && hm.GetBoolean() &&
                              doc.RootElement.TryGetProperty("next_cursor", out var nc) && nc.ValueKind == JsonValueKind.String
                    ? nc.GetString() : null;

                if (!string.IsNullOrEmpty(startCursor)) await Task.Delay(350);
            } while (!string.IsNullOrEmpty(startCursor));
        }
        else if (tableName == "pages")
        {
            string? startCursor = null;
            do
            {
                var body = new Dictionary<string, object> { ["filter"] = new { value = "page", property = "object" }, ["page_size"] = 100 };
                if (!string.IsNullOrEmpty(startCursor)) body["start_cursor"] = startCursor;

                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.notion.com/v1/search")
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
                };
                var response = await client.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                response.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("results", out var results))
                {
                    foreach (var item in results.EnumerateArray())
                    {
                        allRecords.Add(FlattenNotionPage(item));
                    }
                }

                startCursor = doc.RootElement.TryGetProperty("has_more", out var hm) && hm.GetBoolean() &&
                              doc.RootElement.TryGetProperty("next_cursor", out var nc) && nc.ValueKind == JsonValueKind.String
                    ? nc.GetString() : null;

                if (!string.IsNullOrEmpty(startCursor)) await Task.Delay(350);
            } while (!string.IsNullOrEmpty(startCursor));
        }

        return allRecords;
    }

    private static Dictionary<string, object?> FlattenNotionDatabase(JsonElement item)
    {
        var title = "";
        if (item.TryGetProperty("title", out var titleArr) && titleArr.ValueKind == JsonValueKind.Array)
        {
            title = string.Join("", titleArr.EnumerateArray()
                .Select(t => t.TryGetProperty("plain_text", out var pt) ? pt.GetString() : ""));
        }

        var propertyCount = 0;
        if (item.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            propertyCount = props.EnumerateObject().Count();

        return new Dictionary<string, object?>
        {
            ["id"] = GetString(item, "id"),
            ["title"] = title,
            ["created_at"] = GetString(item, "created_time"),
            ["last_edited_at"] = GetString(item, "last_edited_time"),
            ["created_by"] = GetNestedString(item, "created_by", "id"),
            ["property_count"] = propertyCount
        };
    }

    private static Dictionary<string, object?> FlattenNotionPage(JsonElement item)
    {
        var title = "";
        if (item.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in props.EnumerateObject())
            {
                if (prop.Value.TryGetProperty("type", out var t) && t.GetString() == "title" &&
                    prop.Value.TryGetProperty("title", out var titleArr) && titleArr.ValueKind == JsonValueKind.Array)
                {
                    title = string.Join("", titleArr.EnumerateArray()
                        .Select(x => x.TryGetProperty("plain_text", out var pt) ? pt.GetString() : ""));
                    break;
                }
            }
        }

        string? databaseId = null;
        string? parentPageId = null;
        if (item.TryGetProperty("parent", out var parent) && parent.ValueKind == JsonValueKind.Object)
        {
            if (parent.TryGetProperty("type", out var parentType))
            {
                var pType = parentType.GetString();
                if (pType == "database_id") databaseId = GetString(parent, "database_id");
                else if (pType == "page_id") parentPageId = GetString(parent, "page_id");
            }
        }

        return new Dictionary<string, object?>
        {
            ["id"] = GetString(item, "id"),
            ["title"] = title,
            ["database_id"] = databaseId,
            ["parent_page_id"] = parentPageId,
            ["created_at"] = GetString(item, "created_time"),
            ["last_edited_at"] = GetString(item, "last_edited_time"),
            ["created_by"] = GetNestedString(item, "created_by", "id"),
            ["archived"] = GetBool(item, "archived")
        };
    }

    // ─── Airtable API ───────────────────────────────────────────────────

    private async Task<List<Dictionary<string, object?>>> FetchAirtableTableAsync(
        Dictionary<string, string> credentials, string tableName)
    {
        var token = credentials.GetValueOrDefault("personalAccessToken", "") ?? "";
        var baseId = credentials.GetValueOrDefault("baseId", "") ?? "";

        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(baseId))
            throw new InvalidOperationException("Airtable credentials missing personalAccessToken or baseId");

        // We need the original table name (not sanitized) to query Airtable.
        // Discover tables again to find the mapping.
        var originalTableName = await ResolveAirtableTableNameAsync(credentials, tableName);

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var allRecords = new List<Dictionary<string, object?>>();
        string? offset = null;

        do
        {
            var url = $"https://api.airtable.com/v0/{baseId}/{Uri.EscapeDataString(originalTableName)}?pageSize=100";
            if (!string.IsNullOrEmpty(offset)) url += $"&offset={offset}";

            var response = await client.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
            {
                foreach (var rec in records.EnumerateArray())
                {
                    var flat = FlattenAirtableRecord(rec);
                    if (flat != null) allRecords.Add(flat);
                }
            }

            offset = doc.RootElement.TryGetProperty("offset", out var off) && off.ValueKind == JsonValueKind.String
                ? off.GetString() : null;

            if (!string.IsNullOrEmpty(offset)) await Task.Delay(250);
        } while (!string.IsNullOrEmpty(offset));

        return allRecords;
    }

    private async Task<string> ResolveAirtableTableNameAsync(Dictionary<string, string> credentials, string sanitizedName)
    {
        var token = credentials.GetValueOrDefault("personalAccessToken", "") ?? "";
        var baseId = credentials.GetValueOrDefault("baseId", "") ?? "";

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.GetAsync($"https://api.airtable.com/v0/meta/bases/{baseId}/tables");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("tables", out var tables))
        {
            foreach (var t in tables.EnumerateArray())
            {
                var name = t.GetProperty("name").GetString() ?? "";
                if (SanitizeTableName(name) == sanitizedName)
                    return name;
            }
        }
        return sanitizedName; // fallback
    }

    private static Dictionary<string, object?>? FlattenAirtableRecord(JsonElement rec)
    {
        var result = new Dictionary<string, object?>();
        result["id"] = GetString(rec, "id");
        result["created_time"] = GetString(rec, "createdTime");

        if (rec.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in fields.EnumerateObject())
            {
                var colName = SanitizeColumnName(prop.Name);
                if (string.IsNullOrEmpty(colName)) colName = "field";

                var val = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => (object?)prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetDecimal(out var d) ? d : (object?)prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Array => string.Join(", ", prop.Value.EnumerateArray().Select(v =>
                        v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())),
                    JsonValueKind.Null => null,
                    _ => prop.Value.ToString()
                };
                result[colName] = val;
            }
        }
        return result.Count > 2 ? result : null; // skip if only id + created_time (empty record)
    }

    // ─── Salesforce API ─────────────────────────────────────────────────

    private async Task<List<Dictionary<string, object?>>> FetchSalesforceTableAsync(
        Dictionary<string, string> credentials, string tableName)
    {
        var instanceUrl = (credentials.GetValueOrDefault("instanceUrl", "") ?? "").Trim().TrimEnd('/');
        var accessToken = credentials.GetValueOrDefault("accessToken", "") ?? "";

        if (string.IsNullOrEmpty(instanceUrl) || string.IsNullOrEmpty(accessToken))
            throw new InvalidOperationException("Salesforce credentials missing instanceUrl or accessToken");

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var sfObject = tableName switch
        {
            "leads" => "Lead",
            "opportunities" => "Opportunity",
            "accounts" => "Account",
            "cases" => "Case",
            _ => throw new InvalidOperationException($"Unknown Salesforce table: {tableName}")
        };

        var fields = GetSalesforceFields(tableName);
        var soql = Uri.EscapeDataString($"SELECT {string.Join(",", fields)} FROM {sfObject} ORDER BY CreatedDate DESC");

        var allRecords = new List<Dictionary<string, object?>>();
        var url = $"{instanceUrl}/services/data/v59.0/query?q={soql}";

        while (!string.IsNullOrEmpty(url))
        {
            var response = await client.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Salesforce API error for {Table}: HTTP {Status}", tableName, (int)response.StatusCode);
                response.EnsureSuccessStatusCode();
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in records.EnumerateArray())
                {
                    var flat = FlattenSalesforceRecord(item, tableName);
                    if (flat != null) allRecords.Add(flat);
                }
            }

            // nextRecordsUrl pagination
            url = doc.RootElement.TryGetProperty("nextRecordsUrl", out var nextUrl) && nextUrl.ValueKind == JsonValueKind.String
                ? $"{instanceUrl}{nextUrl.GetString()}" : null;

            if (!string.IsNullOrEmpty(url)) await Task.Delay(200);
        }

        return allRecords;
    }

    private static string[] GetSalesforceFields(string tableName) => tableName switch
    {
        "leads" => new[] { "Id", "Name", "Email", "Company", "Title", "Status", "LeadSource", "Industry", "AnnualRevenue", "CreatedDate", "ConvertedDate" },
        "opportunities" => new[] { "Id", "Name", "Amount", "StageName", "Probability", "CloseDate", "AccountId", "OwnerId", "Type", "CreatedDate" },
        "accounts" => new[] { "Id", "Name", "Industry", "Type", "AnnualRevenue", "NumberOfEmployees", "BillingCity", "BillingCountry", "CreatedDate" },
        "cases" => new[] { "Id", "Subject", "Status", "Priority", "Type", "AccountId", "ContactId", "CreatedDate", "ClosedDate" },
        _ => new[] { "Id", "CreatedDate" }
    };

    private static Dictionary<string, object?>? FlattenSalesforceRecord(JsonElement item, string tableName)
    {
        return tableName switch
        {
            "leads" => new Dictionary<string, object?>
            {
                ["id"] = GetString(item, "Id"),
                ["name"] = GetString(item, "Name"),
                ["email"] = GetString(item, "Email"),
                ["company"] = GetString(item, "Company"),
                ["title"] = GetString(item, "Title"),
                ["status"] = GetString(item, "Status"),
                ["source"] = GetString(item, "LeadSource"),
                ["industry"] = GetString(item, "Industry"),
                ["annual_revenue"] = GetDecimal(item, "AnnualRevenue"),
                ["created_at"] = GetString(item, "CreatedDate"),
                ["converted_at"] = GetString(item, "ConvertedDate")
            },
            "opportunities" => new Dictionary<string, object?>
            {
                ["id"] = GetString(item, "Id"),
                ["name"] = GetString(item, "Name"),
                ["amount"] = GetDecimal(item, "Amount"),
                ["stage"] = GetString(item, "StageName"),
                ["probability"] = GetInt(item, "Probability"),
                ["close_date"] = GetString(item, "CloseDate"),
                ["account_id"] = GetString(item, "AccountId"),
                ["owner_id"] = GetString(item, "OwnerId"),
                ["type"] = GetString(item, "Type"),
                ["created_at"] = GetString(item, "CreatedDate")
            },
            "accounts" => new Dictionary<string, object?>
            {
                ["id"] = GetString(item, "Id"),
                ["name"] = GetString(item, "Name"),
                ["industry"] = GetString(item, "Industry"),
                ["type"] = GetString(item, "Type"),
                ["annual_revenue"] = GetDecimal(item, "AnnualRevenue"),
                ["number_of_employees"] = GetInt(item, "NumberOfEmployees"),
                ["billing_city"] = GetString(item, "BillingCity"),
                ["billing_country"] = GetString(item, "BillingCountry"),
                ["created_at"] = GetString(item, "CreatedDate")
            },
            "cases" => new Dictionary<string, object?>
            {
                ["id"] = GetString(item, "Id"),
                ["subject"] = GetString(item, "Subject"),
                ["status"] = GetString(item, "Status"),
                ["priority"] = GetString(item, "Priority"),
                ["type"] = GetString(item, "Type"),
                ["account_id"] = GetString(item, "AccountId"),
                ["contact_id"] = GetString(item, "ContactId"),
                ["created_at"] = GetString(item, "CreatedDate"),
                ["closed_at"] = GetString(item, "ClosedDate")
            },
            _ => null
        };
    }

    // ─── QuickBooks API ─────────────────────────────────────────────────

    private async Task<List<Dictionary<string, object?>>> FetchQuickBooksTableAsync(
        Dictionary<string, string> credentials, string tableName)
    {
        var accessToken = credentials.GetValueOrDefault("accessToken", "") ?? "";
        var realmId = credentials.GetValueOrDefault("realmId", "") ?? "";

        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(realmId))
            throw new InvalidOperationException("QuickBooks credentials missing accessToken or realmId");

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var baseUrl = $"https://quickbooks.api.intuit.com/v3/company/{realmId}";

        if (tableName == "profit_and_loss")
            return await FetchQuickBooksProfitAndLossAsync(client, baseUrl);

        var qbEntity = tableName switch
        {
            "invoices" => "Invoice",
            "expenses" => "Purchase",
            "accounts" => "Account",
            _ => throw new InvalidOperationException($"Unknown QuickBooks table: {tableName}")
        };

        var allRecords = new List<Dictionary<string, object?>>();
        var startPosition = 1;
        var hasMore = true;

        while (hasMore)
        {
            var query = Uri.EscapeDataString($"SELECT * FROM {qbEntity} STARTPOSITION {startPosition} MAXRESULTS 100");
            var response = await client.GetAsync($"{baseUrl}/query?query={query}");
            var json = await response.Content.ReadAsStringAsync();
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("QueryResponse", out var qr) && qr.TryGetProperty(qbEntity, out var items) && items.ValueKind == JsonValueKind.Array)
            {
                var count = items.GetArrayLength();
                foreach (var item in items.EnumerateArray())
                {
                    var flat = FlattenQuickBooksRecord(item, tableName);
                    if (flat != null) allRecords.Add(flat);
                }
                hasMore = count == 100;
                startPosition += count;
            }
            else
            {
                hasMore = false;
            }

            if (hasMore) await Task.Delay(500);
        }

        return allRecords;
    }

    private async Task<List<Dictionary<string, object?>>> FetchQuickBooksProfitAndLossAsync(HttpClient client, string baseUrl)
    {
        var records = new List<Dictionary<string, object?>>();
        // Fetch last 12 months of P&L
        var now = DateTime.UtcNow;
        for (int i = 0; i < 12; i++)
        {
            var month = now.AddMonths(-i);
            var start = new DateTime(month.Year, month.Month, 1).ToString("yyyy-MM-dd");
            var end = new DateTime(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month)).ToString("yyyy-MM-dd");

            var response = await client.GetAsync($"{baseUrl}/reports/ProfitAndLoss?start_date={start}&end_date={end}");
            if (!response.IsSuccessStatusCode) continue;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var record = new Dictionary<string, object?>
            {
                ["period"] = month.ToString("yyyy-MM"),
                ["total_income"] = ExtractQbReportValue(doc, "Income"),
                ["total_cogs"] = ExtractQbReportValue(doc, "Cost of Goods Sold"),
                ["gross_profit"] = ExtractQbReportValue(doc, "Gross Profit"),
                ["total_expenses"] = ExtractQbReportValue(doc, "Expenses"),
                ["net_income"] = ExtractQbReportValue(doc, "Net Income")
            };
            records.Add(record);

            await Task.Delay(500);
        }
        return records;
    }

    private static decimal? ExtractQbReportValue(JsonDocument doc, string sectionName)
    {
        // QuickBooks P&L report has Rows > Row array, each with Summary > ColData
        try
        {
            if (!doc.RootElement.TryGetProperty("Rows", out var rows) ||
                !rows.TryGetProperty("Row", out var rowArr) ||
                rowArr.ValueKind != JsonValueKind.Array) return null;

            foreach (var row in rowArr.EnumerateArray())
            {
                // Check Header group name or Summary
                if (row.TryGetProperty("group", out _) || row.TryGetProperty("Header", out _))
                {
                    var headerName = "";
                    if (row.TryGetProperty("Header", out var header) && header.TryGetProperty("ColData", out var hCols) && hCols.ValueKind == JsonValueKind.Array)
                    {
                        var first = hCols.EnumerateArray().FirstOrDefault();
                        headerName = GetString(first, "value") ?? "";
                    }

                    if (headerName.Contains(sectionName, StringComparison.OrdinalIgnoreCase) &&
                        row.TryGetProperty("Summary", out var summary) &&
                        summary.TryGetProperty("ColData", out var sCols) && sCols.ValueKind == JsonValueKind.Array)
                    {
                        var colArr = sCols.EnumerateArray().ToArray();
                        if (colArr.Length >= 2 && decimal.TryParse(GetString(colArr[1], "value"), NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                            return val;
                    }
                }

                // Top-level summary rows (like Net Income)
                if (row.TryGetProperty("Summary", out var topSummary) &&
                    topSummary.TryGetProperty("ColData", out var topCols) && topCols.ValueKind == JsonValueKind.Array)
                {
                    var topColArr = topCols.EnumerateArray().ToArray();
                    if (topColArr.Length >= 1 && (GetString(topColArr[0], "value") ?? "").Contains(sectionName, StringComparison.OrdinalIgnoreCase) &&
                        topColArr.Length >= 2 && decimal.TryParse(GetString(topColArr[1], "value"), NumberStyles.Any, CultureInfo.InvariantCulture, out var topVal))
                        return topVal;
                }
            }
        }
        catch { /* best effort */ }
        return null;
    }

    private static Dictionary<string, object?>? FlattenQuickBooksRecord(JsonElement item, string tableName)
    {
        return tableName switch
        {
            "invoices" => new Dictionary<string, object?>
            {
                ["id"] = GetString(item, "Id"),
                ["doc_number"] = GetString(item, "DocNumber"),
                ["customer_id"] = GetNestedString(item, "CustomerRef", "value"),
                ["customer_name"] = GetNestedString(item, "CustomerRef", "name"),
                ["total_amount"] = GetDecimal(item, "TotalAmt"),
                ["balance"] = GetDecimal(item, "Balance"),
                ["due_date"] = GetString(item, "DueDate"),
                ["status"] = GetDecimal(item, "Balance") == 0 ? "Paid" : "Open",
                ["created_at"] = GetString(item, "MetaData") != null ? GetNestedString(item, "MetaData", "CreateTime") : GetString(item, "TxnDate")
            },
            "expenses" => new Dictionary<string, object?>
            {
                ["id"] = GetString(item, "Id"),
                ["payment_type"] = GetString(item, "PaymentType"),
                ["account_name"] = GetNestedString(item, "AccountRef", "name"),
                ["vendor_name"] = GetNestedString(item, "EntityRef", "name"),
                ["total_amount"] = GetDecimal(item, "TotalAmt"),
                ["category"] = GetNestedString(item, "AccountRef", "name"),
                ["created_at"] = GetString(item, "TxnDate")
            },
            "accounts" => new Dictionary<string, object?>
            {
                ["id"] = GetString(item, "Id"),
                ["name"] = GetString(item, "Name"),
                ["account_type"] = GetString(item, "AccountType"),
                ["current_balance"] = GetDecimal(item, "CurrentBalance"),
                ["currency"] = GetNestedString(item, "CurrencyRef", "value")
            },
            _ => null
        };
    }

    // ─── Google Analytics API (GA4) ─────────────────────────────────────

    private async Task<List<Dictionary<string, object?>>> FetchGoogleAnalyticsTableAsync(
        Dictionary<string, string> credentials, string tableName)
    {
        var saJson = credentials.GetValueOrDefault("serviceAccountJson", "") ?? "";
        var propertyId = credentials.GetValueOrDefault("propertyId", "") ?? "";

        if (string.IsNullOrEmpty(saJson) || string.IsNullOrEmpty(propertyId))
            throw new InvalidOperationException("Google Analytics credentials missing serviceAccountJson or propertyId");

        var accessToken = await GetGoogleAccessTokenAsync(saJson);
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var (dimensions, metrics) = GetGaDimensionsAndMetrics(tableName);
        var endDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var startDate = DateTime.UtcNow.AddDays(-90).ToString("yyyy-MM-dd");

        var requestBody = new
        {
            dateRanges = new[] { new { startDate, endDate } },
            dimensions = dimensions.Select(d => new { name = d }).ToArray(),
            metrics = metrics.Select(m => new { name = m }).ToArray(),
            limit = 10000
        };

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://analyticsdata.googleapis.com/v1beta/properties/{propertyId}:runReport")
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(json);
        var allRecords = new List<Dictionary<string, object?>>();

        if (doc.RootElement.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                var record = new Dictionary<string, object?>();
                if (row.TryGetProperty("dimensionValues", out var dimVals) && dimVals.ValueKind == JsonValueKind.Array)
                {
                    var dimArr = dimVals.EnumerateArray().ToArray();
                    for (int i = 0; i < dimensions.Length && i < dimArr.Length; i++)
                    {
                        record[SanitizeColumnName(dimensions[i])] = dimArr[i].TryGetProperty("value", out var v) ? v.GetString() : null;
                    }
                }
                if (row.TryGetProperty("metricValues", out var metVals) && metVals.ValueKind == JsonValueKind.Array)
                {
                    var metArr = metVals.EnumerateArray().ToArray();
                    for (int i = 0; i < metrics.Length && i < metArr.Length; i++)
                    {
                        var strVal = metArr[i].TryGetProperty("value", out var v) ? v.GetString() : null;
                        if (decimal.TryParse(strVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var numVal))
                            record[SanitizeColumnName(metrics[i])] = numVal;
                        else
                            record[SanitizeColumnName(metrics[i])] = strVal;
                    }
                }
                allRecords.Add(record);
            }
        }

        return allRecords;
    }

    private static (string[] dimensions, string[] metrics) GetGaDimensionsAndMetrics(string tableName) => tableName switch
    {
        "sessions" => (
            new[] { "date", "sessionSource", "sessionMedium", "sessionCampaignName" },
            new[] { "sessions", "totalUsers", "newUsers", "bounceRate", "averageSessionDuration", "screenPageViewsPerSession" }
        ),
        "pageviews" => (
            new[] { "date", "pagePath", "pageTitle" },
            new[] { "screenPageViews", "sessions", "averageSessionDuration", "entrances", "bounceRate" }
        ),
        "conversions" => (
            new[] { "date", "eventName", "sessionSource", "sessionMedium" },
            new[] { "eventCount", "eventValue", "conversions" }
        ),
        _ => (new[] { "date" }, new[] { "sessions" })
    };

    // ─── Google Sheets API ──────────────────────────────────────────────

    private async Task<List<Dictionary<string, object?>>> FetchGoogleSheetsTableAsync(
        Dictionary<string, string> credentials, string tableName)
    {
        var saJson = credentials.GetValueOrDefault("serviceAccountJson", "") ?? "";
        var spreadsheetId = credentials.GetValueOrDefault("spreadsheetId", "") ?? "";

        if (string.IsNullOrEmpty(saJson) || string.IsNullOrEmpty(spreadsheetId))
            throw new InvalidOperationException("Google Sheets credentials missing");

        var accessToken = await GetGoogleAccessTokenAsync(saJson);
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Resolve sanitized table name back to original sheet title
        var originalTitle = await ResolveGoogleSheetTitleAsync(client, spreadsheetId, tableName);

        var response = await client.GetAsync(
            $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values/{Uri.EscapeDataString(originalTitle)}");
        var json = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(json);
        var allRecords = new List<Dictionary<string, object?>>();

        if (doc.RootElement.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            var rows = values.EnumerateArray().ToArray();
            if (rows.Length < 2) return allRecords; // Need at least header + 1 data row

            // First row = headers
            var headers = rows[0].EnumerateArray()
                .Select((h, i) =>
                {
                    var name = h.ValueKind == JsonValueKind.String ? SanitizeColumnName(h.GetString() ?? $"col_{i}") : $"col_{i}";
                    return string.IsNullOrEmpty(name) ? $"col_{i}" : name;
                }).ToArray();

            for (int i = 1; i < rows.Length; i++)
            {
                var record = new Dictionary<string, object?>();
                var cells = rows[i].EnumerateArray().ToArray();
                for (int j = 0; j < headers.Length; j++)
                {
                    string? cellVal = j < cells.Length && cells[j].ValueKind == JsonValueKind.String
                        ? cells[j].GetString() : j < cells.Length ? cells[j].ToString() : null;

                    // Try to parse numbers
                    if (cellVal != null && decimal.TryParse(cellVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
                        record[headers[j]] = num;
                    else
                        record[headers[j]] = cellVal;
                }
                if (record.Values.Any(v => v != null)) // skip fully empty rows
                    allRecords.Add(record);
            }
        }

        return allRecords;
    }

    private static async Task<string> ResolveGoogleSheetTitleAsync(HttpClient client, string spreadsheetId, string sanitizedName)
    {
        var response = await client.GetAsync($"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}?fields=sheets.properties.title");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("sheets", out var sheets))
        {
            foreach (var s in sheets.EnumerateArray())
            {
                var title = s.GetProperty("properties").GetProperty("title").GetString() ?? "";
                if (SanitizeTableName(title) == sanitizedName)
                    return title;
            }
        }
        return sanitizedName; // fallback
    }
}
