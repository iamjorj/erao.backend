using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Erao.Core.DTOs.Admin;
using Erao.Core.DTOs.Common;
using Erao.Core.Interfaces;

namespace Erao.API.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly IAdminService _adminService;
    private readonly ILogger<AdminController> _logger;

    public AdminController(IAdminService adminService, ILogger<AdminController> logger)
    {
        _adminService = adminService;
        _logger = logger;
    }

    // ─── Auth (public, rate-limited) ─────────────────────────────────

    [HttpGet("auth/status")]
    [AllowAnonymous]
    [EnableRateLimiting("admin-auth")]
    public async Task<ActionResult<ApiResponse<AdminStatusResponse>>> GetStatus()
    {
        try
        {
            var isRegistered = await _adminService.IsAdminRegisteredAsync();
            return Ok(ApiResponse<AdminStatusResponse>.SuccessResponse(
                new AdminStatusResponse { IsRegistered = isRegistered }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking admin status");
            return StatusCode(500, ApiResponse<AdminStatusResponse>.ErrorResponse("An error occurred."));
        }
    }

    [HttpPost("auth/register")]
    [AllowAnonymous]
    [EnableRateLimiting("admin-auth")]
    public async Task<ActionResult<ApiResponse>> Register([FromBody] AdminRegisterRequest request)
    {
        try
        {
            await _adminService.InitiateRegistrationAsync(request);
            return Ok(ApiResponse.SuccessResponse("OTP sent to your email."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initiating admin registration");
            return StatusCode(500, ApiResponse.ErrorResponse("An error occurred."));
        }
    }

    [HttpPost("auth/register/complete")]
    [AllowAnonymous]
    [EnableRateLimiting("admin-auth")]
    public async Task<ActionResult<ApiResponse<AdminAuthResponse>>> CompleteRegistration([FromBody] AdminSetPasswordRequest request)
    {
        try
        {
            var result = await _adminService.CompleteRegistrationAsync(request);
            return Ok(ApiResponse<AdminAuthResponse>.SuccessResponse(result, "Admin account created."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<AdminAuthResponse>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing admin registration");
            return StatusCode(500, ApiResponse<AdminAuthResponse>.ErrorResponse("An error occurred."));
        }
    }

    [HttpPost("auth/login")]
    [AllowAnonymous]
    [EnableRateLimiting("admin-auth")]
    public async Task<ActionResult<ApiResponse>> Login([FromBody] AdminLoginRequest request)
    {
        try
        {
            await _adminService.LoginAsync(request);
            return Ok(ApiResponse.SuccessResponse("OTP sent to your email."));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(ApiResponse.ErrorResponse("Invalid credentials."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during admin login");
            return StatusCode(500, ApiResponse.ErrorResponse("An error occurred."));
        }
    }

    [HttpPost("auth/login/verify")]
    [AllowAnonymous]
    [EnableRateLimiting("admin-auth")]
    public async Task<ActionResult<ApiResponse<AdminAuthResponse>>> VerifyLoginOtp([FromBody] AdminVerifyOtpRequest request)
    {
        try
        {
            var result = await _adminService.VerifyLoginOtpAsync(request);
            return Ok(ApiResponse<AdminAuthResponse>.SuccessResponse(result, "Login successful."));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(ApiResponse<AdminAuthResponse>.ErrorResponse(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<AdminAuthResponse>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying admin OTP");
            return StatusCode(500, ApiResponse<AdminAuthResponse>.ErrorResponse("An error occurred."));
        }
    }

    [HttpPost("auth/refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("admin-auth")]
    public async Task<ActionResult<ApiResponse<AdminAuthResponse>>> RefreshToken([FromBody] AdminRefreshRequest request)
    {
        try
        {
            var result = await _adminService.RefreshTokenAsync(request.RefreshToken);
            return Ok(ApiResponse<AdminAuthResponse>.SuccessResponse(result));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(ApiResponse<AdminAuthResponse>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing admin token");
            return StatusCode(500, ApiResponse<AdminAuthResponse>.ErrorResponse("An error occurred."));
        }
    }

    // ─── Dashboard (admin only) ──────────────────────────────────────

    [HttpGet("dashboard")]
    [Authorize(Policy = "AdminOnly")]
    [EnableRateLimiting("general")]
    public async Task<ActionResult<ApiResponse<AdminDashboardDto>>> GetDashboard()
    {
        try
        {
            var result = await _adminService.GetDashboardAsync();
            return Ok(ApiResponse<AdminDashboardDto>.SuccessResponse(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching admin dashboard");
            return StatusCode(500, ApiResponse<AdminDashboardDto>.ErrorResponse("An error occurred."));
        }
    }

    // ─── Users (admin only) ──────────────────────────────────────────

    [HttpGet("users")]
    [Authorize(Policy = "AdminOnly")]
    [EnableRateLimiting("general")]
    public async Task<ActionResult<ApiResponse<AdminUserListDto>>> GetUsers(
        [FromQuery] string? search,
        [FromQuery] string? tier,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        try
        {
            var result = await _adminService.GetUsersAsync(search, tier, page, pageSize);
            return Ok(ApiResponse<AdminUserListDto>.SuccessResponse(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching users list");
            return StatusCode(500, ApiResponse<AdminUserListDto>.ErrorResponse("An error occurred."));
        }
    }

    [HttpGet("users/{id:guid}")]
    [Authorize(Policy = "AdminOnly")]
    [EnableRateLimiting("general")]
    public async Task<ActionResult<ApiResponse<AdminUserDto>>> GetUser(Guid id)
    {
        try
        {
            var result = await _adminService.GetUserByIdAsync(id);
            return Ok(ApiResponse<AdminUserDto>.SuccessResponse(result));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<AdminUserDto>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching user {UserId}", id);
            return StatusCode(500, ApiResponse<AdminUserDto>.ErrorResponse("An error occurred."));
        }
    }

    [HttpPatch("users/{id:guid}")]
    [Authorize(Policy = "AdminOnly")]
    [EnableRateLimiting("general")]
    public async Task<ActionResult<ApiResponse<AdminUserDto>>> UpdateUser(Guid id, [FromBody] AdminUpdateUserRequest request)
    {
        try
        {
            var result = await _adminService.UpdateUserAsync(id, request);
            return Ok(ApiResponse<AdminUserDto>.SuccessResponse(result, "User updated."));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<AdminUserDto>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating user {UserId}", id);
            return StatusCode(500, ApiResponse<AdminUserDto>.ErrorResponse("An error occurred."));
        }
    }
}
