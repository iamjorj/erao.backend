using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Erao.Application.Services;
using Erao.Core.DTOs.Common;
using Erao.Core.DTOs.Database;
using System.Security.Claims;

namespace Erao.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DatabasesController : ControllerBase
{
    private readonly IDatabaseConnectionService _databaseService;
    private readonly ILogger<DatabasesController> _logger;

    public DatabasesController(IDatabaseConnectionService databaseService, ILogger<DatabasesController> logger)
    {
        _databaseService = databaseService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IEnumerable<DatabaseConnectionDto>>>> GetAll()
    {
        try
        {
            var userId = GetUserId();
            var connections = await _databaseService.GetUserConnectionsAsync(userId);
            return Ok(ApiResponse<IEnumerable<DatabaseConnectionDto>>.SuccessResponse(connections));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting database connections");
            return StatusCode(500, ApiResponse<IEnumerable<DatabaseConnectionDto>>.ErrorResponse("An error occurred"));
        }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResponse<DatabaseConnectionDto>>> GetById(Guid id)
    {
        try
        {
            var userId = GetUserId();
            var connection = await _databaseService.GetByIdAsync(id, userId);
            if (connection == null)
            {
                return NotFound(ApiResponse<DatabaseConnectionDto>.ErrorResponse("Database connection not found"));
            }
            return Ok(ApiResponse<DatabaseConnectionDto>.SuccessResponse(connection));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting database connection {Id}", id);
            return StatusCode(500, ApiResponse<DatabaseConnectionDto>.ErrorResponse("An error occurred"));
        }
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<DatabaseConnectionDto>>> Create([FromBody] CreateDatabaseConnectionRequest request)
    {
        try
        {
            var userId = GetUserId();
            var connection = await _databaseService.CreateAsync(userId, request);
            return CreatedAtAction(nameof(GetById), new { id = connection.Id },
                ApiResponse<DatabaseConnectionDto>.SuccessResponse(connection, "Database connection created"));
        }
        catch (InvalidOperationException ex)
        {
            // Handle limit exceeded or validation errors
            return BadRequest(ApiResponse<DatabaseConnectionDto>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating database connection");
            return StatusCode(500, ApiResponse<DatabaseConnectionDto>.ErrorResponse("An error occurred"));
        }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResponse<DatabaseConnectionDto>>> Update(Guid id, [FromBody] UpdateDatabaseConnectionRequest request)
    {
        try
        {
            var userId = GetUserId();
            var connection = await _databaseService.UpdateAsync(id, userId, request);
            return Ok(ApiResponse<DatabaseConnectionDto>.SuccessResponse(connection, "Database connection updated"));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<DatabaseConnectionDto>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating database connection {Id}", id);
            return StatusCode(500, ApiResponse<DatabaseConnectionDto>.ErrorResponse("An error occurred"));
        }
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResponse>> Delete(Guid id)
    {
        try
        {
            var userId = GetUserId();
            await _databaseService.DeleteAsync(id, userId);
            return Ok(ApiResponse.SuccessResponse("Database connection deleted"));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting database connection {Id}", id);
            return StatusCode(500, ApiResponse.ErrorResponse("An error occurred"));
        }
    }

    [HttpPost("{id}/test")]
    public async Task<ActionResult<ApiResponse<bool>>> TestConnection(Guid id)
    {
        try
        {
            var userId = GetUserId();
            var success = await _databaseService.TestConnectionAsync(id, userId);
            return Ok(ApiResponse<bool>.SuccessResponse(success,
                success ? "Connection successful" : "Connection failed"));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<bool>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing database connection {Id}", id);
            return StatusCode(500, ApiResponse<bool>.ErrorResponse("An error occurred"));
        }
    }

    [HttpGet("{id}/schema")]
    public async Task<ActionResult<ApiResponse<SchemaResponse>>> GetSchema(Guid id)
    {
        try
        {
            var userId = GetUserId();
            var schema = await _databaseService.GetSchemaAsync(id, userId);
            return Ok(ApiResponse<SchemaResponse>.SuccessResponse(schema));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<SchemaResponse>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting schema for database connection {Id}", id);
            return StatusCode(500, ApiResponse<SchemaResponse>.ErrorResponse("An error occurred"));
        }
    }

    private Guid GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            throw new UnauthorizedAccessException("Invalid user");
        }
        return userId;
    }
}
