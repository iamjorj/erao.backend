using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Erao.Application.Services;
using Erao.Core.DTOs;
using Erao.Core.DTOs.Common;
using System.Security.Claims;

namespace Erao.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AccountController : ControllerBase
{
    private readonly IAccountService _accountService;
    private readonly ILogger<AccountController> _logger;

    public AccountController(IAccountService accountService, ILogger<AccountController> logger)
    {
        _accountService = accountService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<UserDto>>> GetAccount()
    {
        try
        {
            var userId = GetUserId();
            var user = await _accountService.GetAccountAsync(userId);
            if (user == null)
            {
                return NotFound(ApiResponse<UserDto>.ErrorResponse("Account not found"));
            }
            return Ok(ApiResponse<UserDto>.SuccessResponse(user));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting account");
            return StatusCode(500, ApiResponse<UserDto>.ErrorResponse("An error occurred"));
        }
    }

    [HttpPut]
    public async Task<ActionResult<ApiResponse<UserDto>>> UpdateAccount([FromBody] UpdateAccountRequest request)
    {
        try
        {
            var userId = GetUserId();
            var user = await _accountService.UpdateAccountAsync(userId, request.FirstName, request.LastName);
            return Ok(ApiResponse<UserDto>.SuccessResponse(user, "Account updated"));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<UserDto>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating account");
            return StatusCode(500, ApiResponse<UserDto>.ErrorResponse("An error occurred"));
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

public class UpdateAccountRequest
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
}
