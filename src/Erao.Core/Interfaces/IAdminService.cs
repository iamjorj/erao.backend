using Erao.Core.DTOs.Admin;

namespace Erao.Core.Interfaces;

public interface IAdminService
{
    // Auth
    Task<bool> IsAdminRegisteredAsync();
    Task InitiateRegistrationAsync(AdminRegisterRequest request);
    Task<AdminAuthResponse> CompleteRegistrationAsync(AdminSetPasswordRequest request);
    Task LoginAsync(AdminLoginRequest request);
    Task<AdminAuthResponse> VerifyLoginOtpAsync(AdminVerifyOtpRequest request);

    // Dashboard
    Task<AdminDashboardDto> GetDashboardAsync();

    // Users
    Task<AdminUserListDto> GetUsersAsync(string? search, string? tier, int page, int pageSize);
    Task<AdminUserDto> GetUserByIdAsync(Guid userId);
    Task<AdminUserDto> UpdateUserAsync(Guid userId, AdminUpdateUserRequest request);
}
