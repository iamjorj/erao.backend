using System.Net.Http.Json;
using System.Text.Json;
using Erao.Core.Enums;
using Erao.Core.Helpers;
using Erao.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Erao.Infrastructure.Services;

public class DodoPaymentsService : IDodoPaymentsService
{
    private readonly HttpClient _httpClient;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<DodoPaymentsService> _logger;
    private readonly string _webhookKey;
    private readonly Dictionary<SubscriptionTier, string> _productIds;

    public DodoPaymentsService(
        HttpClient httpClient,
        IConfiguration configuration,
        IUnitOfWork unitOfWork,
        ILogger<DodoPaymentsService> logger)
    {
        _httpClient = httpClient;
        _unitOfWork = unitOfWork;
        _logger = logger;

        var apiKey = (configuration["DodoPayments:ApiKey"] ?? "").Trim();
        var isTestMode = configuration.GetValue<bool>("DodoPayments:TestMode", true);
        _webhookKey = (configuration["DodoPayments:WebhookKey"] ?? "").Trim();

        var baseUrl = isTestMode
            ? "https://test.dodopayments.com"
            : "https://live.dodopayments.com";

        _httpClient.BaseAddress = new Uri(baseUrl);
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        // Map subscription tiers to Dodo product IDs (create products in Dodo dashboard first)
        _productIds = new Dictionary<SubscriptionTier, string>
        {
            { SubscriptionTier.Professional, configuration["DodoPayments:Products:Professional"] ?? "" },
            { SubscriptionTier.Enterprise, configuration["DodoPayments:Products:Enterprise"] ?? "" }
        };

        _logger.LogInformation("DodoPayments initialized: BaseUrl={BaseUrl}, TestMode={TestMode}, ApiKeySet={ApiKeySet}",
            baseUrl, isTestMode, !string.IsNullOrEmpty(apiKey));
    }

    public async Task<string> CreateCheckoutSessionAsync(
        Guid userId, string email, string name, SubscriptionTier targetTier, string returnUrl)
    {
        if (targetTier == SubscriptionTier.Starter)
            throw new InvalidOperationException("Cannot checkout for the free tier");

        // Check if API key is configured
        if (_httpClient.DefaultRequestHeaders.Authorization?.Parameter is null or "")
            throw new InvalidOperationException("Payment system is not configured. Please contact support.");

        if (!_productIds.TryGetValue(targetTier, out var productId) || string.IsNullOrEmpty(productId))
            throw new InvalidOperationException($"Payment product not configured for {targetTier} plan. Please contact support.");

        var requestBody = new
        {
            product_cart = new[]
            {
                new { product_id = productId, quantity = 1 }
            },
            customer = new
            {
                email,
                name
            },
            return_url = returnUrl,
            metadata = new Dictionary<string, string>
            {
                { "user_id", userId.ToString() },
                { "target_tier", ((int)targetTier).ToString() }
            }
        };

        // Log API key info for debugging (masked)
        var authHeader = _httpClient.DefaultRequestHeaders.Authorization;
        var apiKeyPreview = authHeader?.Parameter?.Length > 8
            ? $"{authHeader.Parameter[..4]}...{authHeader.Parameter[^4..]}"
            : "(not set or too short)";

        _logger.LogInformation(
            "Creating Dodo checkout: User={UserId}, Tier={Tier}, Product={ProductId}, BaseUrl={BaseUrl}, ApiKey={ApiKeyPreview}",
            userId, targetTier, productId, _httpClient.BaseAddress, apiKeyPreview);

        var response = await _httpClient.PostAsJsonAsync("/checkouts", requestBody);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError(
                "Dodo Payments API error: Status={StatusCode}, Response={Content}, ApiKey={ApiKeyPreview}, BaseUrl={BaseUrl}",
                response.StatusCode, errorContent, apiKeyPreview, _httpClient.BaseAddress);

            // Parse error message if possible
            var errorMessage = response.StatusCode switch
            {
                System.Net.HttpStatusCode.Forbidden => "Invalid API key. Please check DodoPayments__ApiKey environment variable.",
                System.Net.HttpStatusCode.Unauthorized => "API key not authorized. Verify the key in Dodo dashboard.",
                System.Net.HttpStatusCode.NotFound => "Invalid endpoint or product ID not found.",
                System.Net.HttpStatusCode.BadRequest => $"Invalid request: {errorContent}",
                _ => $"Payment provider error: {response.StatusCode}"
            };

            throw new InvalidOperationException(errorMessage);
        }

