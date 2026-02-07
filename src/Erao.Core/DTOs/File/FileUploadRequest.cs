using Microsoft.AspNetCore.Http;

namespace Erao.Core.DTOs.File;

public class FileUploadRequest
{
    public IFormFile File { get; set; } = null!;
    public string? Description { get; set; }
}
