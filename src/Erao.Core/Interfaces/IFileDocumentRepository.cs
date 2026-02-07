using Erao.Core.Entities;

namespace Erao.Core.Interfaces;

public interface IFileDocumentRepository : IRepository<FileDocument>
{
    Task<IEnumerable<FileDocument>> GetByUserIdAsync(Guid userId);
    Task<FileDocument?> GetByUserIdAndFileIdAsync(Guid userId, Guid fileId);
}
