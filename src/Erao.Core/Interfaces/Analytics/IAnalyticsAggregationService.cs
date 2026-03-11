using Erao.Core.DTOs.Analytics;

namespace Erao.Core.Interfaces.Analytics;

public interface IAnalyticsAggregationService
{
    Task<AggregationResponse> AggregateAsync(Guid userId, AggregationRequest request);
}
