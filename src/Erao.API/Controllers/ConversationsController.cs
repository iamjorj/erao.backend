using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Erao.Application.Services;
using Erao.Core.DTOs.Chat;
using Erao.Core.DTOs.Common;
using System.Security.Claims;

namespace Erao.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ConversationsController : ControllerBase
{
    private readonly IConversationService _conversationService;
    private readonly ILogger<ConversationsController> _logger;

    public ConversationsController(IConversationService conversationService, ILogger<ConversationsController> logger)
    {
        _conversationService = conversationService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IEnumerable<ConversationDto>>>> GetAll()
    {
        try
        {
            var userId = GetUserId();
            var conversations = await _conversationService.GetUserConversationsAsync(userId);
            return Ok(ApiResponse<IEnumerable<ConversationDto>>.SuccessResponse(conversations));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting conversations");
            return StatusCode(500, ApiResponse<IEnumerable<ConversationDto>>.ErrorResponse("An error occurred"));
        }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResponse<ConversationDto>>> GetById(Guid id)
    {
        try
        {
            var userId = GetUserId();
            var conversation = await _conversationService.GetByIdAsync(id, userId);
            if (conversation == null)
            {
                return NotFound(ApiResponse<ConversationDto>.ErrorResponse("Conversation not found"));
            }
            return Ok(ApiResponse<ConversationDto>.SuccessResponse(conversation));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting conversation {Id}", id);
            return StatusCode(500, ApiResponse<ConversationDto>.ErrorResponse("An error occurred"));
        }
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<ConversationDto>>> Create([FromBody] CreateConversationRequest request)
    {
        try
        {
            var userId = GetUserId();
            var conversation = await _conversationService.CreateAsync(userId, request);
            return CreatedAtAction(nameof(GetById), new { id = conversation.Id },
                ApiResponse<ConversationDto>.SuccessResponse(conversation, "Conversation created"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<ConversationDto>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating conversation");
            return StatusCode(500, ApiResponse<ConversationDto>.ErrorResponse("An error occurred"));
        }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResponse<ConversationDto>>> Update(Guid id, [FromBody] UpdateConversationRequest request)
    {
        try
        {
            var userId = GetUserId();
            var conversation = await _conversationService.UpdateAsync(id, userId, request);
            return Ok(ApiResponse<ConversationDto>.SuccessResponse(conversation, "Conversation updated"));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<ConversationDto>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating conversation {Id}", id);
            return StatusCode(500, ApiResponse<ConversationDto>.ErrorResponse("An error occurred"));
        }
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResponse>> Delete(Guid id)
    {
        try
        {
            var userId = GetUserId();
            await _conversationService.DeleteAsync(id, userId);
            return Ok(ApiResponse.SuccessResponse("Conversation deleted"));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting conversation {Id}", id);
            return StatusCode(500, ApiResponse.ErrorResponse("An error occurred"));
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
