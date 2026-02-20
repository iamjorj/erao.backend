using Erao.Core.Entities;

namespace Erao.Core.Interfaces;

public interface IAppConnectorRepository : IRepository<AppConnector>
{
    Task<IEnumerable<AppConnector>> GetByUserIdAsync(Guid userId);
}
