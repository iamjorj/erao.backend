using System.Security.Cryptography;
using Erao.Core.DTOs.Admin;
using Erao.Core.Enums;
using Erao.Core.Interfaces;
using Erao.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Erao.Infrastructure.Services;

public class AdminService : IAdminService
{
    private const string AdminEmail = "erao.startup@gmail.com";
    private readonly EraoDbContext _db;
    private readonly ITokenService _tokenService;
    private readonly IEmailService _emailService;
    private readonly ILogger<AdminService> _logger;

    public AdminService(
        EraoDbContext db,
        ITokenService tokenService,
        IEmailService emailService,
        ILogger<AdminService> logger)
    {
        _db = db;
        _tokenService = tokenService;
        _emailService = emailService;
        _logger = logger;
    }

    // ─── Auth ────────────────────────────────────────────────────────

    public async Task<bool> IsAdminRegisteredAsync()
    {
        return await _db.Users.AnyAsync(u => u.IsAdmin);
    }

    public async Task InitiateRegistrationAsync(AdminRegisterRequest request)
    {
        if (!string.Equals(request.Email, AdminEmail, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This email is not authorized for admin registration.");

        if (await _db.Users.AnyAsync(u => u.IsAdmin))
            throw new InvalidOperationException("Admin account already registered.");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == request.Email.ToLower());
        if (user == null)
        {
            user = new Core.Entities.User
            {
                Email = request.Email.ToLower(),
                PasswordHash = "",
                FirstName = "Admin",
                LastName = "",
                IsEmailVerified = true,
                BillingCycleReset = DateTime.UtcNow.AddMonths(1),
            };
            _db.Users.Add(user);
        }

        var otp = GenerateOtp();
        user.AdminOtp = BCrypt.Net.BCrypt.HashPassword(otp);
        user.AdminOtpExpiry = DateTime.UtcNow.AddMinutes(10);
        await _db.SaveChangesAsync();

        await _emailService.SendAdminOtpAsync(request.Email, otp);
        _logger.LogInformation("Admin registration OTP sent to {Email}", request.Email);
    }

    public async Task<AdminAuthResponse> CompleteRegistrationAsync(AdminSetPasswordRequest request)
    {
        if (!string.Equals(request.Email, AdminEmail, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This email is not authorized for admin registration.");

        if (await _db.Users.AnyAsync(u => u.IsAdmin))
            throw new InvalidOperationException("Admin account already registered.");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == request.Email.ToLower())
            ?? throw new InvalidOperationException("User not found.");

        if (user.AdminOtp == null || user.AdminOtpExpiry == null || user.AdminOtpExpiry < DateTime.UtcNow)
            throw new InvalidOperationException("OTP expired or not found. Please request a new one.");

        if (!BCrypt.Net.BCrypt.Verify(request.Otp, user.AdminOtp))
            throw new InvalidOperationException("Invalid OTP.");

        user.IsAdmin = true;
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);
        user.AdminOtp = null;
        user.AdminOtpExpiry = null;
        await _db.SaveChangesAsync();

        var token = _tokenService.GenerateAccessToken(user);
        _logger.LogInformation("Admin registration completed for {Email}", request.Email);

        return new AdminAuthResponse
        {
            AccessToken = token,
            ExpiresAt = DateTime.UtcNow.AddHours(8)
        };
    }

    public async Task LoginAsync(AdminLoginRequest request)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == request.Email.ToLower())
            ?? throw new UnauthorizedAccessException("Invalid credentials.");

        if (!user.IsAdmin)
            throw new UnauthorizedAccessException("Invalid credentials.");

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            throw new UnauthorizedAccessException("Invalid credentials.");

        var otp = GenerateOtp();
        user.AdminOtp = BCrypt.Net.BCrypt.HashPassword(otp);
        user.AdminOtpExpiry = DateTime.UtcNow.AddMinutes(10);
        await _db.SaveChangesAsync();

