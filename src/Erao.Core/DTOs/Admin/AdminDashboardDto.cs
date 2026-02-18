namespace Erao.Core.DTOs.Admin;

public class AdminDashboardDto
{
    public int TotalUsers { get; set; }
    public int NewUsersToday { get; set; }
    public int NewUsersThisWeek { get; set; }
    public int NewUsersThisMonth { get; set; }
    public int ActiveUsersLast7Days { get; set; }
    public int TotalQueries { get; set; }

    public SubscriptionBreakdownDto SubscriptionBreakdown { get; set; } = new();

    public List<DailyChartPoint> DailyNewUsers { get; set; } = [];
    public List<MonthlyChartPoint> MonthlyNewUsers { get; set; } = [];

    public List<RecentSignupDto> RecentSignups { get; set; } = [];
}

public class SubscriptionBreakdownDto
{
    public int Starter { get; set; }
    public int Professional { get; set; }
    public int Enterprise { get; set; }
}

public class DailyChartPoint
{
    public string Date { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class MonthlyChartPoint
{
    public string Month { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class RecentSignupDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string SubscriptionTier { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
