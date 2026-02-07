namespace Erao.Core.DTOs.Auth;

public class RegisterResponse
{
    public string Email { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool RequiresEmailVerification { get; set; } = true;
}
