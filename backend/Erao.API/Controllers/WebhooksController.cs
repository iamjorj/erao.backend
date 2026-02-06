using Microsoft.AspNetCore.Mvc;
using Erao.Core.Interfaces;

namespace Erao.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WebhooksController : ControllerBase
{
    private readonly IDodoPaymentsService _dodoPaymentsService;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(IDodoPaymentsService dodoPaymentsService, ILogger<WebhooksController> logger)
    {
        _dodoPaymentsService = dodoPaymentsService;
        _logger = logger;
    }

    [HttpPost("dodo")]
    public async Task<IActionResult> DodoWebhook()
    {
        try
        {
            // Read raw body for signature verification
            using var reader = new StreamReader(Request.Body);
            var payload = await reader.ReadToEndAsync();

            // Extract webhook headers
            var headers = new Dictionary<string, string>();
            foreach (var header in Request.Headers)
            {
                headers[header.Key.ToLowerInvariant()] = header.Value.ToString();
            }

            await _dodoPaymentsService.HandleWebhookAsync(payload, headers);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Dodo Payments webhook");
            // Return 200 to prevent Dodo from retrying (log the error for investigation)
            return Ok();
        }
    }
}
