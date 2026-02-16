using Erao.Core.Enums;

namespace Erao.Core.Entities;

public class User : BaseEntity
{
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public SubscriptionTier SubscriptionTier { get; set; } = SubscriptionTier.Starter;
    public DateTime? SubscriptionStartDate { get; set; }
    public int QueryLimitPerMonth { get; set; } = 25; // Default for Starter tier
    public int QueriesUsedThisMonth { get; set; } = 0;
    public DateTime BillingCycleReset { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiryTime { get; set; }

    // Password reset
    public string? PasswordResetOtp { get; set; }
    public DateTime? PasswordResetOtpExpiry { get; set; }

    // Email verification
    public bool IsEmailVerified { get; set; } = false;
    public string? EmailVerificationOtp { get; set; }
    public DateTime? EmailVerificationOtpExpiry { get; set; }

    // Dodo Payments
    public string? DodoCustomerId { get; set; }
    public string? DodoSubscriptionId { get; set; }

    // Navigation properties
    public virtual ICollection<DatabaseConnection> DatabaseConnections { get; set; } = new List<DatabaseConnection>();
    public virtual ICollection<Conversation> Conversations { get; set; } = new List<Conversation>();
    public virtual ICollection<UsageLog> UsageLogs { get; set; } = new List<UsageLog>();
}
