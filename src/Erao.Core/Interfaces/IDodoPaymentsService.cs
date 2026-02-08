using Erao.Core.Enums;

namespace Erao.Core.Interfaces;

public interface IDodoPaymentsService
{
    /// <summary>
    /// Create a checkout session for a subscription upgrade.
    /// Returns the checkout URL to redirect the user to.
    /// </summary>
    Task<string> CreateCheckoutSessionAsync(Guid userId, string email, string name, SubscriptionTier targetTier, string returnUrl);

    /// <summary>
    /// Handle a webhook event from Dodo Payments.
    /// Verifies signature and processes subscription/payment events.
    /// </summary>
    Task HandleWebhookAsync(string payload, IDictionary<string, string> headers);

    /// <summary>
    /// Test the Dodo Payments API connection.
    /// Returns configuration status and any errors.
    /// </summary>
    Task<(bool IsConfigured, bool IsConnected, string Message)> TestConnectionAsync();
}
