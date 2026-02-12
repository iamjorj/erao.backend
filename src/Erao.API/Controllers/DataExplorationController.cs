using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Erao.Core.DTOs.Common;
using Erao.Core.DTOs.DataExploration;
using Erao.Core.DTOs.SmartFeatures;
using Erao.Core.Interfaces;
using System.Security.Claims;

namespace Erao.API.Controllers;

[ApiController]
[Route("api/data-exploration")]
[Authorize]
public class DataExplorationController : ControllerBase
{
    private readonly IDataExplorationService _explorationService;
    private readonly ILogger<DataExplorationController> _logger;

    public DataExplorationController(
        IDataExplorationService explorationService,
        ILogger<DataExplorationController> logger)
    {
        _explorationService = explorationService;
        _logger = logger;
    }

    #region Database Preview & Stats

    /// <summary>
    /// Get preview data from a database table
    /// </summary>
    [HttpGet("databases/{id}/preview")]
    public async Task<ActionResult<ApiResponse<PreviewResultDto>>> GetDatabaseTablePreview(
        Guid id,
        [FromQuery] string table,
        [FromQuery] int limit = 50)
    {
        try
        {
            var userId = GetUserId();
            var result = await _explorationService.GetDatabaseTablePreviewAsync(id, userId, table, limit);
            return Ok(ApiResponse<PreviewResultDto>.SuccessResponse(result));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<PreviewResultDto>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting preview for database {Id}, table {Table}", id, table);
            return StatusCode(500, ApiResponse<PreviewResultDto>.ErrorResponse("An error occurred"));
        }
    }

    /// <summary>
    /// Get column statistics for a database table column
    /// </summary>
    [HttpGet("databases/{id}/column-stats")]
    public async Task<ActionResult<ApiResponse<ColumnStatsDto>>> GetDatabaseColumnStats(
        Guid id,
        [FromQuery] string table,
        [FromQuery] string column)
    {
        try
        {
            var userId = GetUserId();
            var result = await _explorationService.GetDatabaseColumnStatsAsync(id, userId, table, column);
            return Ok(ApiResponse<ColumnStatsDto>.SuccessResponse(result));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<ColumnStatsDto>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting column stats for database {Id}, table {Table}, column {Column}", id, table, column);
            return StatusCode(500, ApiResponse<ColumnStatsDto>.ErrorResponse("An error occurred"));
        }
    }

    #endregion

    #region File Preview & Stats

    /// <summary>
    /// Get preview data from a file
    /// </summary>
    [HttpGet("files/{id}/preview")]
    public async Task<ActionResult<ApiResponse<PreviewResultDto>>> GetFilePreview(
        Guid id,
        [FromQuery] int limit = 50)
    {
        try
        {
            var userId = GetUserId();
            var result = await _explorationService.GetFilePreviewAsync(id, userId, limit);
            return Ok(ApiResponse<PreviewResultDto>.SuccessResponse(result));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<PreviewResultDto>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting preview for file {Id}", id);
            return StatusCode(500, ApiResponse<PreviewResultDto>.ErrorResponse("An error occurred"));
        }
    }

    /// <summary>
    /// Get column statistics for a file column
    /// </summary>
    [HttpGet("files/{id}/column-stats")]
    public async Task<ActionResult<ApiResponse<ColumnStatsDto>>> GetFileColumnStats(
        Guid id,
        [FromQuery] string column)
    {
        try
        {
            var userId = GetUserId();
            var result = await _explorationService.GetFileColumnStatsAsync(id, userId, column);
            return Ok(ApiResponse<ColumnStatsDto>.SuccessResponse(result));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<ColumnStatsDto>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting column stats for file {Id}, column {Column}", id, column);
            return StatusCode(500, ApiResponse<ColumnStatsDto>.ErrorResponse("An error occurred"));
        }
    }

    #endregion

    #region Smart Features - Suggestions

