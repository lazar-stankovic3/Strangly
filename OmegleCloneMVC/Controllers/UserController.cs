using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OmegleCloneMVC.Data;
using OmegleCloneMVC.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace OmegleCloneMVC.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UsersController : ControllerBase
    {
        private readonly OmegleCloneMVCContext _context;
        private readonly IConfiguration _configuration;

        public UsersController(OmegleCloneMVCContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        [EnableRateLimiting("auth")]
        [HttpPost("Registration")]
        public async Task<IActionResult> Registration([FromBody] UserDto userDTO)
        {
            if (!ModelState.IsValid)
                return BadRequest("Neispravni podaci.");

            var email = userDTO.Mail.Trim().ToLowerInvariant();

            var existingUser = await _context.User.FirstOrDefaultAsync(x => x.Mail.ToLower() == email);
            if (existingUser != null)
                return BadRequest("Korisnik sa tim emailom već postoji.");

            var hasher = new PasswordHasher<User>();
            var newUser = new User
            {
                Username = userDTO.Username.Trim(),
                Mail = email,
                RoleId = (int)Roles.User,
                IsActive = 0,
                CreatedOn = DateTime.UtcNow
            };

            newUser.Password = hasher.HashPassword(newUser, userDTO.Password);
            _context.User.Add(newUser);
            await _context.SaveChangesAsync();

            return Ok("Uspešna registracija");
        }

        [EnableRateLimiting("auth")]
        [HttpPost("Login")]
        public async Task<IActionResult> Login([FromBody] LoginDTO loginDTO)
        {
            if (!ModelState.IsValid)
                return BadRequest("Neispravni podaci.");

            var email = loginDTO.Mail.Trim().ToLowerInvariant();

            var user = await _context.User
                .Include(u => u.Role)
                .FirstOrDefaultAsync(x => x.Mail.ToLower() == email);

            if (user == null)
                return BadRequest("Pogrešan email ili lozinka");

            var hasher = new PasswordHasher<User>();
            var result = hasher.VerifyHashedPassword(user, user.Password, loginDTO.Password);

            if (result == PasswordVerificationResult.Failed)
                return BadRequest("Pogrešan email ili lozinka");

            user.IsActive = 1;
            await _context.SaveChangesAsync();

            // COOKIE AUTH (glavno za MVC)e
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Email, user.Mail),
                new Claim(ClaimTypes.Role, user.Role?.Name ?? Roles.User.ToString())
            };

            if (user.IsPremium && user.PremiumUntil.HasValue && user.PremiumUntil.Value >= DateTime.UtcNow)
                claims.Add(new Claim("isPremium", "true"));

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

            // Redirect po roli
            var redirectUrl =
                string.Equals(user.Role?.Name, "Admin", StringComparison.OrdinalIgnoreCase)
                ? Url.Action("Index", "AdminPanel")
                : Url.Action("Index", "Home");

            // (Opcionalno) JWT — samo ako hoćeš API klijente
            string? jwt = null;
            var jwtKey = _configuration["Jwt:Key"];
            if (!string.IsNullOrWhiteSpace(jwtKey))
            {
                jwt = GenerateJwt(user);
            }

            return Ok(new
            {
                Token = jwt, // može biti null
                User = new
                {
                    user.UserId,
                    user.Username,
                    user.Mail,
                    Role = user.Role?.Name
                },
                RedirectUrl = redirectUrl
            });
        }

        [Authorize]
        [HttpPost("Logout")]
        public async Task<IActionResult> Logout()
        {
            // Uzimamo userId iz cookie claims, ne iz body-ja
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (int.TryParse(userIdStr, out var userId))
            {
                var user = await _context.User.FindAsync(userId);
                if (user != null)
                {
                    user.IsActive = 0;
                    await _context.SaveChangesAsync();
                }
            }

            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Ok("Logout uspešan.");
        }

        // Ovo ostavi ako ti treba samo za Bearer testiranje preko Swagger-a
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [HttpGet("GetUser")]
        public IActionResult GetUser([FromQuery] int id)
        {
            var requesterId = User.FindFirstValue("UserId");
            var requesterRole = User.FindFirstValue(ClaimTypes.Role);
            var isAdmin = string.Equals(requesterRole, "Admin", StringComparison.OrdinalIgnoreCase);

            if (!isAdmin && requesterId != id.ToString())
                return Forbid();

            var user = _context.User.Include(u => u.Role).FirstOrDefault(x => x.UserId == id);
            if (user == null) return NotFound();

            return Ok(new
            {
                user.UserId,
                user.Username,
                user.Mail,
                Role = user.Role?.Name
            });
        }

        private string GenerateJwt(User user)
        {
            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, _configuration["Jwt:Subject"] ?? "strangly"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim("UserId", user.UserId.ToString()),
                new Claim("Mail", user.Mail),
                new Claim("User", user.Username),
                new Claim(ClaimTypes.Role, user.Role?.Name ?? Roles.User.ToString())
            };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]!));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"],
                audience: _configuration["Jwt:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(60),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
