using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Erao.Core.DTOs.Analytics;
using Erao.Core.DTOs.Common;
using Erao.Core.Interfaces.Analytics;

namespace Erao.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AnalyticsController : ControllerBase
{
    private readonly IAnalyticsIngestionService _ingestionService;
    private readonly IAnalyticsQueryService _queryService;
    private readonly IAnalyticsMetricsService _metricsService;
    private readonly IAnalyticsAggregationService _aggregationService;
    private readonly ILogger<AnalyticsController> _logger;

    public AnalyticsController(
        IAnalyticsIngestionService ingestionService,
        IAnalyticsQueryService queryService,
        IAnalyticsMetricsService metricsService,
        IAnalyticsAggregationService aggregationService,
        ILogger<AnalyticsController> logger)
    {
        _ingestionService = ingestionService;
        _queryService = queryService;
        _metricsService = metricsService;
        _aggregationService = aggregationService;
        _logger = logger;
    }

    // ──────────────────────────────────────────────
    // DATASET INGESTION
    // ──────────────────────────────────────────────

    /// <summary>
    /// Ingest records into an analytics dataset. Creates the dataset if it doesn't exist.
    /// </summary>
    [HttpPost("datasets/ingest")]
    [ProducesResponseType(typeof(ApiResponse<DatasetIngestionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<DatasetIngestionResponse>), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<DatasetIngestionResponse>>> IngestDataset(
        [FromBody] DatasetIngestionRequest request)
    {
        try
        {
            var userId = GetUserId();
            var result = await _ingestionService.IngestAsync(userId, request);
            return Ok(ApiResponse<DatasetIngestionResponse>.SuccessResponse(result, "Dataset ingested successfully"));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<DatasetIngestionResponse>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ingesting dataset");
            return StatusCode(500, ApiResponse<DatasetIngestionResponse>.ErrorResponse("An error occurred during ingestion"));
        }
    }

    /// <summary>
    /// List all analytics datasets for the current user.
    /// </summary>
    [HttpGet("datasets")]
    [ProducesResponseType(typeof(ApiResponse<IEnumerable<DatasetDto>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IEnumerable<DatasetDto>>>> GetDatasets()
    {
        try
        {
            var userId = GetUserId();
            var datasets = await _ingestionService.GetDatasetsAsync(userId);
            return Ok(ApiResponse<IEnumerable<DatasetDto>>.SuccessResponse(datasets));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting datasets");
            return StatusCode(500, ApiResponse<IEnumerable<DatasetDto>>.ErrorResponse("An error occurred"));
        }
    }

    /// <summary>
    /// Get a specific dataset by name.
    /// </summary>
    [HttpGet("datasets/{datasetName}")]
    [ProducesResponseType(typeof(ApiResponse<DatasetDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<DatasetDto>), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<DatasetDto>>> GetDataset(string datasetName)
    {
        try
        {
            var userId = GetUserId();
            var dataset = await _ingestionService.GetDatasetAsync(userId, datasetName);
            if (dataset == null)
                return NotFound(ApiResponse<DatasetDto>.ErrorResponse("Dataset not found"));

            return Ok(ApiResponse<DatasetDto>.SuccessResponse(dataset));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dataset {DatasetName}", datasetName);
            return StatusCode(500, ApiResponse<DatasetDto>.ErrorResponse("An error occurred"));
        }
    }

    /// <summary>
    /// Delete a dataset and all its records.
    /// </summary>
    [HttpDelete("datasets/{datasetName}")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse>> DeleteDataset(string datasetName)
    {
        try
        {
            var userId = GetUserId();
            var deleted = await _ingestionService.DeleteDatasetAsync(userId, datasetName);
            if (!deleted)
                return NotFound(ApiResponse.ErrorResponse("Dataset not found"));

            return Ok(ApiResponse.SuccessResponse("Dataset deleted successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting dataset {DatasetName}", datasetName);
            return StatusCode(500, ApiResponse.ErrorResponse("An error occurred"));
        }
    }

    // ──────────────────────────────────────────────
    // ANALYTICS QUERY
    // ──────────────────────────────────────────────

    /// <summary>
    /// Query analytics records with filters, sorting, and pagination.
    /// </summary>
    [HttpPost("query")]
    [ProducesResponseType(typeof(ApiResponse<AnalyticsQueryResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<AnalyticsQueryResponse>>> Query(
        [FromBody] AnalyticsQueryRequest request)
    {
        try
        {
            var userId = GetUserId();
            var result = await _queryService.QueryAsync(userId, request);
            return Ok(ApiResponse<AnalyticsQueryResponse>.SuccessResponse(result));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<AnalyticsQueryResponse>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing analytics query");
            return StatusCode(500, ApiResponse<AnalyticsQueryResponse>.ErrorResponse("An error occurred"));
        }
    }

    // ──────────────────────────────────────────────
    // METRICS
    // ──────────────────────────────────────────────

    /// <summary>
    /// Calculate metrics for a dataset with optional filters.
    /// </summary>
    [HttpPost("metrics")]
    [ProducesResponseType(typeof(ApiResponse<MetricsResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<MetricsResponse>>> CalculateMetrics(
        [FromBody] MetricsRequest request)
    {
        try
        {
            var userId = GetUserId();
            var result = await _metricsService.CalculateAsync(userId, request);
            return Ok(ApiResponse<MetricsResponse>.SuccessResponse(result));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<MetricsResponse>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating metrics");
            return StatusCode(500, ApiResponse<MetricsResponse>.ErrorResponse("An error occurred"));
        }
    }

    /// <summary>
    /// Create a reusable metric definition.
    /// </summary>
    [HttpPost("metrics/definitions")]
    [ProducesResponseType(typeof(ApiResponse<MetricDefinitionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<MetricDefinitionDto>>> CreateMetricDefinition(
        [FromBody] CreateMetricDefinitionRequest request)
    {
        try
        {
            var userId = GetUserId();
            var result = await _metricsService.CreateDefinitionAsync(userId, request);
            return Ok(ApiResponse<MetricDefinitionDto>.SuccessResponse(result, "Metric definition created"));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<MetricDefinitionDto>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating metric definition");
            return StatusCode(500, ApiResponse<MetricDefinitionDto>.ErrorResponse("An error occurred"));
        }
    }

    /// <summary>
    /// List metric definitions for a dataset.
    /// </summary>
    [HttpGet("metrics/definitions/{datasetName}")]
    [ProducesResponseType(typeof(ApiResponse<IEnumerable<MetricDefinitionDto>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IEnumerable<MetricDefinitionDto>>>> GetMetricDefinitions(
        string datasetName)
    {
        try
        {
            var userId = GetUserId();
            var defs = await _metricsService.GetDefinitionsAsync(userId, datasetName);
            return Ok(ApiResponse<IEnumerable<MetricDefinitionDto>>.SuccessResponse(defs));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting metric definitions");
            return StatusCode(500, ApiResponse<IEnumerable<MetricDefinitionDto>>.ErrorResponse("An error occurred"));
        }
    }

    /// <summary>
    /// Delete a metric definition.
    /// </summary>
    [HttpDelete("metrics/definitions/{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse>> DeleteMetricDefinition(Guid id)
    {
        try
        {
            var userId = GetUserId();
            var deleted = await _metricsService.DeleteDefinitionAsync(userId, id);
            if (!deleted)
                return NotFound(ApiResponse.ErrorResponse("Metric definition not found"));

            return Ok(ApiResponse.SuccessResponse("Metric definition deleted"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting metric definition");
            return StatusCode(500, ApiResponse.ErrorResponse("An error occurred"));
        }
    }

    // ──────────────────────────────────────────────
    // AGGREGATION
    // ──────────────────────────────────────────────

    /// <summary>
    /// Aggregate analytics data by time period and/or dimensions.
    /// </summary>
    [HttpPost("aggregate")]
    [ProducesResponseType(typeof(ApiResponse<AggregationResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<AggregationResponse>>> Aggregate(
        [FromBody] AggregationRequest request)
    {
        try
        {
            var userId = GetUserId();
            var result = await _aggregationService.AggregateAsync(userId, request);
            return Ok(ApiResponse<AggregationResponse>.SuccessResponse(result));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<AggregationResponse>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing aggregation");
            return StatusCode(500, ApiResponse<AggregationResponse>.ErrorResponse("An error occurred"));
        }
    }

    // ──────────────────────────────────────────────
    // HELPERS
    // ──────────────────────────────────────────────

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