    /// <summary>
    /// Get AI-powered query suggestions for a database
    /// </summary>
    [HttpGet("databases/{id}/suggestions")]
    public async Task<ActionResult<ApiResponse<List<SuggestedQueryDto>>>> GetDatabaseSuggestions(
        Guid id,
        [FromQuery] string? table = null)
    {
        try
        {
            var userId = GetUserId();
            var result = await _explorationService.GetDatabaseSuggestionsAsync(id, userId, table);
            return Ok(ApiResponse<List<SuggestedQueryDto>>.SuccessResponse(result));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<List<SuggestedQueryDto>>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting suggestions for database {Id}", id);
            return StatusCode(500, ApiResponse<List<SuggestedQueryDto>>.ErrorResponse("An error occurred"));
        }
    }

    /// <summary>
    /// Get AI-powered query suggestions for a file
    /// </summary>
    [HttpGet("files/{id}/suggestions")]
    public async Task<ActionResult<ApiResponse<List<SuggestedQueryDto>>>> GetFileSuggestions(Guid id)
    {
        try
        {
            var userId = GetUserId();
            var result = await _explorationService.GetFileSuggestionsAsync(id, userId);
            return Ok(ApiResponse<List<SuggestedQueryDto>>.SuccessResponse(result));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<List<SuggestedQueryDto>>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting suggestions for file {Id}", id);
            return StatusCode(500, ApiResponse<List<SuggestedQueryDto>>.ErrorResponse("An error occurred"));
        }
    }

    #endregion

    #region Smart Features - Insights

    /// <summary>
    /// Get auto-detected insights for a database table
    /// </summary>
    [HttpPost("databases/{id}/insights")]
    public async Task<ActionResult<ApiResponse<List<InsightDto>>>> GetDatabaseInsights(
        Guid id,
        [FromBody] InsightsRequest request)
    {
        try
        {
            var userId = GetUserId();
            var result = await _explorationService.GetDatabaseInsightsAsync(id, userId, request.TableName);
            return Ok(ApiResponse<List<InsightDto>>.SuccessResponse(result));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<List<InsightDto>>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting insights for database {Id}, table {Table}", id, request.TableName);
            return StatusCode(500, ApiResponse<List<InsightDto>>.ErrorResponse("An error occurred"));
        }
    }

    /// <summary>
    /// Get auto-detected insights for a file
    /// </summary>
    [HttpPost("files/{id}/insights")]
    public async Task<ActionResult<ApiResponse<List<InsightDto>>>> GetFileInsights(Guid id)
    {
        try
        {
            var userId = GetUserId();
            var result = await _explorationService.GetFileInsightsAsync(id, userId);
            return Ok(ApiResponse<List<InsightDto>>.SuccessResponse(result));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<List<InsightDto>>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting insights for file {Id}", id);
            return StatusCode(500, ApiResponse<List<InsightDto>>.ErrorResponse("An error occurred"));
        }
    }

    #endregion

    #region AI-Powered Visualization

    /// <summary>
    /// Analyze query results and get AI-powered visualization recommendations
    /// </summary>
    [HttpPost("analyze-visualization")]
    public async Task<ActionResult<ApiResponse<VisualizationRecommendationDto>>> AnalyzeForVisualization(
        [FromBody] AnalyzeVisualizationRequest request)
    {
        try
        {
            if (request.Columns == null || request.Columns.Count == 0)
            {
                return BadRequest(ApiResponse<VisualizationRecommendationDto>.ErrorResponse("Columns are required"));
            }

            if (request.SampleRows == null || request.SampleRows.Count == 0)
            {
                return BadRequest(ApiResponse<VisualizationRecommendationDto>.ErrorResponse("Sample rows are required"));
            }

            var result = await _explorationService.AnalyzeForVisualizationAsync(request);
            return Ok(ApiResponse<VisualizationRecommendationDto>.SuccessResponse(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analyzing data for visualization");
            return StatusCode(500, ApiResponse<VisualizationRecommendationDto>.ErrorResponse("An error occurred while analyzing data"));
        }
    }

    #endregion

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
