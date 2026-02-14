using AutoMapper;
using Erao.Core.DTOs;
using Erao.Core.DTOs.Auth;
using Erao.Core.Entities;
using Erao.Core.Enums;
using Erao.Core.Helpers;
using Erao.Core.Interfaces;
using Google.Apis.Auth;
using Microsoft.Extensions.Configuration;

namespace Erao.Application.Services;

public interface IAuthService
{
    Task<RegisterResponse> RegisterAsync(RegisterRequest request);
    Task<AuthResponse> LoginAsync(LoginRequest request);
    Task<AuthResponse> RefreshTokenAsync(string refreshToken);
    Task LogoutAsync(Guid userId);
    Task ForgotPasswordAsync(ForgotPasswordRequest request);
    Task<bool> VerifyOtpAsync(VerifyOtpRequest request);
    Task ResetPasswordAsync(ResetPasswordRequest request);
    Task ResendOtpAsync(string email);
    Task<AuthResponse> VerifyEmailAsync(VerifyOtpRequest request);
    Task ResendEmailVerificationOtpAsync(string email);
    Task<AuthResponse> GoogleLoginAsync(string idToken);
}

public class AuthService : IAuthService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITokenService _tokenService;
    private readonly IEmailService _emailService;
    private readonly IMapper _mapper;
    private readonly int _refreshTokenExpirationDays;
    private readonly string _googleClientId;
    private const int OtpExpirationMinutes = 15;

    public AuthService(
        IUnitOfWork unitOfWork,
        ITokenService tokenService,
        IEmailService emailService,
        IMapper mapper,
        IConfiguration configuration)
    {
        _unitOfWork = unitOfWork;
        _tokenService = tokenService;
        _emailService = emailService;
        _mapper = mapper;
        _refreshTokenExpirationDays = int.Parse(configuration["Jwt:RefreshTokenExpirationDays"] ?? "7");
        _googleClientId = configuration["Google:ClientId"] ?? string.Empty;
    }

    public async Task<RegisterResponse> RegisterAsync(RegisterRequest request)
    {
        if (await _unitOfWork.Users.EmailExistsAsync(request.Email))
        {
            throw new InvalidOperationException("Email already registered");
        }

        // Generate 6-digit OTP for email verification
        var otp = GenerateOtp();

        var user = new User
        {
            Email = request.Email.ToLower(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FirstName = request.FirstName,
            LastName = request.LastName,
            SubscriptionTier = SubscriptionTier.Starter,
            QueryLimitPerMonth = GetQueryLimitForTier(SubscriptionTier.Starter),
            BillingCycleReset = DateTime.UtcNow.AddMonths(1),
            IsEmailVerified = false,
            EmailVerificationOtp = otp,
            EmailVerificationOtpExpiry = DateTime.UtcNow.AddMinutes(OtpExpirationMinutes)
        };

        await _unitOfWork.Users.AddAsync(user);
        await _unitOfWork.SaveChangesAsync();

        // Send verification OTP via email (don't fail registration if email fails)
        try
        {
            await _emailService.SendEmailVerificationOtpAsync(user.Email, otp);
        }
        catch (Exception)
        {
            // Log OTP for development testing when email fails
            Console.WriteLine($"[DEV] Email verification OTP for {user.Email}: {otp}");
        }

        return new RegisterResponse
        {
            Email = user.Email,
            Message = "Registration successful. Please verify your email with the OTP sent to your inbox.",
            RequiresEmailVerification = true
        };
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request)
    {
        var user = await _unitOfWork.Users.GetByEmailAsync(request.Email);

        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            throw new UnauthorizedAccessException("Invalid email or password");
        }

        if (!user.IsEmailVerified)
        {
            throw new InvalidOperationException("Email not verified. Please verify your email first.");
        }

        user.RefreshToken = _tokenService.GenerateRefreshToken();
        user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(_refreshTokenExpirationDays);

        await _unitOfWork.Users.UpdateAsync(user);
        await _unitOfWork.SaveChangesAsync();

        var accessToken = _tokenService.GenerateAccessToken(user);

        return new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = user.RefreshToken,
            ExpiresAt = DateTime.UtcNow.AddMinutes(15),
            User = _mapper.Map<UserDto>(user)
        };
    }

    public async Task<AuthResponse> RefreshTokenAsync(string refreshToken)
    {
        var user = await _unitOfWork.Users.GetByRefreshTokenAsync(refreshToken);

        if (user == null || user.RefreshTokenExpiryTime <= DateTime.UtcNow)
        {
            throw new UnauthorizedAccessException("Invalid or expired refresh token");
        }

        user.RefreshToken = _tokenService.GenerateRefreshToken();
        user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(_refreshTokenExpirationDays);

        await _unitOfWork.Users.UpdateAsync(user);
        await _unitOfWork.SaveChangesAsync();

        var accessToken = _tokenService.GenerateAccessToken(user);

        return new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = user.RefreshToken,
            ExpiresAt = DateTime.UtcNow.AddMinutes(15),
            User = _mapper.Map<UserDto>(user)
        };
    }

    public async Task LogoutAsync(Guid userId)
    {
        var user = await _unitOfWork.Users.GetByIdAsync(userId);

        if (user != null)
        {
            user.RefreshToken = null;
            user.RefreshTokenExpiryTime = null;
            await _unitOfWork.Users.UpdateAsync(user);
            await _unitOfWork.SaveChangesAsync();
        }
    }

    public async Task ForgotPasswordAsync(ForgotPasswordRequest request)
    {
        var user = await _unitOfWork.Users.GetByEmailAsync(request.Email);

        // Always return success to prevent email enumeration attacks
        if (user == null)
        {
            return;
        }

        // Generate 6-digit OTP
        var otp = GenerateOtp();

        user.PasswordResetOtp = otp;
        user.PasswordResetOtpExpiry = DateTime.UtcNow.AddMinutes(OtpExpirationMinutes);

        await _unitOfWork.Users.UpdateAsync(user);
        await _unitOfWork.SaveChangesAsync();

        // Send OTP via email
        await _emailService.SendPasswordResetOtpAsync(user.Email, otp);
    }

    public async Task<bool> VerifyOtpAsync(VerifyOtpRequest request)
    {
        var user = await _unitOfWork.Users.GetByEmailAsync(request.Email);

        if (user == null)
        {
            return false;
        }

        if (string.IsNullOrEmpty(user.PasswordResetOtp) ||
            user.PasswordResetOtpExpiry == null ||
            user.PasswordResetOtpExpiry <= DateTime.UtcNow)
        {
            return false;
        }

        return user.PasswordResetOtp == request.Otp;
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request)
    {
        var user = await _unitOfWork.Users.GetByEmailAsync(request.Email);

        if (user == null)
        {
            throw new InvalidOperationException("User not found");
        }

        if (string.IsNullOrEmpty(user.PasswordResetOtp) ||
            user.PasswordResetOtpExpiry == null ||
            user.PasswordResetOtpExpiry <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("OTP has expired or is invalid");
        }

        if (user.PasswordResetOtp != request.Otp)
        {
            throw new InvalidOperationException("Invalid OTP");
        }

        // Update password
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);

        // Clear OTP fields
        user.PasswordResetOtp = null;
        user.PasswordResetOtpExpiry = null;

        // Invalidate refresh token to force re-login
        user.RefreshToken = null;
        user.RefreshTokenExpiryTime = null;

        await _unitOfWork.Users.UpdateAsync(user);
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task ResendOtpAsync(string email)
    {
        var user = await _unitOfWork.Users.GetByEmailAsync(email);

        // Always return success to prevent email enumeration attacks
        if (user == null)
        {
            return;
        }

        // Generate new 6-digit OTP
        var otp = GenerateOtp();

        user.PasswordResetOtp = otp;
        user.PasswordResetOtpExpiry = DateTime.UtcNow.AddMinutes(OtpExpirationMinutes);

        await _unitOfWork.Users.UpdateAsync(user);
        await _unitOfWork.SaveChangesAsync();

        // Send OTP via email
        await _emailService.SendPasswordResetOtpAsync(user.Email, otp);
    }

    public async Task<AuthResponse> VerifyEmailAsync(VerifyOtpRequest request)
    {
        var user = await _unitOfWork.Users.GetByEmailAsync(request.Email);

        if (user == null)
        {
            throw new InvalidOperationException("User not found");
        }

        if (user.IsEmailVerified)
        {
            throw new InvalidOperationException("Email is already verified");
        }

        if (string.IsNullOrEmpty(user.EmailVerificationOtp) ||
            user.EmailVerificationOtpExpiry == null ||
            user.EmailVerificationOtpExpiry <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("OTP has expired. Please request a new one.");
        }

        if (user.EmailVerificationOtp != request.Otp)
        {
            throw new InvalidOperationException("Invalid OTP");
        }

        // Mark email as verified
        user.IsEmailVerified = true;
        user.EmailVerificationOtp = null;
        user.EmailVerificationOtpExpiry = null;

        // Generate tokens for auto-login after verification
        user.RefreshToken = _tokenService.GenerateRefreshToken();
        user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(_refreshTokenExpirationDays);

        await _unitOfWork.Users.UpdateAsync(user);
        await _unitOfWork.SaveChangesAsync();

        var accessToken = _tokenService.GenerateAccessToken(user);

        return new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = user.RefreshToken,
            ExpiresAt = DateTime.UtcNow.AddMinutes(15),
            User = _mapper.Map<UserDto>(user)
        };
    }

    public async Task ResendEmailVerificationOtpAsync(string email)
    {
        var user = await _unitOfWork.Users.GetByEmailAsync(email);

        // Always return success to prevent email enumeration attacks
        if (user == null)
        {
            return;
        }

        if (user.IsEmailVerified)
        {
            return;
        }

        // Generate new 6-digit OTP
        var otp = GenerateOtp();

        user.EmailVerificationOtp = otp;
        user.EmailVerificationOtpExpiry = DateTime.UtcNow.AddMinutes(OtpExpirationMinutes);

        await _unitOfWork.Users.UpdateAsync(user);
        await _unitOfWork.SaveChangesAsync();

        // Send OTP via email (don't fail if email fails)
        try
        {
            await _emailService.SendEmailVerificationOtpAsync(user.Email, otp);
        }
        catch (Exception)
        {
            // Log OTP for development testing when email fails
            Console.WriteLine($"[DEV] Email verification OTP for {user.Email}: {otp}");
        }
    }

    public async Task<AuthResponse> GoogleLoginAsync(string idToken)
    {
        GoogleJsonWebSignature.Payload payload;
        try
        {
            // First try as ID token (from GoogleLogin component)
            var settings = new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = new[] { _googleClientId }
            };
            payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);
        }
        catch
        {
            // Fallback: treat as access token (from custom useGoogleLogin button)
            try
            {
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);
                var response = await httpClient.GetAsync("https://www.googleapis.com/oauth2/v3/userinfo");
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                var userInfo = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
                payload = new GoogleJsonWebSignature.Payload
                {
                    Email = userInfo.GetProperty("email").GetString() ?? "",
                    GivenName = userInfo.TryGetProperty("given_name", out var gn) ? gn.GetString() : "",
                    FamilyName = userInfo.TryGetProperty("family_name", out var fn) ? fn.GetString() : ""
                };
            }
            catch (Exception ex)
            {
                throw new UnauthorizedAccessException($"Google token validation failed: {ex.Message}");
            }
        }

        var email = payload.Email.ToLower();
        var user = await _unitOfWork.Users.GetByEmailAsync(email);

        if (user == null)
        {
            // Create new user — no password needed, email already verified by Google
            user = new User
            {
                Email = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()),
                FirstName = payload.GivenName ?? "",
                LastName = payload.FamilyName ?? "",
                SubscriptionTier = SubscriptionTier.Starter,
                QueryLimitPerMonth = GetQueryLimitForTier(SubscriptionTier.Starter),
                BillingCycleReset = DateTime.UtcNow.AddMonths(1),
                IsEmailVerified = true,
                RefreshToken = _tokenService.GenerateRefreshToken(),
                RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(_refreshTokenExpirationDays)
            };
            await _unitOfWork.Users.AddAsync(user);
            await _unitOfWork.SaveChangesAsync();
        }
        else
        {
            if (!user.IsEmailVerified)
            {
                user.IsEmailVerified = true;
            }

            user.RefreshToken = _tokenService.GenerateRefreshToken();
            user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(_refreshTokenExpirationDays);

            await _unitOfWork.Users.UpdateAsync(user);
            await _unitOfWork.SaveChangesAsync();
        }

        var accessToken = _tokenService.GenerateAccessToken(user);

        return new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = user.RefreshToken,
            ExpiresAt = DateTime.UtcNow.AddMinutes(15),
            User = _mapper.Map<UserDto>(user)
        };
    }

    private static string GenerateOtp()
    {
        var random = new Random();
        return random.Next(100000, 999999).ToString();
    }

    private static int GetQueryLimitForTier(SubscriptionTier tier)
    {
        return SubscriptionLimits.GetQueryLimit(tier);
    }
}
