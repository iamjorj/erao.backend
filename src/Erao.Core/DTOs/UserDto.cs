using Erao.Core.Enums;

namespace Erao.Core.DTOs;

public class UserDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public SubscriptionTier SubscriptionTier { get; set; }
    public int QueryLimitPerMonth { get; set; }
    public int QueriesUsedThisMonth { get; set; }
    public DateTime CreatedAt { get; set; }
}
