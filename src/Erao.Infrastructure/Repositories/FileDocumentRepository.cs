using Microsoft.EntityFrameworkCore;
using Erao.Core.Entities;
using Erao.Core.Interfaces;
using Erao.Infrastructure.Data;

namespace Erao.Infrastructure.Repositories;

public class FileDocumentRepository : Repository<FileDocument>, IFileDocumentRepository
{
    public FileDocumentRepository(EraoDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<FileDocument>> GetByUserIdAsync(Guid userId)
    {
        return await _dbSet
            .Where(f => f.UserId == userId)
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync();
    }

    public async Task<FileDocument?> GetByUserIdAndFileIdAsync(Guid userId, Guid fileId)
    {
        return await _dbSet
            .FirstOrDefaultAsync(f => f.UserId == userId && f.Id == fileId);
    }
}
