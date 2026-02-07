using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Erao.Application.Services;
using Erao.Core.DTOs.Common;
using Erao.Core.DTOs.Subscription;
using System.Security.Claims;

namespace Erao.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SubscriptionsController : ControllerBase
{
    private readonly ISubscriptionService _subscriptionService;
    private readonly ILogger<SubscriptionsController> _logger;

    public SubscriptionsController(ISubscriptionService subscriptionService, ILogger<SubscriptionsController> logger)
    {
        _subscriptionService = subscriptionService;
        _logger = logger;
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
            return BadRequest(ApiResponse<CheckoutResponse>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating upgrade checkout");
            return StatusCode(500, ApiResponse<CheckoutResponse>.ErrorResponse("An error occurred"));
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
