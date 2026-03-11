using Erao.Core.DTOs.Analytics;

namespace Erao.Core.Interfaces.Analytics;

public interface IAnalyticsQueryService
{
    Task<AnalyticsQueryResponse> QueryAsync(Guid userId, AnalyticsQueryRequest request);
}
