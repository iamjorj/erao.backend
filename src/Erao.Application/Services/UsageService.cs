using AutoMapper;
using Erao.Core.DTOs.Usage;
using Erao.Core.Interfaces;

namespace Erao.Application.Services;

public interface IUsageService
{
    Task<UsageDto> GetCurrentUsageAsync(Guid userId);
    Task<IEnumerable<UsageLogDto>> GetUsageHistoryAsync(Guid userId, DateTime? from = null, DateTime? to = null);
}

public class UsageService : IUsageService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMapper _mapper;

    public UsageService(IUnitOfWork unitOfWork, IMapper mapper)
    {
        _unitOfWork = unitOfWork;
        _mapper = mapper;
    }

    public async Task<UsageDto> GetCurrentUsageAsync(Guid userId)
    {
        var user = await _unitOfWork.Users.GetByIdAsync(userId);
        if (user == null)
        {
            throw new InvalidOperationException("User not found");
        }

        // Reset billing cycle if needed
        if (DateTime.UtcNow >= user.BillingCycleReset)
        {
            user.QueriesUsedThisMonth = 0;
            user.BillingCycleReset = DateTime.UtcNow.AddMonths(1);
            await _unitOfWork.Users.UpdateAsync(user);
            await _unitOfWork.SaveChangesAsync();
        }

        var billingCycleStart = user.BillingCycleReset.AddMonths(-1);
        var billingCycleEnd = user.BillingCycleReset;
        var daysUntilReset = (int)Math.Ceiling((billingCycleEnd - DateTime.UtcNow).TotalDays);
        var percentageUsed = user.QueryLimitPerMonth > 0
            ? Math.Round((double)user.QueriesUsedThisMonth / user.QueryLimitPerMonth * 100, 1)
            : 0;

        return new UsageDto
        {
            QueriesUsedThisMonth = user.QueriesUsedThisMonth,
            QueryLimitPerMonth = user.QueryLimitPerMonth,
            PercentageUsed = percentageUsed,
            DaysUntilReset = Math.Max(0, daysUntilReset),
            BillingCycleStart = billingCycleStart,
            BillingCycleEnd = billingCycleEnd
        };
    }

    public async Task<IEnumerable<UsageLogDto>> GetUsageHistoryAsync(Guid userId, DateTime? from = null, DateTime? to = null)
    {
        var logs = await _unitOfWork.UsageLogs.GetByUserIdAsync(userId, from, to);
        return _mapper.Map<IEnumerable<UsageLogDto>>(logs);
    }
}
