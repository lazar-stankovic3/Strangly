using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OmegleCloneMVC.Data;
using OmegleCloneMVC.Models;

namespace OmegleCloneMVC.Controllers
{
    [Route("auth")]
    public class AuthController : Controller
    {
        private readonly OmegleCloneMVCContext _context;

        public AuthController(OmegleCloneMVCContext context)
        {
            _context = context;
        }

        [Authorize]
        [HttpGet("logout")]
        public async Task<IActionResult> Logout()
        {
            var email = User.FindFirstValue(ClaimTypes.Email);
            if (!string.IsNullOrWhiteSpace(email))
            {
                var user = await _context.User.FirstOrDefaultAsync(u => u.Mail == email);
                if (user != null)
                {
                    user.IsActive = 0;
                    await _context.SaveChangesAsync();
                }
            }

            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Index", "Home");
        }

        [HttpGet("google-login")]
        public IActionResult GoogleLogin(string? returnUrl = "/")
        {
            var redirectUrl = Url.Action(nameof(ExternalLoginCallback), "Auth", new { returnUrl }, Request.Scheme);
            var props = new AuthenticationProperties { RedirectUri = redirectUrl };
            return Challenge(props, "Google");
        }

        [HttpGet("facebook-login")]
        public IActionResult FacebookLogin(string? returnUrl = "/")
        {
            var redirectUrl = Url.Action(nameof(ExternalLoginCallback), "Auth", new { returnUrl }, Request.Scheme);
            var props = new AuthenticationProperties { RedirectUri = redirectUrl };
            return Challenge(props, "Facebook");
        }

        [HttpGet("externallogincallback")]
        public async Task<IActionResult> ExternalLoginCallback(string? returnUrl = "/")
        {
            var external = await HttpContext.AuthenticateAsync("External");
            if (!external.Succeeded || external.Principal == null)
                return BadRequest("External login failed.");

            var principal = external.Principal;

            var email = principal.FindFirstValue(ClaimTypes.Email)
                        ?? principal.FindFirstValue("email");

            if (string.IsNullOrWhiteSpace(email))
                return BadRequest("Email is not available from the external provider.");

            email = email.Trim().ToLowerInvariant();

            var username = principal.Identity?.Name
                           ?? principal.FindFirstValue(ClaimTypes.Name)
                           ?? principal.FindFirstValue("name")
                           ?? email.Split('@')[0];

            var user = await _context.User.Include(u => u.Role).FirstOrDefaultAsync(u => u.Mail == email);

            if (user == null)
            {
                user = new User
                {
                    Username = username,
                    Mail = email,
                    Password = string.Empty, // OAuth user
                    RoleId = (int)Roles.User,
                    IsActive = 1,
                    CreatedOn = DateTime.UtcNow
                };

                _context.User.Add(user);
                await _context.SaveChangesAsync();
                user = await _context.User.Include(u => u.Role).FirstAsync(u => u.Mail == email);
            }
            else
            {
                user.IsActive = 1;
                await _context.SaveChangesAsync();
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
                new(ClaimTypes.Name, user.Username),
                new(ClaimTypes.Email, user.Mail),
                new(ClaimTypes.Role, user.Role?.Name ?? Roles.User.ToString())
            };

            if (user.IsPremium && user.PremiumUntil.HasValue && user.PremiumUntil.Value >= DateTime.UtcNow)
                claims.Add(new Claim("isPremium", "true"));

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

            // očisti external cookie
            await HttpContext.SignOutAsync("External");

            return LocalRedirect(string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl);
        }
    }
}