        var json = await response.Content.ReadAsStringAsync();
        _logger.LogDebug("Dodo checkout response: {Response}", json);

        using var doc = JsonDocument.Parse(json);

        // Try to get checkout_url from the response
        if (!doc.RootElement.TryGetProperty("checkout_url", out var urlElement))
        {
            // Try alternate property name
            if (!doc.RootElement.TryGetProperty("url", out urlElement))
            {
                _logger.LogError("Unexpected Dodo response format: {Response}", json);
                throw new InvalidOperationException("Invalid response from payment provider");
            }
        }

        var checkoutUrl = urlElement.GetString()
            ?? throw new InvalidOperationException("No checkout URL returned from Dodo Payments");

        _logger.LogInformation(
            "Created checkout session for user {UserId}, tier {Tier}",
            userId, targetTier);

        return checkoutUrl;
    }

    public async Task HandleWebhookAsync(string payload, IDictionary<string, string> headers)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var eventType = root.GetProperty("type").GetString() ?? "";

        _logger.LogInformation("Received Dodo webhook: {EventType}", eventType);

        switch (eventType)
        {
            case "subscription.active":
                await HandleSubscriptionActive(root);
                break;
            case "subscription.renewed":
                await HandleSubscriptionRenewed(root);
                break;
            case "subscription.on_hold":
            case "subscription.failed":
                await HandleSubscriptionFailed(root);
                break;
            case "payment.succeeded":
                await HandlePaymentSucceeded(root);
                break;
            default:
                _logger.LogInformation("Unhandled webhook event type: {EventType}", eventType);
                break;
        }
    }

    private async Task HandleSubscriptionActive(JsonElement root)
    {
        var data = root.GetProperty("data");
        var subscriptionId = data.GetProperty("subscription_id").GetString() ?? "";
        var metadata = GetMetadata(data);

        if (!metadata.TryGetValue("user_id", out var userIdStr) || !Guid.TryParse(userIdStr, out var userId))
        {
            _logger.LogWarning("Subscription active webhook missing user_id metadata");
            return;
        }

        if (!metadata.TryGetValue("target_tier", out var tierStr) || !int.TryParse(tierStr, out var tierInt))
        {
            _logger.LogWarning("Subscription active webhook missing target_tier metadata");
            return;
        }

        var targetTier = (SubscriptionTier)tierInt;
        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        if (user == null)
        {
            _logger.LogWarning("User {UserId} not found for subscription activation", userId);
            return;
        }

        user.SubscriptionTier = targetTier;
        user.QueryLimitPerMonth = SubscriptionLimits.GetQueryLimit(targetTier);
        user.SubscriptionStartDate = DateTime.UtcNow;
        user.DodoSubscriptionId = subscriptionId;

        if (data.TryGetProperty("customer", out var customer) &&
            customer.TryGetProperty("customer_id", out var customerId))
        {
            user.DodoCustomerId = customerId.GetString();
        }

        await _unitOfWork.Users.UpdateAsync(user);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation(
            "Subscription activated: user={UserId}, tier={Tier}, subscription={SubId}",
            userId, targetTier, subscriptionId);
    }

    private async Task HandleSubscriptionRenewed(JsonElement root)
    {
        var data = root.GetProperty("data");
        var subscriptionId = data.GetProperty("subscription_id").GetString() ?? "";

        var user = await _unitOfWork.Users.FirstOrDefaultAsync(u => u.DodoSubscriptionId == subscriptionId);
        if (user == null)
        {
            _logger.LogWarning("No user found for subscription renewal: {SubId}", subscriptionId);
            return;
        }

        user.QueriesUsedThisMonth = 0;
        user.BillingCycleReset = DateTime.UtcNow.AddMonths(1);

        await _unitOfWork.Users.UpdateAsync(user);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("Subscription renewed for user {UserId}", user.Id);
    }

    private async Task HandleSubscriptionFailed(JsonElement root)
    {
        var data = root.GetProperty("data");
        var subscriptionId = data.GetProperty("subscription_id").GetString() ?? "";

        var user = await _unitOfWork.Users.FirstOrDefaultAsync(u => u.DodoSubscriptionId == subscriptionId);
        if (user == null)
        {
            _logger.LogWarning("No user found for failed subscription: {SubId}", subscriptionId);
            return;
        }

        user.SubscriptionTier = SubscriptionTier.Starter;
        user.QueryLimitPerMonth = SubscriptionLimits.GetQueryLimit(SubscriptionTier.Starter);
        user.DodoSubscriptionId = null;

        await _unitOfWork.Users.UpdateAsync(user);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogWarning("Subscription failed, downgraded user {UserId} to Starter", user.Id);
    }

    private async Task HandlePaymentSucceeded(JsonElement root)
    {
        var data = root.GetProperty("data");
        var metadata = GetMetadata(data);

        if (!metadata.TryGetValue("user_id", out var userIdStr) || !Guid.TryParse(userIdStr, out var userId))
            return;

        if (!metadata.TryGetValue("target_tier", out var tierStr) || !int.TryParse(tierStr, out var tierInt))
            return;

        var targetTier = (SubscriptionTier)tierInt;
        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        if (user == null) return;

        user.SubscriptionTier = targetTier;
        user.QueryLimitPerMonth = SubscriptionLimits.GetQueryLimit(targetTier);
        user.SubscriptionStartDate = DateTime.UtcNow;

        if (data.TryGetProperty("customer", out var customer) &&
            customer.TryGetProperty("customer_id", out var customerId))
        {
            user.DodoCustomerId = customerId.GetString();
        }

        await _unitOfWork.Users.UpdateAsync(user);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("Payment succeeded: user={UserId}, tier={Tier}", userId, targetTier);
    }

    private static Dictionary<string, string> GetMetadata(JsonElement data)
    {
        var metadata = new Dictionary<string, string>();
        if (data.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in meta.EnumerateObject())
            {
                metadata[prop.Name] = prop.Value.GetString() ?? "";
            }
        }
        return metadata;
    }

    public async Task<(bool IsConfigured, bool IsConnected, string Message)> TestConnectionAsync()
    {
        // Check if API key is configured
        var authHeader = _httpClient.DefaultRequestHeaders.Authorization;
        var apiKey = authHeader?.Parameter ?? "";

        if (string.IsNullOrEmpty(apiKey))
        {
            return (false, false, "API key not configured. Set DodoPayments__ApiKey environment variable.");
        }

        // Show key details for debugging (safely masked)
        var keyLength = apiKey.Length;
        var keyPreview = keyLength > 20
            ? $"{apiKey[..10]}...{apiKey[^6..]} (length={keyLength})"
            : $"(too short, length={keyLength})";
        var hasSpecialChars = apiKey.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-');

        // Check if products are configured
        var hasProfessional = _productIds.TryGetValue(SubscriptionTier.Professional, out var profId) && !string.IsNullOrEmpty(profId);
        var hasEnterprise = _productIds.TryGetValue(SubscriptionTier.Enterprise, out var entId) && !string.IsNullOrEmpty(entId);

        if (!hasProfessional || !hasEnterprise)
        {
            return (false, false, $"Products not configured. Professional={profId ?? "not set"}, Enterprise={entId ?? "not set"}");
        }

        // Try to list products to verify API connection
        try
        {
            var response = await _httpClient.GetAsync("/products?page_size=1");
            var content = await response.Content.ReadAsStringAsync();

            var details = $"BaseUrl={_httpClient.BaseAddress}, Key={keyPreview}, HasSpecialChars={hasSpecialChars}, Professional={profId}, Enterprise={entId}";

            if (response.IsSuccessStatusCode)
            {
                return (true, true, $"Connected! {details}");
            }

            // Show response headers for debugging
            var responseHeaders = string.Join(", ", response.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}"));

            return (true, false, $"API error: {response.StatusCode}. Response={content}. Headers={responseHeaders}. {details}");
        }
        catch (Exception ex)
        {
            return (true, false, $"Connection error: {ex.Message}. Key={keyPreview}");
        }
    }
}
