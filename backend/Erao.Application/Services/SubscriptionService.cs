using Erao.Core.DTOs.Subscription;
using Erao.Core.Enums;
using Erao.Core.Helpers;
using Erao.Core.Interfaces;

namespace Erao.Application.Services;

public interface ISubscriptionService
{
    Task<IEnumerable<SubscriptionPlanDto>> GetPlansAsync(Guid userId);
    Task<SubscriptionResponse> GetCurrentSubscriptionAsync(Guid userId);
    Task<SubscriptionResponse> DowngradeToFreeAsync(Guid userId);
    Task<CheckoutResponse> CreateUpgradeCheckoutAsync(Guid userId, SubscriptionTier newTier, string returnUrl);
}

public class SubscriptionService : ISubscriptionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDodoPaymentsService _dodoPayments;

    public SubscriptionService(IUnitOfWork unitOfWork, IDodoPaymentsService dodoPayments)
    {
        _unitOfWork = unitOfWork;
        _dodoPayments = dodoPayments;
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

    public async Task<CheckoutResponse> CreateUpgradeCheckoutAsync(Guid userId, SubscriptionTier newTier, string returnUrl)
    {
        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        if (user == null)
            throw new InvalidOperationException("User not found");

        if (!Enum.IsDefined(typeof(SubscriptionTier), newTier))
            throw new InvalidOperationException("Invalid subscription tier");

        if (newTier == user.SubscriptionTier)
            throw new InvalidOperationException("Already on this plan");

        if (newTier == SubscriptionTier.Starter)
            throw new InvalidOperationException("Use the downgrade endpoint to switch to the free plan");

        var checkoutUrl = await _dodoPayments.CreateCheckoutSessionAsync(
            userId,
            user.Email,
            $"{user.FirstName} {user.LastName}".Trim(),
            newTier,
            returnUrl);

        return new CheckoutResponse { CheckoutUrl = checkoutUrl };
    }

    public async Task<SubscriptionResponse> DowngradeToFreeAsync(Guid userId)
    {
        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        if (user == null)
            throw new InvalidOperationException("User not found");

        if (user.SubscriptionTier == SubscriptionTier.Starter)
            throw new InvalidOperationException("Already on the free plan");

        // Check database connection limits
        var newDbLimit = SubscriptionLimits.GetDatabaseConnectionLimit(SubscriptionTier.Starter);
        var currentDbCount = (await _unitOfWork.DatabaseConnections.GetByUserIdAsync(userId)).Count();
        if (currentDbCount > newDbLimit)
        {
            throw new InvalidOperationException(
                $"Cannot downgrade: You have {currentDbCount} database connections but Free plan only allows {newDbLimit}. Please remove some databases first.");
        }

        user.SubscriptionTier = SubscriptionTier.Starter;
        user.QueryLimitPerMonth = SubscriptionLimits.GetQueryLimit(SubscriptionTier.Starter);
        user.DodoSubscriptionId = null;

        await _unitOfWork.Users.UpdateAsync(user);
        await _unitOfWork.SaveChangesAsync();

        return new SubscriptionResponse
        {
            CurrentTier = user.SubscriptionTier,
            TierName = SubscriptionLimits.GetDisplayName(user.SubscriptionTier),
            QueriesPerMonth = user.QueryLimitPerMonth,
            QueriesUsed = user.QueriesUsedThisMonth,
            BillingCycleReset = user.BillingCycleReset
        };
    }
}
