using System.Security.Cryptography;
using Erao.Core.DTOs.Admin;
using Erao.Core.Enums;
using Erao.Core.Helpers;
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
        user.RefreshToken = _tokenService.GenerateRefreshToken();
        user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(7);
        await _db.SaveChangesAsync();

        var token = _tokenService.GenerateAccessToken(user);
        _logger.LogInformation("Admin registration completed for {Email}", request.Email);

        return new AdminAuthResponse
        {
            AccessToken = token,
            RefreshToken = user.RefreshToken,
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
        user.RefreshToken = _tokenService.GenerateRefreshToken();
        user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(7);
        await _db.SaveChangesAsync();

        var token = _tokenService.GenerateAccessToken(user);
        _logger.LogInformation("Admin login verified for {Email}", request.Email);

        return new AdminAuthResponse
        {
            AccessToken = token,
            RefreshToken = user.RefreshToken,
            ExpiresAt = DateTime.UtcNow.AddHours(8)
        };
    }

    public async Task<AdminAuthResponse> RefreshTokenAsync(string refreshToken)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.RefreshToken == refreshToken && u.IsAdmin)
            ?? throw new UnauthorizedAccessException("Invalid refresh token.");

        if (user.RefreshTokenExpiryTime == null || user.RefreshTokenExpiryTime < DateTime.UtcNow)
            throw new UnauthorizedAccessException("Refresh token expired.");

        user.RefreshToken = _tokenService.GenerateRefreshToken();
        user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(7);
        await _db.SaveChangesAsync();

        var token = _tokenService.GenerateAccessToken(user);

        return new AdminAuthResponse
        {
            AccessToken = token,
            RefreshToken = user.RefreshToken,
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

        // Revenue — MRR from current paid user counts
        var mrr = breakdown.Professional * SubscriptionLimits.GetPrice(SubscriptionTier.Professional)
                + breakdown.Enterprise * SubscriptionLimits.GetPrice(SubscriptionTier.Enterprise);

        // Today = actual revenue from subscriptions that started today
        var newSubsToday = await _db.Users
            .Where(u => u.SubscriptionTier != SubscriptionTier.Starter
                && u.SubscriptionStartDate != null
                && u.SubscriptionStartDate >= todayStart)
            .Select(u => u.SubscriptionTier)
            .ToListAsync();
        var todayRevenue = newSubsToday.Sum(t => SubscriptionLimits.GetPrice(t));

        // Monthly revenue chart (last 12 months)
        var paidUsers = await _db.Users
            .Where(u => u.SubscriptionTier != SubscriptionTier.Starter && u.SubscriptionStartDate != null)
            .Select(u => new { u.SubscriptionTier, u.SubscriptionStartDate })
            .ToListAsync();

        var monthlyRevenueList = new List<MonthlyRevenuePoint>();
        var currentMonthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var thisYearRevenue = 0m;

        for (var i = 0; i < 12; i++)
        {
            var mStart = twelveMonthsAgo.AddMonths(i);
            var mEnd = mStart.AddMonths(1);

            // Current month: use live MRR; past months: use SubscriptionStartDate
            var amount = mStart == currentMonthStart
                ? mrr
                : paidUsers
                    .Where(u => u.SubscriptionStartDate < mEnd)
                    .Sum(u => SubscriptionLimits.GetPrice(u.SubscriptionTier));

            monthlyRevenueList.Add(new MonthlyRevenuePoint
            {
                Month = mStart.ToString("MMM yyyy"),
                Amount = amount,
            });

            // Sum months in current year for This Year
            if (mStart.Year == now.Year)
                thisYearRevenue += amount;
        }

        var revenue = new RevenueDto
        {
            Mrr = mrr,
            Today = todayRevenue,
            ThisYear = thisYearRevenue,
        };

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
            Revenue = revenue,
            DailyNewUsers = dailyNewUsers,
            MonthlyNewUsers = monthlyNewUsers,
            MonthlyRevenue = monthlyRevenueList,
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
                SubscriptionStartDate = u.SubscriptionStartDate,
                SubscriptionEndDate = u.SubscriptionEndDate,
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
                SubscriptionStartDate = u.SubscriptionStartDate,
                SubscriptionEndDate = u.SubscriptionEndDate,
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
            var oldTier = user.SubscriptionTier;
            user.SubscriptionTier = request.SubscriptionTier.Value;
            user.QueryLimitPerMonth = request.SubscriptionTier.Value switch
            {
                SubscriptionTier.Professional => 500,
                SubscriptionTier.Enterprise => 10000,
                _ => 25,
            };

            // Track subscription dates
            if (request.SubscriptionTier.Value != SubscriptionTier.Starter)
            {
                if (oldTier != request.SubscriptionTier.Value || user.SubscriptionStartDate == null)
                {
                    user.SubscriptionStartDate = DateTime.UtcNow;
                    user.SubscriptionEndDate = DateTime.UtcNow.AddDays(30);
                }
            }
            else
            {
                user.SubscriptionStartDate = null;
                user.SubscriptionEndDate = null;
            }
        }

        if (request.QueryLimitPerMonth.HasValue)
            user.QueryLimitPerMonth = request.QueryLimitPerMonth.Value;

        if (request.ResetQueriesUsed == true)
            user.QueriesUsedThisMonth = 0;

        await _db.SaveChangesAsync();
        return await GetUserByIdAsync(userId);
    }

    // ─── Conversations ──────────────────────────────────────────────

    public async Task<AdminConversationListDto> GetUserConversationsAsync(Guid userId)
    {
        var conversations = await _db.Conversations
            .Where(c => c.UserId == userId)
            .Include(c => c.DatabaseConnection)
            .Include(c => c.FileDocument)
            .Include(c => c.AppConnector)
            .Include(c => c.Messages)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new AdminConversationDto
            {
                Id = c.Id,
                Title = c.Title,
                DataSourceName = c.DatabaseConnection != null ? c.DatabaseConnection.Name
                    : c.FileDocument != null ? c.FileDocument.OriginalFileName
                    : c.AppConnector != null ? c.AppConnector.Name
                    : null,
                DataSourceType = c.DatabaseConnectionId != null ? "Database"
                    : c.FileDocumentId != null ? "File"
                    : c.AppConnectorId != null ? "App"
                    : null,
                MessageCount = c.Messages.Count,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt,
            })
            .ToListAsync();

        return new AdminConversationListDto
        {
            Conversations = conversations,
            TotalCount = conversations.Count,
        };
    }

    public async Task<AdminConversationDetailDto> GetConversationDetailAsync(Guid conversationId)
    {
        var conversation = await _db.Conversations
            .Where(c => c.Id == conversationId)
            .Include(c => c.User)
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
            .Include(c => c.DatabaseConnection)
            .Include(c => c.FileDocument)
            .Include(c => c.AppConnector)
            .FirstOrDefaultAsync()
            ?? throw new InvalidOperationException("Conversation not found.");

        return new AdminConversationDetailDto
        {
            Id = conversation.Id,
            Title = conversation.Title,
            DataSourceName = conversation.DatabaseConnection?.Name
                ?? conversation.FileDocument?.OriginalFileName
                ?? conversation.AppConnector?.Name,
            DataSourceType = conversation.DatabaseConnectionId != null ? "Database"
                : conversation.FileDocumentId != null ? "File"
                : conversation.AppConnectorId != null ? "App"
                : null,
            UserId = conversation.UserId,
            UserEmail = conversation.User.Email,
            Messages = conversation.Messages.Select(m => new AdminMessageDto
            {
                Id = m.Id,
                Role = (int)m.Role,
                Content = m.Content,
                SqlQuery = m.SqlQuery,
                QueryResult = m.QueryResult,
                TokensUsed = m.TokensUsed,
                CreatedAt = m.CreatedAt,
            }).ToList(),
            CreatedAt = conversation.CreatedAt,
            UpdatedAt = conversation.UpdatedAt,
        };
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static string GenerateOtp()
    {
        return RandomNumberGenerator.GetInt32(100000, 999999).ToString();
    }
}
