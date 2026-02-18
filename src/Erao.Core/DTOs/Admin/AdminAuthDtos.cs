namespace Erao.Core.DTOs.Admin;

public class AdminRegisterRequest
{
    public string Email { get; set; } = string.Empty;
}

public class AdminSetPasswordRequest
{
    public string Email { get; set; } = string.Empty;
    public string Otp { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class AdminLoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class AdminVerifyOtpRequest
{
    public string Email { get; set; } = string.Empty;
    public string Otp { get; set; } = string.Empty;
}

public class AdminAuthResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}

public class AdminRefreshRequest
{
    public string RefreshToken { get; set; } = string.Empty;
}

public class AdminStatusResponse
{
    public bool IsRegistered { get; set; }
}
