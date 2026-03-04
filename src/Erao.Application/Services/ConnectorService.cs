using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AutoMapper;
using Erao.Core.DTOs.Connector;
using Erao.Core.Entities;
using Erao.Core.Enums;
using Erao.Core.Helpers;
using Erao.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Erao.Application.Services;

public interface IConnectorService
{
    Task<IEnumerable<AppConnectorDto>> GetConnectorsAsync(Guid userId);
    Task<AppConnectorDto?> GetByIdAsync(Guid id, Guid userId);
    Task<AppConnectorDto> CreateConnectorAsync(Guid userId, CreateConnectorRequest request);
    Task<AppConnectorDto> UpdateConnectorAsync(Guid userId, Guid connectorId, UpdateConnectorRequest request);
    Task DeleteConnectorAsync(Guid userId, Guid connectorId);
    Task<ConnectionTestResult> TestConnectionAsync(int connectorType, Dictionary<string, string> credentials);
    Task<AppConnectorDto> SyncConnectorAsync(Guid connectorId, Guid userId);
    List<ConnectorMetadataDto> GetAllMetadata();
}

public class ConnectionTestResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? AccountName { get; set; } // e.g. shop name, Stripe account name
}

public class ConnectorService : IConnectorService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IEncryptionService _encryptionService;
    private readonly IConnectorSyncService _connectorSyncService;
    private readonly IMinioService _minioService;
    private readonly IMapper _mapper;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ConnectorService> _logger;

    public ConnectorService(
        IUnitOfWork unitOfWork,
        IEncryptionService encryptionService,
        IConnectorSyncService connectorSyncService,
        IMinioService minioService,
        IMapper mapper,
        IHttpClientFactory httpClientFactory,
        ILogger<ConnectorService> logger)
    {
        _unitOfWork = unitOfWork;
        _encryptionService = encryptionService;
        _connectorSyncService = connectorSyncService;
        _minioService = minioService;
        _mapper = mapper;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IEnumerable<AppConnectorDto>> GetConnectorsAsync(Guid userId)
    {
        var connectors = await _unitOfWork.AppConnectors.GetByUserIdAsync(userId);
        return _mapper.Map<IEnumerable<AppConnectorDto>>(connectors);
    }

    public async Task<AppConnectorDto?> GetByIdAsync(Guid id, Guid userId)
    {
        var connector = await _unitOfWork.AppConnectors.GetByIdAsync(id);
        if (connector == null || connector.UserId != userId)
        {
            return null;
        }
        return _mapper.Map<AppConnectorDto>(connector);
    }

    public async Task<AppConnectorDto> CreateConnectorAsync(Guid userId, CreateConnectorRequest request)
    {
        if (!Enum.IsDefined(typeof(ConnectorType), request.ConnectorType))
        {
            throw new InvalidOperationException("Invalid connector type");
        }

        var connectorType = (ConnectorType)request.ConnectorType;

        // Test connection before saving
        var testResult = await TestConnectionAsync(request.ConnectorType, request.Credentials);
        if (!testResult.Success)
        {
            throw new InvalidOperationException(testResult.Message);
        }

        // Encrypt credentials as JSON
        var credentialsJson = JsonSerializer.Serialize(request.Credentials);
        var encryptedCredentials = _encryptionService.Encrypt(credentialsJson);

        var connector = new AppConnector
        {
            UserId = userId,
            Name = request.Name,
            ConnectorType = connectorType,
            EncryptedCredentials = encryptedCredentials,
            IsActive = true,
            SchemaContext = GetSchemaTemplate(connectorType)
        };

        await _unitOfWork.AppConnectors.AddAsync(connector);
        await _unitOfWork.SaveChangesAsync();

        return _mapper.Map<AppConnectorDto>(connector);
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(int connectorType, Dictionary<string, string> credentials)
    {
        if (!Enum.IsDefined(typeof(ConnectorType), connectorType))
        {
            return new ConnectionTestResult { Success = false, Message = "Invalid connector type" };
        }

        var type = (ConnectorType)connectorType;

        try
        {
            return type switch
            {
                ConnectorType.Shopify => await TestShopifyAsync(credentials),
                ConnectorType.Stripe => await TestStripeAsync(credentials),
                ConnectorType.WooCommerce => await TestWooCommerceAsync(credentials),
                ConnectorType.HubSpot => await TestHubSpotAsync(credentials),
                ConnectorType.Notion => await TestNotionAsync(credentials),
                ConnectorType.Airtable => await TestAirtableAsync(credentials),
                ConnectorType.Salesforce => await TestSalesforceAsync(credentials),
                ConnectorType.QuickBooks => await TestQuickBooksAsync(credentials),
                ConnectorType.GoogleAnalytics => await TestGoogleConnectorAsync(type, credentials),
                ConnectorType.GoogleSheets => await TestGoogleConnectorAsync(type, credentials),
                _ => ValidateCredentialFields(type, credentials)
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Connection test failed for {Type}", type);
            return new ConnectionTestResult
            {
                Success = false,
                Message = $"Could not reach {type} API. Check your credentials and try again."
            };
        }
        catch (TaskCanceledException)
        {
            return new ConnectionTestResult
            {
                Success = false,
                Message = $"Connection to {type} timed out. Please try again."
            };
        }
    }

    private async Task<ConnectionTestResult> TestShopifyAsync(Dictionary<string, string> credentials)
    {
        if (!credentials.TryGetValue("storeUrl", out var storeUrl) || string.IsNullOrWhiteSpace(storeUrl))
            return new ConnectionTestResult { Success = false, Message = "Store URL is required" };
        if (!credentials.TryGetValue("apiKey", out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
            return new ConnectionTestResult { Success = false, Message = "API Key is required" };

        // Normalize store URL
        storeUrl = storeUrl.Trim().TrimEnd('/');
        if (!storeUrl.Contains('.'))
            storeUrl += ".myshopify.com";
        if (!storeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            storeUrl = "https://" + storeUrl;

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Add("X-Shopify-Access-Token", apiKey.Trim());

        var response = await client.GetAsync($"{storeUrl}/admin/api/2024-01/shop.json");

        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var shopName = doc.RootElement.GetProperty("shop").GetProperty("name").GetString();
            return new ConnectionTestResult
            {
                Success = true,
                Message = $"Connected to {shopName}",
                AccountName = shopName
            };
        }

        return response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized =>
                new ConnectionTestResult { Success = false, Message = "Invalid API key. Check your Shopify Admin API access token." },
            System.Net.HttpStatusCode.NotFound =>
                new ConnectionTestResult { Success = false, Message = "Store not found. Check your store URL." },
            System.Net.HttpStatusCode.Forbidden =>
                new ConnectionTestResult { Success = false, Message = "Access denied. Your API key may not have the required permissions." },
            _ =>
                new ConnectionTestResult { Success = false, Message = $"Shopify returned {(int)response.StatusCode}. Check your credentials." }
        };
    }

    private async Task<ConnectionTestResult> TestStripeAsync(Dictionary<string, string> credentials)
    {
        if (!credentials.TryGetValue("secretKey", out var secretKey) || string.IsNullOrWhiteSpace(secretKey))
            return new ConnectionTestResult { Success = false, Message = "Secret Key is required" };

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secretKey.Trim());

        var response = await client.GetAsync("https://api.stripe.com/v1/balance");

        if (response.IsSuccessStatusCode)
        {
            // Get account name from /v1/account
            string? accountName = null;
            try
            {
                var acctResponse = await client.GetAsync("https://api.stripe.com/v1/account");
                if (acctResponse.IsSuccessStatusCode)
                {
                    var acctJson = await acctResponse.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(acctJson);
                    if (doc.RootElement.TryGetProperty("settings", out var settings) &&
                        settings.TryGetProperty("dashboard", out var dashboard) &&
                        dashboard.TryGetProperty("display_name", out var displayName))
                    {
                        accountName = displayName.GetString();
                    }
                }
            }
            catch { /* best effort */ }

            return new ConnectionTestResult
            {
                Success = true,
                Message = accountName != null ? $"Connected to {accountName}" : "Connected to Stripe",
                AccountName = accountName
            };
        }

        return response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized =>
                new ConnectionTestResult { Success = false, Message = "Invalid secret key. Check your Stripe API key." },
            System.Net.HttpStatusCode.Forbidden =>
                new ConnectionTestResult { Success = false, Message = "Access denied. Your key may be restricted." },
            _ =>
                new ConnectionTestResult { Success = false, Message = $"Stripe returned {(int)response.StatusCode}. Check your credentials." }
        };
    }

    private async Task<ConnectionTestResult> TestWooCommerceAsync(Dictionary<string, string> credentials)
    {
        var storeUrl = (credentials.GetValueOrDefault("storeUrl", "") ?? "").Trim().TrimEnd('/');
        var consumerKey = credentials.GetValueOrDefault("consumerKey", "") ?? "";
        var consumerSecret = credentials.GetValueOrDefault("consumerSecret", "") ?? "";

        if (string.IsNullOrWhiteSpace(storeUrl)) return new ConnectionTestResult { Success = false, Message = "Store URL is required" };
        if (string.IsNullOrWhiteSpace(consumerKey)) return new ConnectionTestResult { Success = false, Message = "Consumer Key is required" };
        if (string.IsNullOrWhiteSpace(consumerSecret)) return new ConnectionTestResult { Success = false, Message = "Consumer Secret is required" };

        if (!storeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) storeUrl = "https://" + storeUrl;

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        // Use HTTP Basic Auth instead of passing credentials in query string (more secure -- avoids
        // credentials appearing in server access logs, proxy logs, and browser history)
        var basicAuthValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{consumerKey}:{consumerSecret}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicAuthValue);

        var url = $"{storeUrl}/wp-json/wc/v3/system_status";

        var response = await client.GetAsync(url);
        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var storeName = doc.RootElement.TryGetProperty("environment", out var env) && env.TryGetProperty("site_url", out var su) ? su.GetString() : storeUrl;
            return new ConnectionTestResult { Success = true, Message = $"Connected to {storeName}", AccountName = storeName };
        }

        return new ConnectionTestResult { Success = false, Message = $"WooCommerce returned {(int)response.StatusCode}. Check your credentials." };
    }

    private async Task<ConnectionTestResult> TestHubSpotAsync(Dictionary<string, string> credentials)
    {
        var token = credentials.GetValueOrDefault("privateAppToken", "") ?? "";
        if (string.IsNullOrWhiteSpace(token)) return new ConnectionTestResult { Success = false, Message = "Private App Token is required" };

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("https://api.hubapi.com/crm/v3/objects/contacts?limit=1");
        if (response.IsSuccessStatusCode)
            return new ConnectionTestResult { Success = true, Message = "Connected to HubSpot", AccountName = "HubSpot CRM" };

        return response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            ? new ConnectionTestResult { Success = false, Message = "Invalid token. Check your HubSpot Private App Token." }
            : new ConnectionTestResult { Success = false, Message = $"HubSpot returned {(int)response.StatusCode}. Check your credentials." };
    }

    private async Task<ConnectionTestResult> TestNotionAsync(Dictionary<string, string> credentials)
    {
        var token = credentials.GetValueOrDefault("integrationToken", "") ?? "";
        if (string.IsNullOrWhiteSpace(token)) return new ConnectionTestResult { Success = false, Message = "Integration Token is required" };

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("Notion-Version", "2022-06-28");

        var response = await client.GetAsync("https://api.notion.com/v1/users/me");
        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            return new ConnectionTestResult { Success = true, Message = $"Connected as {name ?? "Notion Integration"}", AccountName = name };
        }

        return new ConnectionTestResult { Success = false, Message = $"Notion returned {(int)response.StatusCode}. Check your integration token." };
    }

    private async Task<ConnectionTestResult> TestAirtableAsync(Dictionary<string, string> credentials)
    {
        var token = credentials.GetValueOrDefault("personalAccessToken", "") ?? "";
        var baseId = credentials.GetValueOrDefault("baseId", "") ?? "";

        if (string.IsNullOrWhiteSpace(token)) return new ConnectionTestResult { Success = false, Message = "Personal Access Token is required" };
        if (string.IsNullOrWhiteSpace(baseId)) return new ConnectionTestResult { Success = false, Message = "Base ID is required" };

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync($"https://api.airtable.com/v0/meta/bases/{baseId}/tables");
        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var tableCount = doc.RootElement.TryGetProperty("tables", out var t) && t.ValueKind == JsonValueKind.Array ? t.GetArrayLength() : 0;
            return new ConnectionTestResult { Success = true, Message = $"Connected to base with {tableCount} tables", AccountName = baseId };
        }

        return new ConnectionTestResult { Success = false, Message = $"Airtable returned {(int)response.StatusCode}. Check your token and base ID." };
    }

    private async Task<ConnectionTestResult> TestSalesforceAsync(Dictionary<string, string> credentials)
    {
        var instanceUrl = (credentials.GetValueOrDefault("instanceUrl", "") ?? "").Trim().TrimEnd('/');
        var accessToken = credentials.GetValueOrDefault("accessToken", "") ?? "";

        if (string.IsNullOrWhiteSpace(instanceUrl)) return new ConnectionTestResult { Success = false, Message = "Instance URL is required" };
        if (string.IsNullOrWhiteSpace(accessToken)) return new ConnectionTestResult { Success = false, Message = "Access Token is required" };

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync($"{instanceUrl}/services/data/v59.0/sobjects");
        if (response.IsSuccessStatusCode)
        {
            // Try to get org name
            string? orgName = null;
            try
            {
                var orgResponse = await client.GetAsync($"{instanceUrl}/services/data/v59.0/query?q={Uri.EscapeDataString("SELECT Name FROM Organization LIMIT 1")}");
                if (orgResponse.IsSuccessStatusCode)
                {
                    var orgJson = await orgResponse.Content.ReadAsStringAsync();
                    using var orgDoc = JsonDocument.Parse(orgJson);
                    if (orgDoc.RootElement.TryGetProperty("records", out var recs) && recs.GetArrayLength() > 0)
                        orgName = recs[0].TryGetProperty("Name", out var n) ? n.GetString() : null;
                }
            }
            catch { /* best effort */ }

            return new ConnectionTestResult
            {
                Success = true,
                Message = orgName != null ? $"Connected to {orgName}" : "Connected to Salesforce",
                AccountName = orgName
            };
        }

        return response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            ? new ConnectionTestResult { Success = false, Message = "Invalid access token. Check your Salesforce credentials." }
            : new ConnectionTestResult { Success = false, Message = $"Salesforce returned {(int)response.StatusCode}. Check your instance URL and access token." };
    }

    private async Task<ConnectionTestResult> TestQuickBooksAsync(Dictionary<string, string> credentials)
    {
        var accessToken = credentials.GetValueOrDefault("accessToken", "") ?? "";
        var realmId = credentials.GetValueOrDefault("realmId", "") ?? "";

        if (string.IsNullOrWhiteSpace(accessToken)) return new ConnectionTestResult { Success = false, Message = "Access Token is required" };
        if (string.IsNullOrWhiteSpace(realmId)) return new ConnectionTestResult { Success = false, Message = "Realm ID is required" };

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await client.GetAsync($"https://quickbooks.api.intuit.com/v3/company/{realmId}/companyinfo/{realmId}");
        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var companyName = doc.RootElement.TryGetProperty("CompanyInfo", out var ci)
                ? (ci.TryGetProperty("CompanyName", out var cn) ? cn.GetString() : null) : null;
            return new ConnectionTestResult
            {
                Success = true,
                Message = companyName != null ? $"Connected to {companyName}" : "Connected to QuickBooks",
                AccountName = companyName
            };
        }

        return new ConnectionTestResult { Success = false, Message = $"QuickBooks returned {(int)response.StatusCode}. Check your access token and realm ID." };
    }

    private async Task<ConnectionTestResult> TestGoogleConnectorAsync(ConnectorType type, Dictionary<string, string> credentials)
    {
        var (success, message, accountName) = await _connectorSyncService.TestGoogleConnectionAsync(type, credentials);
        return new ConnectionTestResult { Success = success, Message = message, AccountName = accountName };
    }

    private static ConnectionTestResult ValidateCredentialFields(ConnectorType type, Dictionary<string, string> credentials)
    {
        // For coming-soon connectors, just ensure credentials aren't empty
        if (credentials.Count == 0)
            return new ConnectionTestResult { Success = false, Message = "Credentials are required" };

        foreach (var (key, value) in credentials)
        {
            if (string.IsNullOrWhiteSpace(value))
                return new ConnectionTestResult { Success = false, Message = $"{key} is required" };
        }

        return new ConnectionTestResult
        {
            Success = true,
            Message = $"Credentials saved for {type} (connection test will run when data sync is available)"
        };
    }

    public async Task<AppConnectorDto> UpdateConnectorAsync(Guid userId, Guid connectorId, UpdateConnectorRequest request)
    {
        var connector = await _unitOfWork.AppConnectors.GetByIdAsync(connectorId);
        if (connector == null || connector.UserId != userId)
        {
            throw new InvalidOperationException("Connector not found");
        }

        if (!string.IsNullOrEmpty(request.Name))
        {
            connector.Name = request.Name;
        }

        if (request.Credentials != null)
        {
            var credentialsJson = JsonSerializer.Serialize(request.Credentials);
            connector.EncryptedCredentials = _encryptionService.Encrypt(credentialsJson);
            // Invalidate schema cache on credential change
            connector.SchemaContext = GetSchemaTemplate(connector.ConnectorType);
        }

        await _unitOfWork.AppConnectors.UpdateAsync(connector);
        await _unitOfWork.SaveChangesAsync();

        return _mapper.Map<AppConnectorDto>(connector);
    }

    public async Task<AppConnectorDto> SyncConnectorAsync(Guid connectorId, Guid userId)
    {
        var connector = await _unitOfWork.AppConnectors.GetByIdAsync(connectorId);
        if (connector == null || connector.UserId != userId)
            throw new InvalidOperationException("Connector not found");

        await _connectorSyncService.SyncAsync(connectorId, userId);

        // Reload to get updated sync fields
        connector = await _unitOfWork.AppConnectors.GetByIdAsync(connectorId);
        return _mapper.Map<AppConnectorDto>(connector!);
    }

    public async Task DeleteConnectorAsync(Guid userId, Guid connectorId)
    {
        var connector = await _unitOfWork.AppConnectors.GetByIdAsync(connectorId);
        if (connector == null || connector.UserId != userId)
        {
            throw new InvalidOperationException("Connector not found");
        }

        // Clean up R2 Parquet files
        if (!string.IsNullOrEmpty(connector.ParquetStoragePaths))
        {
            try
            {
                var paths = JsonSerializer.Deserialize<Dictionary<string, string>>(connector.ParquetStoragePaths);
                if (paths != null)
                {
                    foreach (var (_, objectKey) in paths)
                    {
                        try { await _minioService.DeleteFileAsync(objectKey); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete Parquet file: {Key}", objectKey); }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse ParquetStoragePaths for cleanup");
            }
        }

        await _unitOfWork.AppConnectors.DeleteAsync(connector);
        await _unitOfWork.SaveChangesAsync();
    }

    public List<ConnectorMetadataDto> GetAllMetadata()
    {
        return ConnectorMetadataRegistry.GetAll();
    }

    private static string GetSchemaTemplate(ConnectorType type) => type switch
    {
        ConnectorType.Shopify => ShopifySchema,
        ConnectorType.Stripe => StripeSchema,
        ConnectorType.WooCommerce => WooCommerceSchema,
        ConnectorType.QuickBooks => QuickBooksSchema,
        ConnectorType.HubSpot => HubSpotSchema,
        ConnectorType.Salesforce => SalesforceSchema,
        ConnectorType.GoogleAnalytics => GoogleAnalyticsSchema,
        ConnectorType.Notion => NotionSchema,
        ConnectorType.Airtable => AirtableSchema,
        ConnectorType.GoogleSheets => GoogleSheetsSchema,
        _ => ""
    };

    private const string ShopifySchema = @"-- Shopify Store Data
-- orders (Sales orders placed in your store)
CREATE TABLE ""orders"" (
    ""id"" BIGINT NOT NULL,              -- Unique order ID
    ""order_number"" INTEGER,            -- Customer-facing #1001, #1002...
    ""email"" TEXT,                      -- Customer email
    ""total_price"" DECIMAL NOT NULL,    -- Total including tax & shipping
    ""subtotal_price"" DECIMAL,          -- Subtotal before tax & shipping
    ""total_tax"" DECIMAL,              -- Total tax amount
    ""total_discounts"" DECIMAL,        -- Total discounts applied
    ""financial_status"" TEXT,           -- pending | paid | refunded | voided
    ""fulfillment_status"" TEXT,         -- unfulfilled | partial | fulfilled
    ""currency"" TEXT,                   -- Currency code (USD, EUR, etc.)
    ""created_at"" TIMESTAMP NOT NULL,   -- When order was placed
    ""updated_at"" TIMESTAMP,           -- Last update time
    ""cancelled_at"" TIMESTAMP,         -- When cancelled (NULL if not)
    ""customer_id"" BIGINT              -- FK to customers table
);

-- products (Items available in your store)
CREATE TABLE ""products"" (
    ""id"" BIGINT NOT NULL,
    ""title"" TEXT NOT NULL,             -- Product name
    ""vendor"" TEXT,                     -- Brand / manufacturer
    ""product_type"" TEXT,              -- Category (e.g., 'Shirts', 'Electronics')
    ""status"" TEXT,                     -- active | draft | archived
    ""created_at"" TIMESTAMP NOT NULL,
    ""updated_at"" TIMESTAMP,
    ""published_at"" TIMESTAMP          -- When made visible to customers
);

-- customers (People who have accounts or placed orders)
CREATE TABLE ""customers"" (
    ""id"" BIGINT NOT NULL,
    ""email"" TEXT,
    ""first_name"" TEXT,
    ""last_name"" TEXT,
    ""orders_count"" INTEGER,           -- Total orders placed
    ""total_spent"" DECIMAL,            -- Lifetime spend
    ""created_at"" TIMESTAMP NOT NULL,
    ""updated_at"" TIMESTAMP
);

-- inventory (Stock levels per variant per location)
CREATE TABLE ""inventory"" (
    ""inventory_item_id"" BIGINT NOT NULL,
    ""variant_id"" BIGINT,
    ""product_id"" BIGINT,
    ""sku"" TEXT,
    ""available"" INTEGER,              -- Units available to sell
    ""location_id"" BIGINT
);";

    private const string StripeSchema = @"-- Stripe Payments Data
-- payments (Successful and attempted charges)
CREATE TABLE ""payments"" (
    ""id"" TEXT NOT NULL,                -- Charge ID (ch_xxx)
    ""amount"" INTEGER NOT NULL,         -- Amount in cents
    ""currency"" TEXT NOT NULL,          -- 3-letter currency code
    ""status"" TEXT NOT NULL,            -- succeeded | pending | failed
    ""description"" TEXT,
    ""customer_id"" TEXT,               -- FK to customers (cus_xxx)
    ""payment_method"" TEXT,            -- card | bank_transfer | etc.
    ""created_at"" TIMESTAMP NOT NULL,
    ""receipt_email"" TEXT
);

-- subscriptions (Recurring billing)
CREATE TABLE ""subscriptions"" (
    ""id"" TEXT NOT NULL,                -- Subscription ID (sub_xxx)
    ""customer_id"" TEXT NOT NULL,
    ""status"" TEXT NOT NULL,            -- active | past_due | canceled | trialing
    ""plan_id"" TEXT,                   -- Price/plan identifier
    ""plan_amount"" INTEGER,            -- Amount in cents per interval
    ""plan_interval"" TEXT,             -- month | year | week | day
    ""current_period_start"" TIMESTAMP,
    ""current_period_end"" TIMESTAMP,
    ""cancel_at_period_end"" BOOLEAN,
    ""created_at"" TIMESTAMP NOT NULL
);

-- customers (Stripe customer records)
CREATE TABLE ""customers"" (
    ""id"" TEXT NOT NULL,                -- Customer ID (cus_xxx)
    ""email"" TEXT,
    ""name"" TEXT,
    ""created_at"" TIMESTAMP NOT NULL,
    ""balance"" INTEGER,                -- Account balance in cents
    ""currency"" TEXT,
    ""delinquent"" BOOLEAN             -- Has unpaid invoices
);

-- invoices (Billing documents)
CREATE TABLE ""invoices"" (
    ""id"" TEXT NOT NULL,                -- Invoice ID (in_xxx)
    ""customer_id"" TEXT NOT NULL,
    ""subscription_id"" TEXT,
    ""amount_due"" INTEGER NOT NULL,    -- Amount due in cents
    ""amount_paid"" INTEGER,
    ""status"" TEXT,                     -- draft | open | paid | void | uncollectible
    ""currency"" TEXT,
    ""created_at"" TIMESTAMP NOT NULL,
    ""due_date"" TIMESTAMP,
    ""paid_at"" TIMESTAMP
);";

    private const string WooCommerceSchema = @"-- WooCommerce Store Data
-- orders (Customer orders)
CREATE TABLE ""orders"" (
    ""id"" BIGINT NOT NULL,
    ""order_number"" TEXT,
    ""status"" TEXT,                     -- pending | processing | on-hold | completed | cancelled | refunded | failed
    ""total"" DECIMAL NOT NULL,
    ""subtotal"" DECIMAL,
    ""total_tax"" DECIMAL,
    ""discount_total"" DECIMAL,
    ""shipping_total"" DECIMAL,
    ""payment_method"" TEXT,
    ""customer_id"" BIGINT,
    ""billing_email"" TEXT,
    ""currency"" TEXT,
    ""date_created"" TIMESTAMP NOT NULL,
    ""date_modified"" TIMESTAMP
);

-- products (Store products)
CREATE TABLE ""products"" (
    ""id"" BIGINT NOT NULL,
    ""name"" TEXT NOT NULL,
    ""type"" TEXT,                       -- simple | variable | grouped | external
    ""status"" TEXT,                     -- publish | draft | pending | private
    ""sku"" TEXT,
    ""price"" DECIMAL,
    ""regular_price"" DECIMAL,
    ""sale_price"" DECIMAL,
    ""stock_quantity"" INTEGER,
    ""stock_status"" TEXT,              -- instock | outofstock | onbackorder
    ""categories"" TEXT,
    ""date_created"" TIMESTAMP NOT NULL
);

-- customers (Registered customers)
CREATE TABLE ""customers"" (
    ""id"" BIGINT NOT NULL,
    ""email"" TEXT,
    ""first_name"" TEXT,
    ""last_name"" TEXT,
    ""orders_count"" INTEGER,
    ""total_spent"" DECIMAL,
    ""date_created"" TIMESTAMP NOT NULL
);

-- coupons (Discount coupons)
CREATE TABLE ""coupons"" (
    ""id"" BIGINT NOT NULL,
    ""code"" TEXT NOT NULL,
    ""discount_type"" TEXT,             -- percent | fixed_cart | fixed_product
    ""amount"" DECIMAL,
    ""usage_count"" INTEGER,
    ""usage_limit"" INTEGER,
    ""date_created"" TIMESTAMP NOT NULL,
    ""date_expires"" TIMESTAMP
);";

    private const string QuickBooksSchema = @"-- QuickBooks Accounting Data
-- invoices (Sales invoices sent to customers)
CREATE TABLE ""invoices"" (
    ""id"" TEXT NOT NULL,
    ""doc_number"" TEXT,                -- Invoice number
    ""customer_id"" TEXT,
    ""customer_name"" TEXT,
    ""total_amount"" DECIMAL NOT NULL,
    ""balance"" DECIMAL,               -- Amount still owed
    ""due_date"" DATE,
    ""status"" TEXT,                    -- Paid | Open | Overdue | Voided
    ""created_at"" TIMESTAMP NOT NULL
);

-- expenses (Bills and expenses)
CREATE TABLE ""expenses"" (
    ""id"" TEXT NOT NULL,
    ""payment_type"" TEXT,             -- Cash | Check | CreditCard
    ""account_name"" TEXT,
    ""vendor_name"" TEXT,
    ""total_amount"" DECIMAL NOT NULL,
    ""category"" TEXT,
    ""created_at"" TIMESTAMP NOT NULL
);

-- accounts (Chart of accounts)
CREATE TABLE ""accounts"" (
    ""id"" TEXT NOT NULL,
    ""name"" TEXT NOT NULL,
    ""account_type"" TEXT,             -- Bank | CreditCard | Income | Expense | etc.
    ""current_balance"" DECIMAL,
    ""currency"" TEXT
);

-- profit_and_loss (P&L summary)
CREATE TABLE ""profit_and_loss"" (
    ""period"" TEXT NOT NULL,           -- e.g., '2025-01'
    ""total_income"" DECIMAL,
    ""total_cogs"" DECIMAL,            -- Cost of goods sold
    ""gross_profit"" DECIMAL,
    ""total_expenses"" DECIMAL,
    ""net_income"" DECIMAL
);";

    private const string HubSpotSchema = @"-- HubSpot CRM Data
-- contacts (People in your CRM)
CREATE TABLE ""contacts"" (
    ""id"" BIGINT NOT NULL,
    ""email"" TEXT,
    ""first_name"" TEXT,
    ""last_name"" TEXT,
    ""phone"" TEXT,
    ""company"" TEXT,
    ""lifecycle_stage"" TEXT,           -- subscriber | lead | mql | sql | opportunity | customer
    ""lead_status"" TEXT,              -- New | Open | In Progress | Qualified | Unqualified
    ""source"" TEXT,                    -- Organic Search | Paid | Referral | Direct | etc.
    ""created_at"" TIMESTAMP NOT NULL
);

-- deals (Sales opportunities)
CREATE TABLE ""deals"" (
    ""id"" BIGINT NOT NULL,
    ""deal_name"" TEXT NOT NULL,
    ""amount"" DECIMAL,
    ""stage"" TEXT,                     -- Pipeline stage name
    ""pipeline"" TEXT,                 -- Pipeline name
    ""close_date"" DATE,
    ""owner_id"" BIGINT,
    ""contact_id"" BIGINT,
    ""company_id"" BIGINT,
    ""created_at"" TIMESTAMP NOT NULL
);

-- companies (Organizations)
CREATE TABLE ""companies"" (
    ""id"" BIGINT NOT NULL,
    ""name"" TEXT NOT NULL,
    ""domain"" TEXT,
    ""industry"" TEXT,
    ""annual_revenue"" DECIMAL,
    ""number_of_employees"" INTEGER,
    ""city"" TEXT,
    ""country"" TEXT,
    ""created_at"" TIMESTAMP NOT NULL
);

-- tickets (Support tickets)
CREATE TABLE ""tickets"" (
    ""id"" BIGINT NOT NULL,
    ""subject"" TEXT NOT NULL,
    ""status"" TEXT,                    -- New | Waiting | In Progress | Closed
    ""priority"" TEXT,                 -- Low | Medium | High | Urgent
    ""category"" TEXT,
    ""contact_id"" BIGINT,
    ""created_at"" TIMESTAMP NOT NULL,
    ""closed_at"" TIMESTAMP
);";

    private const string SalesforceSchema = @"-- Salesforce CRM Data
-- leads (Potential customers)
CREATE TABLE ""leads"" (
    ""id"" TEXT NOT NULL,
    ""name"" TEXT,
    ""email"" TEXT,
    ""company"" TEXT,
    ""title"" TEXT,
    ""status"" TEXT,                    -- Open | Working | Closed-Converted | Closed-Not Converted
    ""source"" TEXT,                    -- Web | Phone | Partner | etc.
    ""industry"" TEXT,
    ""annual_revenue"" DECIMAL,
    ""created_at"" TIMESTAMP NOT NULL,
    ""converted_at"" TIMESTAMP
);

-- opportunities (Sales deals)
CREATE TABLE ""opportunities"" (
    ""id"" TEXT NOT NULL,
    ""name"" TEXT NOT NULL,
    ""amount"" DECIMAL,
    ""stage"" TEXT,                     -- Prospecting | Qualification | Proposal | Negotiation | Closed Won | Closed Lost
    ""probability"" INTEGER,           -- Win probability percentage
    ""close_date"" DATE,
    ""account_id"" TEXT,
    ""owner_id"" TEXT,
    ""type"" TEXT,                      -- New Business | Existing Business
    ""created_at"" TIMESTAMP NOT NULL
);

-- accounts (Companies / organizations)
CREATE TABLE ""accounts"" (
    ""id"" TEXT NOT NULL,
    ""name"" TEXT NOT NULL,
    ""industry"" TEXT,
    ""type"" TEXT,                      -- Prospect | Customer | Partner | etc.
    ""annual_revenue"" DECIMAL,
    ""number_of_employees"" INTEGER,
    ""billing_city"" TEXT,
    ""billing_country"" TEXT,
    ""created_at"" TIMESTAMP NOT NULL
);

-- cases (Customer support cases)
CREATE TABLE ""cases"" (
    ""id"" TEXT NOT NULL,
    ""subject"" TEXT,
    ""status"" TEXT,                    -- New | Working | Escalated | Closed
    ""priority"" TEXT,                 -- Low | Medium | High | Critical
    ""type"" TEXT,                      -- Question | Problem | Feature Request
    ""account_id"" TEXT,
    ""contact_id"" TEXT,
    ""created_at"" TIMESTAMP NOT NULL,
    ""closed_at"" TIMESTAMP
);";

    private const string GoogleAnalyticsSchema = @"-- Google Analytics Data
-- sessions (Website traffic sessions)
CREATE TABLE ""sessions"" (
    ""date"" DATE NOT NULL,
    ""source"" TEXT,                    -- google | facebook | direct | etc.
    ""medium"" TEXT,                    -- organic | cpc | referral | none | email
    ""campaign"" TEXT,                 -- Campaign name
    ""sessions"" INTEGER NOT NULL,
    ""users"" INTEGER,
    ""new_users"" INTEGER,
    ""bounce_rate"" DECIMAL,           -- Percentage (0-100)
    ""avg_session_duration"" DECIMAL,  -- In seconds
    ""pages_per_session"" DECIMAL
);

-- pageviews (Page-level metrics)
CREATE TABLE ""pageviews"" (
    ""date"" DATE NOT NULL,
    ""page_path"" TEXT NOT NULL,
    ""page_title"" TEXT,
    ""pageviews"" INTEGER NOT NULL,
    ""unique_pageviews"" INTEGER,
    ""avg_time_on_page"" DECIMAL,     -- In seconds
    ""entrances"" INTEGER,
    ""exits"" INTEGER,
    ""exit_rate"" DECIMAL
);

-- conversions (Goal completions and events)
CREATE TABLE ""conversions"" (
    ""date"" DATE NOT NULL,
    ""goal_name"" TEXT,
    ""completions"" INTEGER NOT NULL,
    ""value"" DECIMAL,                 -- Goal value
    ""conversion_rate"" DECIMAL,       -- Percentage
    ""source"" TEXT,
    ""medium"" TEXT
);";

    private const string NotionSchema = @"-- Notion Workspace Data
-- databases (Notion databases / collections)
CREATE TABLE ""databases"" (
    ""id"" TEXT NOT NULL,
    ""title"" TEXT NOT NULL,
    ""created_at"" TIMESTAMP NOT NULL,
    ""last_edited_at"" TIMESTAMP,
    ""created_by"" TEXT,
    ""property_count"" INTEGER         -- Number of properties/columns
);

-- pages (Pages within databases or standalone)
CREATE TABLE ""pages"" (
    ""id"" TEXT NOT NULL,
    ""title"" TEXT,
    ""database_id"" TEXT,              -- Parent database (NULL for standalone pages)
    ""parent_page_id"" TEXT,           -- Parent page for nested pages
    ""created_at"" TIMESTAMP NOT NULL,
    ""last_edited_at"" TIMESTAMP,
    ""created_by"" TEXT,
    ""archived"" BOOLEAN
);";

    private const string AirtableSchema = @"-- Airtable Data
-- bases (Airtable bases / workspaces)
CREATE TABLE ""bases"" (
    ""id"" TEXT NOT NULL,
    ""name"" TEXT NOT NULL,
    ""table_count"" INTEGER
);

-- tables (Tables within bases)
CREATE TABLE ""tables"" (
    ""id"" TEXT NOT NULL,
    ""base_id"" TEXT NOT NULL,
    ""name"" TEXT NOT NULL,
    ""field_count"" INTEGER,
    ""record_count"" INTEGER
);

-- records (Rows in tables — schema varies per table)
CREATE TABLE ""records"" (
    ""id"" TEXT NOT NULL,
    ""table_id"" TEXT NOT NULL,
    ""created_at"" TIMESTAMP,
    ""fields"" JSONB                   -- Dynamic fields as key-value pairs
);";

    private const string GoogleSheetsSchema = @"-- Google Sheets Data
-- sheets (Individual sheets/tabs)
CREATE TABLE ""sheets"" (
    ""spreadsheet_id"" TEXT NOT NULL,
    ""sheet_id"" INTEGER NOT NULL,
    ""title"" TEXT NOT NULL,
    ""row_count"" INTEGER,
    ""column_count"" INTEGER
);

-- rows (Data rows — columns are dynamic based on headers)
CREATE TABLE ""rows"" (
    ""spreadsheet_id"" TEXT NOT NULL,
    ""sheet_title"" TEXT NOT NULL,
    ""row_number"" INTEGER NOT NULL,
    ""data"" JSONB                     -- Row data as key-value pairs from column headers
);";
}
