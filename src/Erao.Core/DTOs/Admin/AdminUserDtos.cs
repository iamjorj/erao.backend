using Erao.Core.Enums;

namespace Erao.Core.DTOs.Admin;

public class AdminUserListDto
{
    public List<AdminUserDto> Users { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}

public class AdminUserDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public SubscriptionTier SubscriptionTier { get; set; }
    public int QueryLimitPerMonth { get; set; }
    public int QueriesUsedThisMonth { get; set; }
    public bool IsEmailVerified { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int DatabaseCount { get; set; }
    public int FileCount { get; set; }
    public int ConversationCount { get; set; }
}

public class AdminUpdateUserRequest
{
    public SubscriptionTier? SubscriptionTier { get; set; }
    public int? QueryLimitPerMonth { get; set; }
    public bool? ResetQueriesUsed { get; set; }
}
