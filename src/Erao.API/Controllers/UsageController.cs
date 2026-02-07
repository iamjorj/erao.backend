using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Erao.Application.Services;
using Erao.Core.DTOs.Common;
using Erao.Core.DTOs.Usage;
using System.Security.Claims;

namespace Erao.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UsageController : ControllerBase
{
    private readonly IUsageService _usageService;
    private readonly ILogger<UsageController> _logger;

    public UsageController(IUsageService usageService, ILogger<UsageController> logger)
    {
        _usageService = usageService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<UsageDto>>> GetCurrentUsage()
    {
        try
        {
            var userId = GetUserId();
            var usage = await _usageService.GetCurrentUsageAsync(userId);
            return Ok(ApiResponse<UsageDto>.SuccessResponse(usage));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<UsageDto>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting usage");
            return StatusCode(500, ApiResponse<UsageDto>.ErrorResponse("An error occurred"));
        }
    }

    [HttpGet("history")]
    public async Task<ActionResult<ApiResponse<IEnumerable<UsageLogDto>>>> GetUsageHistory(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null)
    {
        try
        {
            var userId = GetUserId();
            var history = await _usageService.GetUsageHistoryAsync(userId, from, to);
            return Ok(ApiResponse<IEnumerable<UsageLogDto>>.SuccessResponse(history));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting usage history");
            return StatusCode(500, ApiResponse<IEnumerable<UsageLogDto>>.ErrorResponse("An error occurred"));
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
