namespace Erao.Core.DTOs.Usage;

public class UsageDto
{
    public int QueriesUsedThisMonth { get; set; }
    public int QueryLimitPerMonth { get; set; }
    public double PercentageUsed { get; set; }
    public int DaysUntilReset { get; set; }
    public DateTime BillingCycleStart { get; set; }
    public DateTime BillingCycleEnd { get; set; }
}
