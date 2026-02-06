using Erao.Core.Enums;

namespace Erao.Core.DTOs.Subscription;

public class SubscriptionPlanDto
{
    public SubscriptionTier Tier { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Description { get; set; } = string.Empty;
    public int QueriesPerMonth { get; set; }
    public int DatabaseConnections { get; set; }
    public string SupportLevel { get; set; } = string.Empty;
    public List<string> Features { get; set; } = new();
    public bool IsCurrent { get; set; }
    public bool IsPopular { get; set; }
}

public class UpgradeSubscriptionRequest
{
    public SubscriptionTier NewTier { get; set; }
    public string ReturnUrl { get; set; } = string.Empty;
}

public class CheckoutResponse
{
    public string CheckoutUrl { get; set; } = string.Empty;
}

public class SubscriptionResponse
{
    public SubscriptionTier CurrentTier { get; set; }
    public string TierName { get; set; } = string.Empty;
    public int QueriesPerMonth { get; set; }
    public int QueriesUsed { get; set; }
    public DateTime BillingCycleReset { get; set; }
}
