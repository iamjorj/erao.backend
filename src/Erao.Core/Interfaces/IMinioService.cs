namespace Erao.Core.Interfaces;

public interface IMinioService
{
    Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, Guid userId);
    Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, Guid userId, string prefix);
    Task<Stream> DownloadFileAsync(string objectName);
    Task DeleteFileAsync(string objectName);
    Task<bool> FileExistsAsync(string objectName);
    string GetObjectName(Guid userId, string fileName);
}
