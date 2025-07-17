using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stripe.Checkout;

namespace OmegleCloneMVC.Controllers
{
    [Authorize]
    public class SubscriptionController : Controller
    {
        public IActionResult Buy()
        {
            var domain = $"{Request.Scheme}://{Request.Host}";

            var options = new SessionCreateOptions
            {
                Mode = "subscription",
                PaymentMethodTypes = new List<string> { "card" },
                LineItems = new List<SessionLineItemOptions>
                {
                    new SessionLineItemOptions
                    {
                        Price = "price_1Rltag0613z4vGXYgm6QFoLm", // <- tvoj stvarni Price ID ovde
                        Quantity = 1
                    }
                },
                SuccessUrl = domain + "/Subscription/Success?session_id={CHECKOUT_SESSION_ID}",
                CancelUrl = domain + "/Subscription/Cancel"
            };

            var service = new SessionService();
            var session = service.Create(options);

            return Redirect(session.Url);
        }

        public IActionResult Success()
        {
            // Ovde možeš obeležiti korisnika kao Premium
            return View();
        }

        public IActionResult Cancel()
        {
            return View();
        }
    }
}
