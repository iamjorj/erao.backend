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
public class ChatController : ControllerBase
{
    private readonly IChatService _chatService;
    private readonly ILogger<ChatController> _logger;

    public ChatController(IChatService chatService, ILogger<ChatController> logger)
    {
        _chatService = chatService;
        _logger = logger;
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<ChatResponse>>> SendMessage([FromBody] ChatRequest request)
    {
        try
        {
            var userId = GetUserId();
            var response = await _chatService.ProcessMessageAsync(userId, request);
            return Ok(ApiResponse<ChatResponse>.SuccessResponse(response));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(ApiResponse<ChatResponse>.ErrorResponse(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<ChatResponse>.ErrorResponse(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing chat message");
            return StatusCode(500, ApiResponse<ChatResponse>.ErrorResponse("An error occurred while processing your message"));
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
