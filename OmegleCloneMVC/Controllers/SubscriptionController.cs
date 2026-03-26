using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OmegleCloneMVC.Data;
using Stripe;
using Stripe.Checkout;

namespace OmegleCloneMVC.Controllers
{
    [Authorize]
    public class SubscriptionController : Controller
    {
        private readonly OmegleCloneMVCContext _context;
        private readonly IConfiguration _config;
        private readonly ILogger<SubscriptionController> _logger;

        public SubscriptionController(OmegleCloneMVCContext context, IConfiguration config, ILogger<SubscriptionController> logger)
        {
            _context = context;
            _config = config;
            _logger = logger;

            var secretKey = _config["Stripe:SecretKey"];
            if (!string.IsNullOrWhiteSpace(secretKey))
                StripeConfiguration.ApiKey = secretKey;
        }

        public IActionResult Buy()
        {
            var domain = $"{Request.Scheme}://{Request.Host}";

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var email = User.FindFirstValue(ClaimTypes.Email) ?? "";

            if (string.IsNullOrWhiteSpace(userId))
                return Unauthorized("Nema userId u cookie claim-ovima. Logout/Login ponovo.");

            var priceId = _config["Stripe:PriceId"];
            if (string.IsNullOrWhiteSpace(priceId))
                return BadRequest("Stripe PriceId nije podešen (Stripe:PriceId).");

            var options = new SessionCreateOptions
            {
                Mode = "subscription",
                PaymentMethodTypes = new List<string> { "card" },

                ClientReferenceId = userId,
                CustomerEmail = string.IsNullOrWhiteSpace(email) ? null : email,

                // metadata na checkout session
                Metadata = new Dictionary<string, string>
                {
                    { "userId", userId },
                    { "email", email }
                },

                // KLJUČNO: metadata na subscription (za subscription.deleted event)
                SubscriptionData = new SessionSubscriptionDataOptions
                {
                    Metadata = new Dictionary<string, string>
                    {
                        { "userId", userId },
                        { "email", email }
                    }
                },

                LineItems = new List<SessionLineItemOptions>
                {
                    new SessionLineItemOptions
                    {
                        Price = priceId,
                        Quantity = 1
                    }
                },

                SuccessUrl = domain + "/Subscription/Success?session_id={CHECKOUT_SESSION_ID}",
                CancelUrl = domain + "/Subscription/Cancel"
            };

            try
            {
                var service = new SessionService();
                var session = service.Create(options);
                return Redirect(session.Url);
            }
            catch (StripeException ex)
            {
                _logger.LogError(ex, "Stripe checkout create failed: {Msg}", ex.StripeError?.Message);
                return BadRequest(ex.StripeError?.Message ?? "Stripe error");
            }
        }

        public IActionResult Success(string? session_id = null)
        {
            ViewData["SessionId"] = session_id;
            return View();
        }

        public IActionResult Cancel()
        {
            return View();
        }

