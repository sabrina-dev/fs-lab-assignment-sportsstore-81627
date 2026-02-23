using Microsoft.AspNetCore.Mvc;
using SportsStore.Models;
using SportsStore.Services;

namespace SportsStore.Controllers {

    public class OrderController : Controller {
        private readonly IOrderRepository _repository;
        private readonly Cart _cart;
        private readonly IPaymentService _paymentService;
        private readonly ILogger<OrderController> _logger;

        public OrderController(
            IOrderRepository repoService,
            Cart cartService,
            IPaymentService paymentService,
            ILogger<OrderController> logger) {
            _repository     = repoService;
            _cart           = cartService;
            _paymentService = paymentService;
            _logger         = logger;
        }

        public ViewResult Checkout() => View(new Order());

        [HttpPost]
        public async Task<IActionResult> Checkout(Order order) {
            if (_cart.Lines.Count() == 0) {
                ModelState.AddModelError("", "Sorry, your cart is empty!");
            }

            if (!ModelState.IsValid) {
                return View();
            }

            // 1. Persist draft order so we have an OrderID for Stripe metadata
            order.Lines         = _cart.Lines.ToArray();
            order.PaymentStatus = "Pending";
            _repository.SaveOrder(order);
            _cart.Clear();

            _logger.LogInformation(
                "Order saved as draft. OrderId={OrderId} CustomerName={CustomerName} Lines={Lines}",
                order.OrderID, order.Name,                 order.Lines.Count);

            // 2. Build absolute success / cancel URLs
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var successUrl = $"{baseUrl}/PaymentSuccess?orderId={order.OrderID}";
            var cancelUrl  = $"{baseUrl}/PaymentCancelled?orderId={order.OrderID}";

            // 3. Create Stripe Checkout Session and redirect
            try {
                var checkoutUrl = await _paymentService.CreateCheckoutSessionAsync(
                    order, successUrl, cancelUrl);

                _logger.LogInformation(
                    "Redirecting to Stripe Checkout. OrderId={OrderId}", order.OrderID);

                return Redirect(checkoutUrl);
            }
            catch (Exception ex) {
                _logger.LogError(ex,
                    "Failed to create Stripe Checkout Session for OrderId={OrderId}", order.OrderID);
                ModelState.AddModelError("",
                    "Payment could not be initiated. Please try again.");
                return View(order);
            }
        }
    }
}
