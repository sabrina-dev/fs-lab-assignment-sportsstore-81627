using SportsStore.Models;

namespace SportsStore.Services;

public interface IPaymentService
{
    /// <summary>
    /// Creates a Stripe Checkout Session for the given order and returns the session URL to redirect to.
    /// </summary>
    Task<string> CreateCheckoutSessionAsync(Order order, string successUrl, string cancelUrl);
}
