using Erao.Core.DTOs.File;
using Microsoft.AspNetCore.Http;

namespace Erao.Core.Interfaces;

public interface IFileDocumentService
{
    Task<FileUploadResponse> UploadFileAsync(Guid userId, IFormFile file, CancellationToken cancellationToken = default);
    Task<FileListResponse> GetUserFilesAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<FileDocumentDto?> GetFileByIdAsync(Guid userId, Guid fileId, CancellationToken cancellationToken = default);
    Task<FileSchemaResponse?> GetFileSchemaAsync(Guid userId, Guid fileId, CancellationToken cancellationToken = default);
    Task<FileContentResponse?> GetFileContentAsync(Guid userId, Guid fileId, int page = 1, int pageSize = 100, CancellationToken cancellationToken = default);
    Task<bool> DeleteFileAsync(Guid userId, Guid fileId, CancellationToken cancellationToken = default);
    Task<string?> GetParsedContentForQueryAsync(Guid fileId, CancellationToken cancellationToken = default);
}
