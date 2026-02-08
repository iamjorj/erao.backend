using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Erao.Application.Services;
using Erao.Core.DTOs.Common;
using Erao.Core.DTOs.Subscription;
using Erao.Core.Interfaces;
using System.Security.Claims;

namespace Erao.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SubscriptionsController : ControllerBase
{
    private readonly ISubscriptionService _subscriptionService;
    private readonly IDodoPaymentsService _dodoPayments;
    private readonly ILogger<SubscriptionsController> _logger;

    public SubscriptionsController(
        ISubscriptionService subscriptionService,
        IDodoPaymentsService dodoPayments,
        ILogger<SubscriptionsController> logger)
    {
        _subscriptionService = subscriptionService;
        _dodoPayments = dodoPayments;
        _logger = logger;
    }

    /// <summary>
    /// Test Dodo Payments API connection (for debugging)
    /// </summary>
    [HttpGet("test-payments")]
    [AllowAnonymous]
    public async Task<ActionResult> TestPaymentsConnection()
    {
        var (isConfigured, isConnected, message) = await _dodoPayments.TestConnectionAsync();

        var result = new
        {
            success = isConnected,
            isConfigured,
            isConnected,
            message
        };

        if (isConnected)
            return Ok(result);

        return BadRequest(result);
    }

    [HttpGet("plans")]
    public async Task<ActionResult<ApiResponse<IEnumerable<SubscriptionPlanDto>>>> GetPlans()
    {
        try
        {
            var userId = GetUserId();
            var plans = await _subscriptionService.GetPlansAsync(userId);
            return Ok(ApiResponse<IEnumerable<SubscriptionPlanDto>>.SuccessResponse(plans));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting subscription plans");
            return StatusCode(500, ApiResponse<IEnumerable<SubscriptionPlanDto>>.ErrorResponse("An error occurred"));
        }
    }

    [HttpGet("current")]
    public async Task<ActionResult<ApiResponse<SubscriptionResponse>>> GetCurrentSubscription()
    {
        try
        {
            var userId = GetUserId();
            var subscription = await _subscriptionService.GetCurrentSubscriptionAsync(userId);
            return Ok(ApiResponse<SubscriptionResponse>.SuccessResponse(subscription));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<SubscriptionResponse>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting current subscription");
            return StatusCode(500, ApiResponse<SubscriptionResponse>.ErrorResponse("An error occurred"));
        }
    }

    /// <summary>
    /// Create a Dodo Payments checkout session to upgrade to a paid plan.
    /// Returns a checkout URL - redirect the user there to pay.
    /// </summary>
    [HttpPost("upgrade")]
    public async Task<ActionResult<ApiResponse<CheckoutResponse>>> UpgradeSubscription([FromBody] UpgradeSubscriptionRequest request)
    {
        try
        {
            var userId = GetUserId();
            var checkout = await _subscriptionService.CreateUpgradeCheckoutAsync(userId, request.NewTier, request.ReturnUrl);
            return Ok(ApiResponse<CheckoutResponse>.SuccessResponse(checkout, "Checkout session created"));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation during upgrade checkout");
            return BadRequest(ApiResponse<CheckoutResponse>.ErrorResponse(ex.Message));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Payment provider API error during upgrade checkout");
            return StatusCode(503, ApiResponse<CheckoutResponse>.ErrorResponse("Payment service temporarily unavailable. Please try again later."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating upgrade checkout: {Message}", ex.Message);
            return StatusCode(500, ApiResponse<CheckoutResponse>.ErrorResponse("Failed to create checkout. Please try again or contact support."));
        }
    }

    /// <summary>
    /// Downgrade to the free (Starter) plan.
    /// </summary>
    [HttpPost("downgrade")]
    public async Task<ActionResult<ApiResponse<SubscriptionResponse>>> DowngradeSubscription()
    {
        try
        {
            var userId = GetUserId();
            var subscription = await _subscriptionService.DowngradeToFreeAsync(userId);
            return Ok(ApiResponse<SubscriptionResponse>.SuccessResponse(subscription, "Downgraded to free plan"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<SubscriptionResponse>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downgrading subscription");
            return StatusCode(500, ApiResponse<SubscriptionResponse>.ErrorResponse("An error occurred"));
        }
    }

    private Guid GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            throw new UnauthorizedAccessException("Invalid user");
        }
        return userId;
    }
}
