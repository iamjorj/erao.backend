using Erao.Core.Entities;

namespace Erao.Core.Interfaces;

public interface IDatabaseConnectionRepository : IRepository<DatabaseConnection>
{
    Task<IEnumerable<DatabaseConnection>> GetByUserIdAsync(Guid userId);
    Task<DatabaseConnection?> GetByIdWithUserAsync(Guid id);
}
