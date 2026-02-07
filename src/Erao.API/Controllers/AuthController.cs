using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Erao.Application.Services;
using Erao.Core.DTOs.Auth;
using Erao.Core.DTOs.Common;
using System.Security.Claims;

namespace Erao.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IAuthService authService, ILogger<AuthController> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<RegisterResponse>>> Register([FromBody] RegisterRequest request)
    {
        try
        {
            var result = await _authService.RegisterAsync(request);
            return Ok(ApiResponse<RegisterResponse>.SuccessResponse(result, "Registration successful. Please verify your email."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<RegisterResponse>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during registration");
            return StatusCode(500, ApiResponse<RegisterResponse>.ErrorResponse("An error occurred during registration"));
        }
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<AuthResponse>>> Login([FromBody] LoginRequest request)
    {
        try
        {
            var result = await _authService.LoginAsync(request);
            return Ok(ApiResponse<AuthResponse>.SuccessResponse(result, "Login successful"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(ApiResponse<AuthResponse>.ErrorResponse(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            // Email not verified
            return BadRequest(ApiResponse<AuthResponse>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during login");
            return StatusCode(500, ApiResponse<AuthResponse>.ErrorResponse("An error occurred during login"));
        }
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<AuthResponse>>> RefreshToken([FromBody] RefreshTokenRequest request)
    {
        try
        {
            var result = await _authService.RefreshTokenAsync(request.RefreshToken);
            return Ok(ApiResponse<AuthResponse>.SuccessResponse(result, "Token refreshed successfully"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(ApiResponse<AuthResponse>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing token");
            return StatusCode(500, ApiResponse<AuthResponse>.ErrorResponse("An error occurred while refreshing token"));
        }
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<ActionResult<ApiResponse>> Logout()
    {
        try
        {
            var userId = GetUserId();
            await _authService.LogoutAsync(userId);
            return Ok(ApiResponse.SuccessResponse("Logged out successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during logout");
            return StatusCode(500, ApiResponse.ErrorResponse("An error occurred during logout"));
        }
    }

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse>> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        try
        {
            await _authService.ForgotPasswordAsync(request);
            // Always return success to prevent email enumeration
            return Ok(ApiResponse.SuccessResponse("If an account with that email exists, a password reset code has been sent."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during forgot password");
            return StatusCode(500, ApiResponse.ErrorResponse("An error occurred while processing your request"));
        }
    }

    [HttpPost("verify-otp")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<bool>>> VerifyOtp([FromBody] VerifyOtpRequest request)
    {
        try
        {
            var isValid = await _authService.VerifyOtpAsync(request);
            if (isValid)
            {
                return Ok(ApiResponse<bool>.SuccessResponse(true, "OTP is valid"));
            }
            return BadRequest(ApiResponse<bool>.ErrorResponse("Invalid or expired OTP"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying OTP");
            return StatusCode(500, ApiResponse<bool>.ErrorResponse("An error occurred while verifying OTP"));
        }
    }

    [HttpPost("resend-otp")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse>> ResendOtp([FromBody] ResendOtpRequest request)
    {
        try
        {
            await _authService.ResendOtpAsync(request.Email);
            // Always return success to prevent email enumeration
            return Ok(ApiResponse.SuccessResponse("If an account with that email exists, a new code has been sent."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resending OTP");
            return StatusCode(500, ApiResponse.ErrorResponse("An error occurred while processing your request"));
        }
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse>> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        try
        {
            await _authService.ResetPasswordAsync(request);
            return Ok(ApiResponse.SuccessResponse("Password has been reset successfully"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting password");
            return StatusCode(500, ApiResponse.ErrorResponse("An error occurred while resetting password"));
        }
    }

    [HttpPost("verify-email")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<AuthResponse>>> VerifyEmail([FromBody] VerifyOtpRequest request)
    {
        try
        {
            var result = await _authService.VerifyEmailAsync(request);
            return Ok(ApiResponse<AuthResponse>.SuccessResponse(result, "Email verified successfully"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<AuthResponse>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying email");
            return StatusCode(500, ApiResponse<AuthResponse>.ErrorResponse("An error occurred while verifying email"));
        }
    }

    [HttpPost("resend-email-verification")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse>> ResendEmailVerification([FromBody] ResendOtpRequest request)
    {
        try
        {
            await _authService.ResendEmailVerificationOtpAsync(request.Email);
            // Always return success to prevent email enumeration
            return Ok(ApiResponse.SuccessResponse("If an account with that email exists and is not verified, a new code has been sent."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resending email verification OTP");
            return StatusCode(500, ApiResponse.ErrorResponse("An error occurred while processing your request"));
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
