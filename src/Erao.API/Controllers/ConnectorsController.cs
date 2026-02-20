using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Erao.Application.Services;
using Erao.Core.DTOs.Common;
using Erao.Core.DTOs.Connector;
using ConnectionTestResult = Erao.Application.Services.ConnectionTestResult;

namespace Erao.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ConnectorsController : ControllerBase
{
    private readonly IConnectorService _connectorService;
    private readonly ILogger<ConnectorsController> _logger;

    public ConnectorsController(IConnectorService connectorService, ILogger<ConnectorsController> logger)
    {
        _connectorService = connectorService;
        _logger = logger;
    }

    [HttpGet("metadata")]
    [AllowAnonymous]
    public ActionResult<ApiResponse<List<ConnectorMetadataDto>>> GetMetadata()
    {
        var metadata = _connectorService.GetAllMetadata();
        return Ok(ApiResponse<List<ConnectorMetadataDto>>.SuccessResponse(metadata));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IEnumerable<AppConnectorDto>>>> GetAll()
    {
        try
        {
            var userId = GetUserId();
            var connectors = await _connectorService.GetConnectorsAsync(userId);
            return Ok(ApiResponse<IEnumerable<AppConnectorDto>>.SuccessResponse(connectors));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting connectors");
            return StatusCode(500, ApiResponse<IEnumerable<AppConnectorDto>>.ErrorResponse("An error occurred"));
        }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResponse<AppConnectorDto>>> GetById(Guid id)
    {
        try
        {
            var userId = GetUserId();
            var connector = await _connectorService.GetByIdAsync(id, userId);
            if (connector == null)
            {
                return NotFound(ApiResponse<AppConnectorDto>.ErrorResponse("Connector not found"));
            }
            return Ok(ApiResponse<AppConnectorDto>.SuccessResponse(connector));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting connector {Id}", id);
            return StatusCode(500, ApiResponse<AppConnectorDto>.ErrorResponse("An error occurred"));
        }
    }

    [HttpPost("test")]
    public async Task<ActionResult<ApiResponse<ConnectionTestResult>>> TestConnection([FromBody] CreateConnectorRequest request)
    {
        try
        {
            var result = await _connectorService.TestConnectionAsync(request.ConnectorType, request.Credentials);
            if (result.Success)
            {
                return Ok(ApiResponse<ConnectionTestResult>.SuccessResponse(result, result.Message));
            }
            return BadRequest(ApiResponse<ConnectionTestResult>.ErrorResponse(result.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing connector connection");
            return StatusCode(500, ApiResponse<ConnectionTestResult>.ErrorResponse("Connection test failed"));
        }
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<AppConnectorDto>>> Create([FromBody] CreateConnectorRequest request)
    {
        try
        {
            var userId = GetUserId();
            var connector = await _connectorService.CreateConnectorAsync(userId, request);
            return CreatedAtAction(nameof(GetById), new { id = connector.Id },
                ApiResponse<AppConnectorDto>.SuccessResponse(connector, "Connector created"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<AppConnectorDto>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating connector");
            return StatusCode(500, ApiResponse<AppConnectorDto>.ErrorResponse("An error occurred"));
        }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResponse<AppConnectorDto>>> Update(Guid id, [FromBody] UpdateConnectorRequest request)
    {
        try
        {
            var userId = GetUserId();
            var connector = await _connectorService.UpdateConnectorAsync(userId, id, request);
            return Ok(ApiResponse<AppConnectorDto>.SuccessResponse(connector, "Connector updated"));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<AppConnectorDto>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating connector {Id}", id);
            return StatusCode(500, ApiResponse<AppConnectorDto>.ErrorResponse("An error occurred"));
        }
    }

    [HttpPost("{id}/sync")]
    public async Task<ActionResult<ApiResponse<AppConnectorDto>>> Sync(Guid id)
    {
        try
        {
            var userId = GetUserId();
            var connector = await _connectorService.SyncConnectorAsync(id, userId);
            return Ok(ApiResponse<AppConnectorDto>.SuccessResponse(connector, "Sync completed"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<AppConnectorDto>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing connector {Id}", id);
            return StatusCode(500, ApiResponse<AppConnectorDto>.ErrorResponse("Sync failed: " + ex.Message));
        }
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResponse>> Delete(Guid id)
    {
        try
        {
            var userId = GetUserId();
            await _connectorService.DeleteConnectorAsync(userId, id);
            return Ok(ApiResponse.SuccessResponse("Connector deleted"));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting connector {Id}", id);
            return StatusCode(500, ApiResponse.ErrorResponse("An error occurred"));
        }
    }

    private Guid GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            throw new UnauthorizedAccessException("User not found");
        }
        return userId;
    }
}
