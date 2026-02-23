using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SportsStore.Pages
{
    public class PaymentCancelledModel : PageModel
    {
        public int OrderId { get; set; }

        public void OnGet(int orderId)
        {
            OrderId = orderId;
        }
    }
}
