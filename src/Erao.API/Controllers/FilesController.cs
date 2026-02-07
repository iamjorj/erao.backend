using System.Security.Claims;
using Erao.Core.DTOs.File;
using Erao.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Erao.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FilesController : ControllerBase
{
    private readonly IFileDocumentService _fileService;
    private readonly ILogger<FilesController> _logger;

    public FilesController(IFileDocumentService fileService, ILogger<FilesController> logger)
    {
        _fileService = fileService;
        _logger = logger;
    }

    /// <summary>
    /// Upload a file (Excel, Word, CSV, etc.)
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100MB limit
    [ProducesResponseType(typeof(FileUploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<FileUploadResponse>> UploadFile(IFormFile file, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _fileService.UploadFileAsync(userId.Value, file, cancellationToken);

        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>
    /// Get all files for the current user
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(FileListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<FileListResponse>> GetFiles(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _fileService.GetUserFilesAsync(userId.Value, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Get a specific file by ID
    /// </summary>
    [HttpGet("{fileId:guid}")]
    [ProducesResponseType(typeof(FileDocumentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<FileDocumentDto>> GetFile(Guid fileId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _fileService.GetFileByIdAsync(userId.Value, fileId, cancellationToken);

        if (result == null)
            return NotFound(new { message = "File not found" });

        return Ok(result);
    }

    /// <summary>
    /// Get file schema (columns, data types, sample data)
    /// </summary>
    [HttpGet("{fileId:guid}/schema")]
    [ProducesResponseType(typeof(FileSchemaResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<FileSchemaResponse>> GetFileSchema(Guid fileId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _fileService.GetFileSchemaAsync(userId.Value, fileId, cancellationToken);

        if (result == null)
            return NotFound(new { message = "File not found" });

        return Ok(result);
    }

    /// <summary>
    /// Get file content with pagination
    /// </summary>
    [HttpGet("{fileId:guid}/content")]
    [ProducesResponseType(typeof(FileContentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<FileContentResponse>> GetFileContent(
        Guid fileId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 10;
        if (pageSize > 1000) pageSize = 1000;

        var result = await _fileService.GetFileContentAsync(userId.Value, fileId, page, pageSize, cancellationToken);

        if (result == null)
            return NotFound(new { message = "File not found or has no content" });

        return Ok(result);
    }

    /// <summary>
    /// Delete a file
    /// </summary>
    [HttpDelete("{fileId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteFile(Guid fileId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _fileService.DeleteFileAsync(userId.Value, fileId, cancellationToken);

        if (!result)
            return NotFound(new { message = "File not found" });

        return NoContent();
    }

    private Guid? GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return null;
        return userId;
    }
}
