using Microsoft.Extensions.Options;
using SportsStore.Models;
using Stripe.Checkout;

namespace SportsStore.Services;

public class StripePaymentService : IPaymentService
{
    private readonly StripeSettings _settings;
    private readonly ILogger<StripePaymentService> _logger;

    public StripePaymentService(
        IOptions<StripeSettings> settings,
        ILogger<StripePaymentService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        Stripe.StripeConfiguration.ApiKey = _settings.SecretKey;
    }

    public async Task<string> CreateCheckoutSessionAsync(
        Order order, string successUrl, string cancelUrl)
    {
        _logger.LogInformation(
            "Creating Stripe Checkout Session for OrderId={OrderId} ItemCount={ItemCount}",
            order.OrderID, order.Lines.Count);

        var lineItems = order.Lines.Select(line => new SessionLineItemOptions
        {
            PriceData = new SessionLineItemPriceDataOptions
            {
                Currency = "usd",
                UnitAmount = (long)(line.Product.Price * 100),
                ProductData = new SessionLineItemPriceDataProductDataOptions
                {
                    Name = line.Product.Name,
                    Description = line.Product.Description
                }
            },
            Quantity = line.Quantity
        }).ToList();

        var options = new SessionCreateOptions
        {
            PaymentMethodTypes = new List<string> { "card" },
            LineItems = lineItems,
            Mode = "payment",
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            Metadata = new Dictionary<string, string>
            {
                { "OrderId", order.OrderID.ToString() }
            },
            CustomerEmail = null
        };

        try
        {
            var service = new SessionService();
            var session = await service.CreateAsync(options);

            _logger.LogInformation(
                "Stripe Checkout Session created. OrderId={OrderId} SessionId={SessionId} Url={Url}",
                order.OrderID, session.Id, session.Url);

            return session.Url;
        }
        catch (Stripe.StripeException ex)
        {
            _logger.LogError(ex,
                "Stripe error creating checkout session. OrderId={OrderId} StripeError={StripeError}",
                order.OrderID, ex.StripeError?.Message);
            throw;
        }
    }
}
