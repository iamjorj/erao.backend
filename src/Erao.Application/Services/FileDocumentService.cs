using System.Text.Json;
using Erao.Core.DTOs.File;
using Erao.Core.Entities;
using Erao.Core.Enums;
using Erao.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Erao.Application.Services;

public class FileDocumentService : IFileDocumentService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IEnumerable<IFileParser> _fileParsers;
    private readonly IMinioService _minioService;
    private readonly ILogger<FileDocumentService> _logger;
    private readonly long _maxFileSizeBytes;

    private static readonly Dictionary<string, FileType> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".xlsx", FileType.Excel },
        { ".xls", FileType.Excel },
        { ".docx", FileType.Word },
        { ".doc", FileType.Word },
        { ".csv", FileType.Csv },
        { ".xml", FileType.Xml },
        { ".json", FileType.Json },
        { ".txt", FileType.Text }
    };

    public FileDocumentService(
        IUnitOfWork unitOfWork,
        IEnumerable<IFileParser> fileParsers,
        IMinioService minioService,
        IConfiguration configuration,
        ILogger<FileDocumentService> logger)
    {
        _unitOfWork = unitOfWork;
        _fileParsers = fileParsers;
        _minioService = minioService;
        _logger = logger;
        _maxFileSizeBytes = configuration.GetValue<long>("FileStorage:MaxFileSizeBytes", 100 * 1024 * 1024); // 100MB default
    }

    public async Task<FileUploadResponse> UploadFileAsync(Guid userId, IFormFile file, CancellationToken cancellationToken = default)
    {
        try
        {
            // Validate file
            if (file == null || file.Length == 0)
            {
                return new FileUploadResponse
                {
                    Success = false,
                    Message = "No file provided"
                };
            }

            if (file.Length > _maxFileSizeBytes)
            {
                return new FileUploadResponse
                {
                    Success = false,
                    Message = $"File size exceeds maximum allowed size of {_maxFileSizeBytes / (1024 * 1024)}MB"
                };
            }

            var extension = Path.GetExtension(file.FileName);
            if (!SupportedExtensions.TryGetValue(extension, out var fileType))
            {
                return new FileUploadResponse
                {
                    Success = false,
                    Message = $"Unsupported file type: {extension}. Supported types: {string.Join(", ", SupportedExtensions.Keys)}"
                };
            }

            // Generate unique filename
            var fileName = $"{Guid.NewGuid()}{extension}";

            // Upload to MinIO
            string objectName;
            using (var stream = file.OpenReadStream())
            {
                objectName = await _minioService.UploadFileAsync(stream, fileName, file.ContentType, userId);
            }

            // Create file document entity
            var fileDocument = new FileDocument
            {
                UserId = userId,
                FileName = fileName,
                OriginalFileName = file.FileName,
                FileType = fileType,
                FileSizeBytes = file.Length,
                StoragePath = objectName, // Store MinIO object name
                Status = FileProcessingStatus.Processing
            };

            await _unitOfWork.FileDocuments.AddAsync(fileDocument);
            await _unitOfWork.SaveChangesAsync();

            // Download from MinIO to parse
            using var fileStream = await _minioService.DownloadFileAsync(objectName);

            // Parse file
            var parser = _fileParsers.FirstOrDefault(p => p.CanParse(fileType));
            if (parser != null)
            {
                var parseResult = await parser.ParseAsync(fileStream, file.FileName, cancellationToken);

                if (parseResult.Success)
                {
                    fileDocument.ParsedContent = parseResult.ParsedContentJson;
                    fileDocument.SchemaInfo = parseResult.SchemaInfoJson;
                    fileDocument.RowCount = parseResult.RowCount;
                    fileDocument.Status = FileProcessingStatus.Completed;
                }
                else
                {
                    fileDocument.Status = FileProcessingStatus.Failed;
                    fileDocument.ErrorMessage = parseResult.ErrorMessage;
                }
            }
            else
            {
                // For unsupported parsing (like plain text), just store as-is
                if (fileType == FileType.Text || fileType == FileType.Json || fileType == FileType.Xml)
                {
                    using var reader = new StreamReader(fileStream);
                    var content = await reader.ReadToEndAsync(cancellationToken);
                    fileDocument.ParsedContent = content;
                    fileDocument.Status = FileProcessingStatus.Completed;
                }
                else
                {
                    fileDocument.Status = FileProcessingStatus.Failed;
                    fileDocument.ErrorMessage = $"No parser available for file type: {fileType}";
                }
            }

            await _unitOfWork.FileDocuments.UpdateAsync(fileDocument);
            await _unitOfWork.SaveChangesAsync();

            _logger.LogInformation("File {FileName} uploaded to MinIO successfully for user {UserId}", file.FileName, userId);

            return new FileUploadResponse
            {
                Success = true,
                Message = fileDocument.Status == FileProcessingStatus.Completed
                    ? "File uploaded and processed successfully"
                    : $"File uploaded but processing failed: {fileDocument.ErrorMessage}",
                File = MapToDto(fileDocument)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading file for user {UserId}", userId);
            return new FileUploadResponse
            {
                Success = false,
                Message = $"Error uploading file: {ex.Message}"
            };
        }
    }

    public async Task<FileListResponse> GetUserFilesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var files = await _unitOfWork.FileDocuments.GetByUserIdAsync(userId);

        return new FileListResponse
        {
            Files = files.Select(MapToDto).ToList(),
            TotalCount = files.Count()
        };
    }

    public async Task<FileDocumentDto?> GetFileByIdAsync(Guid userId, Guid fileId, CancellationToken cancellationToken = default)
    {
        var file = await _unitOfWork.FileDocuments.GetByUserIdAndFileIdAsync(userId, fileId);
        return file != null ? MapToDto(file) : null;
    }

    public async Task<FileSchemaResponse?> GetFileSchemaAsync(Guid userId, Guid fileId, CancellationToken cancellationToken = default)
    {
        var file = await _unitOfWork.FileDocuments.GetByUserIdAndFileIdAsync(userId, fileId);
        if (file == null)
            return null;

        var columns = new List<ColumnInfo>();
        if (!string.IsNullOrEmpty(file.SchemaInfo))
        {
            try
            {
                columns = JsonSerializer.Deserialize<List<ColumnInfo>>(file.SchemaInfo) ?? new List<ColumnInfo>();
            }
            catch
            {
                // If it's not a list, try to parse as object
            }
        }

        // Get sample data (first 5 rows)
        string? sampleData = null;
        if (!string.IsNullOrEmpty(file.ParsedContent))
        {
            try
            {
                var data = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(file.ParsedContent);
                if (data != null)
                {
                    var sample = data.Take(5).ToList();
                    sampleData = JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = true });
                }
            }
            catch
            {
                // For Word documents, take first 500 characters
                sampleData = file.ParsedContent.Length > 500
                    ? file.ParsedContent.Substring(0, 500) + "..."
                    : file.ParsedContent;
            }
        }

        return new FileSchemaResponse
        {
            FileId = file.Id,
            FileName = file.OriginalFileName,
            FileType = file.FileType,
            Columns = columns,
            TotalRows = file.RowCount ?? 0,
            SampleData = sampleData
        };
    }

    public async Task<FileContentResponse?> GetFileContentAsync(Guid userId, Guid fileId, int page = 1, int pageSize = 100, CancellationToken cancellationToken = default)
    {
        var file = await _unitOfWork.FileDocuments.GetByUserIdAndFileIdAsync(userId, fileId);
        if (file == null || string.IsNullOrEmpty(file.ParsedContent))
            return null;

        var columns = new List<string>();
        var data = new List<Dictionary<string, object?>>();

        try
        {
            var allData = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(file.ParsedContent);
            if (allData != null)
            {
                // Get columns from first row
                if (allData.Any())
                {
                    columns = allData.First().Keys.ToList();
                }

                // Paginate
                data = allData
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();
            }
        }
        catch
        {
            // Not tabular data
        }

        return new FileContentResponse
        {
            FileId = file.Id,
            FileName = file.OriginalFileName,
            FileType = file.FileType,
            Columns = columns,
            Data = data,
            TotalRows = file.RowCount ?? 0,
            PageSize = pageSize,
            CurrentPage = page
        };
    }

    public async Task<bool> DeleteFileAsync(Guid userId, Guid fileId, CancellationToken cancellationToken = default)
    {
        var file = await _unitOfWork.FileDocuments.GetByUserIdAndFileIdAsync(userId, fileId);
        if (file == null)
            return false;

        // Delete from MinIO
        if (!string.IsNullOrEmpty(file.StoragePath))
        {
            try
            {
                await _minioService.DeleteFileAsync(file.StoragePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete file from MinIO: {ObjectName}", file.StoragePath);
            }
        }

        await _unitOfWork.FileDocuments.DeleteAsync(file);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("File {FileId} deleted for user {UserId}", fileId, userId);
        return true;
    }

    public async Task<string?> GetParsedContentForQueryAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var file = await _unitOfWork.FileDocuments.GetByIdAsync(fileId);
        return file?.ParsedContent;
    }

    private FileDocumentDto MapToDto(FileDocument file)
    {
        var columns = new List<string>();
        if (!string.IsNullOrEmpty(file.SchemaInfo))
        {
            try
            {
                var schemaColumns = JsonSerializer.Deserialize<List<ColumnInfo>>(file.SchemaInfo);
                if (schemaColumns != null)
                {
                    columns = schemaColumns.Select(c => c.Name).ToList();
                }
            }
            catch { }
        }

        return new FileDocumentDto
        {
            Id = file.Id,
            FileName = file.FileName,
            OriginalFileName = file.OriginalFileName,
            FileType = file.FileType,
            FileSizeBytes = file.FileSizeBytes,
            RowCount = file.RowCount,
            Status = file.Status,
            ErrorMessage = file.ErrorMessage,
            Columns = columns,
            CreatedAt = file.CreatedAt,
            UpdatedAt = file.UpdatedAt
        };
    }
}
