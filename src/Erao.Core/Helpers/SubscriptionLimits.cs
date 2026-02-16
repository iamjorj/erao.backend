using Erao.Core.Enums;

namespace Erao.Core.Helpers;

/// <summary>
/// Centralized subscription limits configuration.
/// All tier limits should be defined here to ensure consistency.
/// </summary>
public static class SubscriptionLimits
{
    /// <summary>
    /// Get the monthly query limit for a subscription tier.
    /// </summary>
    public static int GetQueryLimit(SubscriptionTier tier) => tier switch
    {
        SubscriptionTier.Starter => 25,
        SubscriptionTier.Professional => 150,
        SubscriptionTier.Enterprise => -1, // Unlimited
        _ => 25
    };

    /// <summary>
    /// Get the database connection limit for a subscription tier.
    /// Returns -1 for unlimited.
    /// </summary>
    public static int GetDatabaseConnectionLimit(SubscriptionTier tier) => tier switch
    {
        SubscriptionTier.Starter => 1,
        SubscriptionTier.Professional => 5,
        SubscriptionTier.Enterprise => -1, // Unlimited
        _ => 1
    };

    /// <summary>
    /// Get the price for a subscription tier.
    /// </summary>
    public static decimal GetPrice(SubscriptionTier tier) => tier switch
    {
        SubscriptionTier.Starter => 0m,
        SubscriptionTier.Professional => 49m,
        SubscriptionTier.Enterprise => 299m,
        _ => 0m
    };

    /// <summary>
    /// Get the display name for a subscription tier.
    /// </summary>
    public static string GetDisplayName(SubscriptionTier tier) => tier switch
    {
        SubscriptionTier.Starter => "Free",
        SubscriptionTier.Professional => "Pro",
        SubscriptionTier.Enterprise => "Enterprise",
        _ => "Free"
    };

    /// <summary>
    /// Get the description for a subscription tier.
    /// </summary>
    public static string GetDescription(SubscriptionTier tier) => tier switch
    {
        SubscriptionTier.Starter => "For individuals getting started",
        SubscriptionTier.Professional => "For power users",
        SubscriptionTier.Enterprise => "For large organizations",
        _ => "For individuals getting started"
    };

    /// <summary>
    /// Get the support level for a subscription tier.
    /// </summary>
    public static string GetSupportLevel(SubscriptionTier tier) => tier switch
    {
        SubscriptionTier.Starter => "Community",
        SubscriptionTier.Professional => "Email",
        SubscriptionTier.Enterprise => "Priority",
        _ => "Community"
    };

    /// <summary>
    /// Get the features list for a subscription tier.
    /// </summary>
    public static List<string> GetFeatures(SubscriptionTier tier) => tier switch
    {
        SubscriptionTier.Starter => new List<string>
        {
            "1 database connection",
            "25 queries/month"
        },
        SubscriptionTier.Professional => new List<string>
        {
            "5 database connections",
            "150 queries/month"
        },
        SubscriptionTier.Enterprise => new List<string>
        {
            "Unlimited database connections",
            "Unlimited queries"
        },
        _ => new List<string> { "1 database connection", "25 queries/month" }
    };

    /// <summary>
    /// Check if a tier is the most popular/recommended tier.
    /// </summary>
    public static bool IsPopular(SubscriptionTier tier) => tier == SubscriptionTier.Professional;
}
