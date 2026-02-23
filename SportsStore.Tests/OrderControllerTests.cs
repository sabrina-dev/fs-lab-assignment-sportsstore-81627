using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SportsStore.Controllers;
using SportsStore.Models;
using SportsStore.Services;
using Xunit;

namespace SportsStore.Tests {

    public class OrderControllerTests {

        [Fact]
        public async Task Cannot_Checkout_Empty_Cart() {
            // Arrange - create a mock repository
            Mock<IOrderRepository> mock = new Mock<IOrderRepository>();
            // Arrange - create an empty cart
            Cart cart = new Cart();
            // Arrange - create the order
            Order order = new Order();
            // Arrange - create an instance of the controller
            OrderController target = new OrderController(
                mock.Object,
                cart,
                new Mock<IPaymentService>().Object,
                NullLogger<OrderController>.Instance);

            // Act
            ViewResult? result = await target.Checkout(order) as ViewResult;

            // Assert - check that the order hasn't been stored 
            mock.Verify(m => m.SaveOrder(It.IsAny<Order>()), Times.Never);
            // Assert - check that the method is returning the default view
            Assert.True(string.IsNullOrEmpty(result?.ViewName));
            // Assert - check that I am passing an invalid model to the view
            Assert.False(result?.ViewData.ModelState.IsValid);
        }

        [Fact]
        public async Task Cannot_Checkout_Invalid_ShippingDetails() {

            // Arrange - create a mock order repository
            Mock<IOrderRepository> mock = new Mock<IOrderRepository>();
            // Arrange - create a cart with one item
            Cart cart = new Cart();
            cart.AddItem(new Product(), 1);
            // Arrange - create an instance of the controller
            OrderController target = new OrderController(
                mock.Object,
                cart,
                new Mock<IPaymentService>().Object,
                NullLogger<OrderController>.Instance);
            // Arrange - add an error to the model
            target.ModelState.AddModelError("error", "error");

            // Act - try to checkout
            ViewResult? result = await target.Checkout(new Order()) as ViewResult;

            // Assert - check that the order hasn't been passed stored
            mock.Verify(m => m.SaveOrder(It.IsAny<Order>()), Times.Never);
            // Assert - check that the method is returning the default view
            Assert.True(string.IsNullOrEmpty(result?.ViewName));
            // Assert - check that I am passing an invalid model to the view
            Assert.False(result?.ViewData.ModelState.IsValid);
        }

        [Fact]
        public async Task Can_Checkout_And_Submit_Order() {
            // Arrange - create a mock order repository
            Mock<IOrderRepository> mock = new Mock<IOrderRepository>();
            // Arrange - create a cart with one item
            Cart cart = new Cart();
            cart.AddItem(new Product(), 1);
            // Arrange - mock payment service returning a Stripe URL
            Mock<IPaymentService> paymentMock = new Mock<IPaymentService>();
            paymentMock
                .Setup(s => s.CreateCheckoutSessionAsync(
                    It.IsAny<Order>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync("https://checkout.stripe.com/test");
            // Arrange - create an instance of the controller with a fake HttpContext
            OrderController target = new OrderController(
                mock.Object,
                cart,
                paymentMock.Object,
                NullLogger<OrderController>.Instance);

            // Provide a minimal HttpContext so Request.Scheme / Request.Host work
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = "https";
            httpContext.Request.Host   = new HostString("localhost");
            target.ControllerContext   = new ControllerContext
            {
                HttpContext = httpContext
            };

            // Act - try to checkout
            var result = await target.Checkout(new Order());

            // Assert - check that the order has been stored
            mock.Verify(m => m.SaveOrder(It.IsAny<Order>()), Times.Once);
            // Assert - check that the method redirects to Stripe checkout
            Assert.IsType<RedirectResult>(result);
            Assert.Equal("https://checkout.stripe.com/test", ((RedirectResult)result).Url);
        }
    }
}