        // ===== UI helper: proveri status premium iz baze =====
        [Authorize]
        [HttpGet]
        public async Task<IActionResult> PremiumStatus()
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out var userId))
                return Unauthorized();

            var user = await _context.User.FirstOrDefaultAsync(u => u.UserId == userId);
            if (user == null) return NotFound();

            var isPremium = user.IsPremium && user.PremiumUntil != null && user.PremiumUntil >= DateTime.UtcNow;
            return Json(new { isPremium, premiumUntil = user.PremiumUntil });
        }

        // ===== Re-issue cookie claims da korisnik odmah dobije benefite =====
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RefreshPremium()
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out var userId))
                return Unauthorized();

            var user = await _context.User.Include(u => u.Role).FirstOrDefaultAsync(u => u.UserId == userId);
            if (user == null) return NotFound();

            var isPremium = user.IsPremium && user.PremiumUntil != null && user.PremiumUntil >= DateTime.UtcNow;

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
                new Claim(ClaimTypes.Name, user.Username ?? ""),
                new Claim(ClaimTypes.Email, user.Mail ?? ""),
                new Claim(ClaimTypes.Role, user.Role?.Name ?? "User"),
            };

            if (isPremium)
                claims.Add(new Claim("isPremium", "true"));

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

            return Ok(new { ok = true, isPremium, premiumUntil = user.PremiumUntil });
        }

        // ===== STRIPE WEBHOOK =====
        [AllowAnonymous]
        [HttpPost("/stripe/webhook")]
        public async Task<IActionResult> StripeWebhook()
        {
            _logger.LogInformation("WEBHOOK HIT");

            var webhookSecret = _config["Stripe:WebhookSecret"];
            if (string.IsNullOrWhiteSpace(webhookSecret))
                return BadRequest("Stripe WebhookSecret nije podešen (Stripe:WebhookSecret).");

            var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
            var signatureHeader = Request.Headers["Stripe-Signature"].ToString();

            Event stripeEvent;
            try
            {
                stripeEvent = EventUtility.ConstructEvent(json, signatureHeader, webhookSecret);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SIGNATURE FAIL");
                return BadRequest();
            }

            try
            {
                // ===== checkout.session.completed =====
                if (stripeEvent.Type == "checkout.session.completed")
                {
                    var session = stripeEvent.Data.Object as Stripe.Checkout.Session;
                    if (session == null)
                    {
                        _logger.LogWarning("checkout.session.completed: Session cast failed. ObjType={Type}",
                            stripeEvent.Data.Object?.GetType().FullName);
                        return Ok();
                    }

                    var email = session.CustomerDetails?.Email ?? session.CustomerEmail;

                    // kompatibilno sa raznim Stripe.NET verzijama (SubscriptionId / Subscription)
                    var subscriptionId = GetSubscriptionIdSafe(session);

                    _logger.LogInformation(
                        "SESSION completed: id={Id} mode={Mode} payStatus={Pay} status={Status} cref={Cref} sub={Sub} email={Email}",
                        session.Id, session.Mode, session.PaymentStatus, session.Status, session.ClientReferenceId, subscriptionId, email
                    );

                    // Robust check: tretiraj kao subscription i kad mode nije pouzdan, ali subscriptionId postoji
                    var isSubscription =
                        string.Equals(session.Mode, "subscription", StringComparison.OrdinalIgnoreCase) ||
                        !string.IsNullOrWhiteSpace(subscriptionId);

                    if (!isSubscription)
                    {
                        _logger.LogInformation("Ignoring session {Id} because not subscription. mode={Mode} sub={Sub}",
                            session.Id, session.Mode, subscriptionId);
                        return Ok();
                    }

                    // fallback: 30 dana
                    var premiumUntilUtc = DateTime.UtcNow.AddDays(30);

                    var user = await ResolveUserAsync(session.ClientReferenceId, email, session.Metadata);
                    if (user == null)
                    {
                        _logger.LogWarning("Could not resolve user for session {SessionId}. cref={Cref} email={Email}",
                            session.Id, session.ClientReferenceId, email);
                        return Ok();
                    }

                    // pokušaj da uzmeš period end sa subscription-a
                    if (!string.IsNullOrWhiteSpace(subscriptionId))
                    {
                        try
                        {
                            var subService = new Stripe.SubscriptionService();
                            var sub = await subService.GetAsync(subscriptionId);

                            var endUnix = TryGetSubscriptionPeriodEndUnixSeconds(sub);
                            if (endUnix.HasValue && endUnix.Value > 0)
                                premiumUntilUtc = DateTimeOffset.FromUnixTimeSeconds(endUnix.Value).UtcDateTime;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Could not read subscription period end; using 30-day fallback");
                        }
                    }

                    user.IsPremium = true;
                    user.PremiumUntil = premiumUntilUtc;
                    await _context.SaveChangesAsync();

                    _logger.LogInformation("Premium activated for userId={UserId} until={Until}",
                        user.UserId, user.PremiumUntil);

                    return Ok();
                }

                // ===== customer.subscription.deleted =====
                if (stripeEvent.Type == "customer.subscription.deleted")
                {
                    var sub = stripeEvent.Data.Object as Stripe.Subscription;
                    if (sub == null) return Ok();

                    var md = sub.Metadata;
                    var userIdFromMd = (md != null && md.ContainsKey("userId")) ? md["userId"] : null;

                    _logger.LogInformation("subscription.deleted: sub={SubId} md_userId={UserId}",
                        sub.Id, userIdFromMd);

                    var user = await ResolveUserAsync(userIdFromMd, null, md);
                    if (user == null)
                    {
                        _logger.LogWarning("subscription.deleted: Could not resolve user. sub={SubId}", sub.Id);
                        return Ok();
                    }

                    user.IsPremium = false;
                    user.PremiumUntil = null;
                    await _context.SaveChangesAsync();

                    _logger.LogInformation("Premium removed for userId={UserId}", user.UserId);

                    return Ok();
                }

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stripe webhook processing error");
                return Ok(); // 200 da Stripe ne retry spamuje
            }
        }

        private async Task<OmegleCloneMVC.Models.User?> ResolveUserAsync(
            string? clientReferenceId,
            string? customerEmail,
            Dictionary<string, string>? metadata)
        {
            // 1) client_reference_id
            if (int.TryParse(clientReferenceId, out var uid))
                return await _context.User.FirstOrDefaultAsync(u => u.UserId == uid);

            // 2) metadata userId
            if (metadata != null && metadata.TryGetValue("userId", out var mdUserId) && int.TryParse(mdUserId, out var metaUid))
                return await _context.User.FirstOrDefaultAsync(u => u.UserId == metaUid);

            // 3) email
            var email = customerEmail;
            if (string.IsNullOrWhiteSpace(email) && metadata != null && metadata.TryGetValue("email", out var mdEmail))
                email = mdEmail;

            if (string.IsNullOrWhiteSpace(email))
            {
                _logger.LogWarning("ResolveUserAsync failed: no userId and no email. cref={Cref}", clientReferenceId);
                return null;
            }

            email = email.Trim().ToLowerInvariant();
            return await _context.User.FirstOrDefaultAsync(u => u.Mail.ToLower() == email);
        }

        // ---- Helper: uzmi subscription id bez zavisnosti od Stripe.NET tipa ----
        private static string? GetSubscriptionIdSafe(Stripe.Checkout.Session session)
        {
            if (session == null) return null;

            // 1) SubscriptionId property (ako postoji)
            var p1 = session.GetType().GetProperty("SubscriptionId");
            if (p1 != null)
            {
                var v = p1.GetValue(session);
                var s = v?.ToString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }

            // 2) Subscription property (može biti string ili ExpandableField ili Stripe.Subscription)
            var p2 = session.GetType().GetProperty("Subscription");
            if (p2 != null)
            {
                var subVal = p2.GetValue(session);
                if (subVal == null) return null;

                // string
                if (subVal is string ss) return ss;

                // Stripe.Subscription
                if (subVal is Stripe.Subscription subObj) return subObj.Id;

                // ExpandableField-like: probaj "Id" property
                var idProp = subVal.GetType().GetProperty("Id");
                if (idProp != null)
                {
                    var idVal = idProp.GetValue(subVal)?.ToString();
                    if (!string.IsNullOrWhiteSpace(idVal)) return idVal;
                }

                // fallback
                return subVal.ToString();
            }

            return null;
        }

        // helper (kompajlira stabilno bez pattern matching problema)
        private static long? TryGetSubscriptionPeriodEndUnixSeconds(object subscription)
        {
            if (subscription == null) return null;

            var t = subscription.GetType();
            var namesToTry = new[]
            {
                "CurrentPeriodEnd",
                "CurrentPeriodEndUnixTime",
                "CurrentPeriodEndTimestamp",
                "CurrentPeriodEndUtc",
                "CurrentPeriodEndDate"
            };

            foreach (var name in namesToTry)
            {
                var prop = t.GetProperty(name);
                if (prop == null) continue;

                var val = prop.GetValue(subscription);
                if (val == null) continue;

                if (val is long) return (long)val;
                if (val is int) return (int)val;

                var type = val.GetType();
                if (type == typeof(long?))
                {
                    var ln = (long?)val;
                    if (ln.HasValue) return ln.Value;
                }
                if (type == typeof(int?))
                {
                    var inn = (int?)val;
                    if (inn.HasValue) return inn.Value;
                }

                if (val is DateTime)
                {
                    var dt = (DateTime)val;
                    return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeSeconds();
                }
                if (type == typeof(DateTime?))
                {
                    var dtn = (DateTime?)val;
                    if (dtn.HasValue)
                        return new DateTimeOffset(DateTime.SpecifyKind(dtn.Value, DateTimeKind.Utc)).ToUnixTimeSeconds();
                }

                if (val is DateTimeOffset)
                {
                    var dto = (DateTimeOffset)val;
                    return dto.ToUnixTimeSeconds();
                }
                if (type == typeof(DateTimeOffset?))
                {
                    var dton = (DateTimeOffset?)val;
                    if (dton.HasValue) return dton.Value.ToUnixTimeSeconds();
                }
            }

            return null;
        }
    }
}