        await _emailService.SendAdminOtpAsync(request.Email, otp);
        _logger.LogInformation("Admin login OTP sent to {Email}", request.Email);
    }

    public async Task<AdminAuthResponse> VerifyLoginOtpAsync(AdminVerifyOtpRequest request)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == request.Email.ToLower())
            ?? throw new UnauthorizedAccessException("Invalid credentials.");

        if (!user.IsAdmin)
            throw new UnauthorizedAccessException("Invalid credentials.");

        if (user.AdminOtp == null || user.AdminOtpExpiry == null || user.AdminOtpExpiry < DateTime.UtcNow)
            throw new InvalidOperationException("OTP expired. Please login again.");

        if (!BCrypt.Net.BCrypt.Verify(request.Otp, user.AdminOtp))
            throw new InvalidOperationException("Invalid OTP.");

        user.AdminOtp = null;
        user.AdminOtpExpiry = null;
        await _db.SaveChangesAsync();

        var token = _tokenService.GenerateAccessToken(user);
        _logger.LogInformation("Admin login verified for {Email}", request.Email);

        return new AdminAuthResponse
        {
            AccessToken = token,
            ExpiresAt = DateTime.UtcNow.AddHours(8)
        };
    }

    // ─── Dashboard ───────────────────────────────────────────────────

    public async Task<AdminDashboardDto> GetDashboardAsync()
    {
        var now = DateTime.UtcNow;
        var todayStart = now.Date;
        var weekStart = todayStart.AddDays(-(int)todayStart.DayOfWeek);
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var totalUsers = await _db.Users.CountAsync();
        var newToday = await _db.Users.CountAsync(u => u.CreatedAt >= todayStart);
        var newThisWeek = await _db.Users.CountAsync(u => u.CreatedAt >= weekStart);
        var newThisMonth = await _db.Users.CountAsync(u => u.CreatedAt >= monthStart);

        var sevenDaysAgo = now.AddDays(-7);
        var activeUsers = await _db.UsageLogs
            .Where(l => l.CreatedAt >= sevenDaysAgo)
            .Select(l => l.UserId)
            .Distinct()
            .CountAsync();

        var totalQueries = await _db.UsageLogs.CountAsync();

        var breakdown = new SubscriptionBreakdownDto
        {
            Starter = await _db.Users.CountAsync(u => u.SubscriptionTier == SubscriptionTier.Starter),
            Professional = await _db.Users.CountAsync(u => u.SubscriptionTier == SubscriptionTier.Professional),
            Enterprise = await _db.Users.CountAsync(u => u.SubscriptionTier == SubscriptionTier.Enterprise),
        };

        // Daily new users (last 30 days)
        var thirtyDaysAgo = todayStart.AddDays(-29);
        var dailyRaw = await _db.Users
            .Where(u => u.CreatedAt >= thirtyDaysAgo)
            .GroupBy(u => u.CreatedAt.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToListAsync();

        var dailyDict = dailyRaw.ToDictionary(d => d.Date, d => d.Count);
        var dailyNewUsers = new List<DailyChartPoint>();
        for (var d = thirtyDaysAgo; d <= todayStart; d = d.AddDays(1))
        {
            dailyNewUsers.Add(new DailyChartPoint
            {
                Date = d.ToString("MMM dd"),
                Count = dailyDict.GetValueOrDefault(d, 0)
            });
        }

        // Monthly new users (last 12 months)
        var twelveMonthsAgo = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-11);
        var monthlyRaw = await _db.Users
            .Where(u => u.CreatedAt >= twelveMonthsAgo)
            .GroupBy(u => new { u.CreatedAt.Year, u.CreatedAt.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Count = g.Count() })
            .ToListAsync();

        var monthlyDict = monthlyRaw.ToDictionary(m => $"{m.Year}-{m.Month:D2}", m => m.Count);
        var monthlyNewUsers = new List<MonthlyChartPoint>();
        for (var i = 0; i < 12; i++)
        {
            var month = twelveMonthsAgo.AddMonths(i);
            var key = $"{month.Year}-{month.Month:D2}";
            monthlyNewUsers.Add(new MonthlyChartPoint
            {
                Month = month.ToString("MMM yyyy"),
                Count = monthlyDict.GetValueOrDefault(key, 0)
            });
        }

        var recentSignups = await _db.Users
            .OrderByDescending(u => u.CreatedAt)
            .Take(10)
            .Select(u => new RecentSignupDto
            {
                Id = u.Id,
                Email = u.Email,
                FirstName = u.FirstName,
                LastName = u.LastName,
                SubscriptionTier = u.SubscriptionTier.ToString(),
                CreatedAt = u.CreatedAt,
            })
            .ToListAsync();

        return new AdminDashboardDto
        {
            TotalUsers = totalUsers,
            NewUsersToday = newToday,
            NewUsersThisWeek = newThisWeek,
            NewUsersThisMonth = newThisMonth,
            ActiveUsersLast7Days = activeUsers,
            TotalQueries = totalQueries,
            SubscriptionBreakdown = breakdown,
            DailyNewUsers = dailyNewUsers,
            MonthlyNewUsers = monthlyNewUsers,
            RecentSignups = recentSignups,
        };
    }

    // ─── Users ───────────────────────────────────────────────────────

    public async Task<AdminUserListDto> GetUsersAsync(string? search, string? tier, int page, int pageSize)
    {
        var query = _db.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLower();
            query = query.Where(u => u.Email.ToLower().Contains(s)
                || u.FirstName.ToLower().Contains(s)
                || u.LastName.ToLower().Contains(s));
        }

        if (!string.IsNullOrWhiteSpace(tier) && Enum.TryParse<SubscriptionTier>(tier, true, out var parsedTier))
        {
            query = query.Where(u => u.SubscriptionTier == parsedTier);
        }

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        var users = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new AdminUserDto
            {
                Id = u.Id,
                Email = u.Email,
                FirstName = u.FirstName,
                LastName = u.LastName,
                SubscriptionTier = u.SubscriptionTier,
                QueryLimitPerMonth = u.QueryLimitPerMonth,
                QueriesUsedThisMonth = u.QueriesUsedThisMonth,
                IsEmailVerified = u.IsEmailVerified,
                CreatedAt = u.CreatedAt,
                UpdatedAt = u.UpdatedAt,
                DatabaseCount = u.DatabaseConnections.Count,
                ConversationCount = u.Conversations.Count,
            })
            .ToListAsync();

        var userIds = users.Select(u => u.Id).ToList();
        var fileCounts = await _db.FileDocuments
            .Where(f => userIds.Contains(f.UserId))
            .GroupBy(f => f.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.UserId, g => g.Count);

        foreach (var user in users)
            user.FileCount = fileCounts.GetValueOrDefault(user.Id, 0);

        return new AdminUserListDto
        {
            Users = users,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
        };
    }

    public async Task<AdminUserDto> GetUserByIdAsync(Guid userId)
    {
        var user = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => new AdminUserDto
            {
                Id = u.Id,
                Email = u.Email,
                FirstName = u.FirstName,
                LastName = u.LastName,
                SubscriptionTier = u.SubscriptionTier,
                QueryLimitPerMonth = u.QueryLimitPerMonth,
                QueriesUsedThisMonth = u.QueriesUsedThisMonth,
                IsEmailVerified = u.IsEmailVerified,
                CreatedAt = u.CreatedAt,
                UpdatedAt = u.UpdatedAt,
                DatabaseCount = u.DatabaseConnections.Count,
                ConversationCount = u.Conversations.Count,
            })
            .FirstOrDefaultAsync() ?? throw new InvalidOperationException("User not found.");

        user.FileCount = await _db.FileDocuments.CountAsync(f => f.UserId == userId);
        return user;
    }

    public async Task<AdminUserDto> UpdateUserAsync(Guid userId, AdminUpdateUserRequest request)
    {
        var user = await _db.Users.FindAsync(userId)
            ?? throw new InvalidOperationException("User not found.");

        if (request.SubscriptionTier.HasValue)
        {
            user.SubscriptionTier = request.SubscriptionTier.Value;
            user.QueryLimitPerMonth = request.SubscriptionTier.Value switch
            {
                SubscriptionTier.Professional => 500,
                SubscriptionTier.Enterprise => 10000,
                _ => 25,
            };
        }

        if (request.QueryLimitPerMonth.HasValue)
            user.QueryLimitPerMonth = request.QueryLimitPerMonth.Value;

        if (request.ResetQueriesUsed == true)
            user.QueriesUsedThisMonth = 0;

        await _db.SaveChangesAsync();
        return await GetUserByIdAsync(userId);
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static string GenerateOtp()
    {
        return RandomNumberGenerator.GetInt32(100000, 999999).ToString();
    }
}
