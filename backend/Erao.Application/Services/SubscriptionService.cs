using Erao.Core.DTOs.Subscription;
using Erao.Core.Enums;
using Erao.Core.Helpers;
using Erao.Core.Interfaces;

namespace Erao.Application.Services;

public interface ISubscriptionService
{
    Task<IEnumerable<SubscriptionPlanDto>> GetPlansAsync(Guid userId);
    Task<SubscriptionResponse> GetCurrentSubscriptionAsync(Guid userId);
    Task<SubscriptionResponse> UpgradeSubscriptionAsync(Guid userId, SubscriptionTier newTier);
}

public class SubscriptionService : ISubscriptionService
{
    private readonly IUnitOfWork _unitOfWork;

    public SubscriptionService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    /// <summary>
    /// Generate subscription plans from the centralized SubscriptionLimits helper.
    /// </summary>
    private static List<SubscriptionPlanDto> GetAllPlans()
    {
        return Enum.GetValues<SubscriptionTier>()
            .Select(tier => new SubscriptionPlanDto
            {
                Tier = tier,
                Name = SubscriptionLimits.GetDisplayName(tier),
                Price = SubscriptionLimits.GetPrice(tier),
                Description = SubscriptionLimits.GetDescription(tier),
                QueriesPerMonth = SubscriptionLimits.GetQueryLimit(tier),
                DatabaseConnections = SubscriptionLimits.GetDatabaseConnectionLimit(tier),
                SupportLevel = SubscriptionLimits.GetSupportLevel(tier),
                Features = SubscriptionLimits.GetFeatures(tier),
                IsPopular = SubscriptionLimits.IsPopular(tier)
            })
            .ToList();
    }

    public async Task<IEnumerable<SubscriptionPlanDto>> GetPlansAsync(Guid userId)
    {
        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        var currentTier = user?.SubscriptionTier ?? SubscriptionTier.Starter;

        var plans = GetAllPlans();
        foreach (var plan in plans)
        {
            plan.IsCurrent = plan.Tier == currentTier;
        }
        return plans;
    }

    public async Task<SubscriptionResponse> GetCurrentSubscriptionAsync(Guid userId)
    {
        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        if (user == null)
        {
            throw new InvalidOperationException("User not found");
        }

        return new SubscriptionResponse
        {
            CurrentTier = user.SubscriptionTier,
            TierName = SubscriptionLimits.GetDisplayName(user.SubscriptionTier),
            QueriesPerMonth = user.QueryLimitPerMonth,
            QueriesUsed = user.QueriesUsedThisMonth,
            BillingCycleReset = user.BillingCycleReset
        };
    }

    public async Task<SubscriptionResponse> UpgradeSubscriptionAsync(Guid userId, SubscriptionTier newTier)
    {
        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        if (user == null)
        {
            throw new InvalidOperationException("User not found");
        }

        // Validate tier exists
        if (!Enum.IsDefined(typeof(SubscriptionTier), newTier))
        {
            throw new InvalidOperationException("Invalid subscription tier");
        }

        // Can't change to same tier
        if (newTier == user.SubscriptionTier)
        {
            throw new InvalidOperationException("Already on this plan");
        }

        // Check if downgrading - verify user doesn't exceed new limits
        if (newTier < user.SubscriptionTier)
        {
            var newDbLimit = SubscriptionLimits.GetDatabaseConnectionLimit(newTier);
            if (newDbLimit != -1) // -1 means unlimited
            {
                var currentDbCount = (await _unitOfWork.DatabaseConnections.GetByUserIdAsync(userId)).Count();
                if (currentDbCount > newDbLimit)
                {
                    throw new InvalidOperationException(
                        $"Cannot downgrade: You have {currentDbCount} database connections but {SubscriptionLimits.GetDisplayName(newTier)} plan only allows {newDbLimit}. Please remove some databases first.");
                }
            }
        }

        user.SubscriptionTier = newTier;
        user.QueryLimitPerMonth = SubscriptionLimits.GetQueryLimit(newTier);
        // Keep the current billing cycle, just update the limit

        await _unitOfWork.Users.UpdateAsync(user);
        await _unitOfWork.SaveChangesAsync();

        return new SubscriptionResponse
        {
            CurrentTier = user.SubscriptionTier,
            TierName = SubscriptionLimits.GetDisplayName(newTier),
            QueriesPerMonth = user.QueryLimitPerMonth,
            QueriesUsed = user.QueriesUsedThisMonth,
            BillingCycleReset = user.BillingCycleReset
        };
    }
}
