using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OmegleCloneMVC.Data;
using OmegleCloneMVC.Models;

namespace OmegleCloneMVC.Controllers
{
    [Authorize(Roles = "Admin")]
    [Route("AdminPanel")]
    public class AdminPanelController : Controller
    {
        private readonly OmegleCloneMVCContext _context;

        public AdminPanelController(OmegleCloneMVCContext context)
        {
            _context = context;
        }

        // ===== DASHBOARD + LIST =====
        [HttpGet("")]
        public async Task<IActionResult> Index(
            string? q = null,
            int? roleId = null,
            bool? premium = null,
            bool? active = null,
            int page = 1,
            int pageSize = 25)
        {
            q = q?.Trim();
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 10, 100);

            var query = _context.User
                .Include(u => u.Role)
                .AsNoTracking()
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
            {
                var lq = q.ToLowerInvariant();
                query = query.Where(u =>
                    u.Username.ToLower().Contains(lq) ||
                    u.Mail.ToLower().Contains(lq));
            }

            if (roleId.HasValue)
                query = query.Where(u => u.RoleId == roleId.Value);

            if (premium.HasValue)
                query = premium.Value
                    ? query.Where(u => u.IsPremium && u.PremiumUntil != null && u.PremiumUntil >= DateTime.UtcNow)
                    : query.Where(u => !u.IsPremium || u.PremiumUntil == null || u.PremiumUntil < DateTime.UtcNow);

            if (active.HasValue)
                query = active.Value ? query.Where(u => u.IsActive == 1) : query.Where(u => u.IsActive == 0);

            var total = await query.CountAsync();
            var totalPages = (int)Math.Ceiling(total / (double)pageSize);

            if (totalPages > 0) page = Math.Min(page, totalPages);

            var users = await query
                .OrderByDescending(u => u.UserId)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var roles = await _context.Role.AsNoTracking().OrderBy(r => r.Id).ToListAsync();

            var vm = new AdminDashboardVm
            {
                Q = q,
                RoleId = roleId,
                Premium = premium,
                Active = active,
                Page = page,
                PageSize = pageSize,
                Total = total,
                TotalPages = totalPages,
                Roles = roles.Select(r => new AdminRoleVm { Id = r.Id, Name = r.Name }).ToList(),
                Users = users.Select(u => AdminUserRowVm.From(u)).ToList(),
                Stats = await GetStatsAsync()
            };

            return View(vm);
        }

        // ===== DETAILS =====
        [HttpGet("UserDetails/{id:int}")]
        public async Task<IActionResult> UserDetails(int id)
        {
            var user = await _context.User.Include(u => u.Role).FirstOrDefaultAsync(u => u.UserId == id);
            if (user == null) return NotFound();

            var roles = await _context.Role.AsNoTracking().OrderBy(r => r.Id).ToListAsync();

            var vm = new AdminUserDetailsVm
            {
                User = AdminUserRowVm.From(user),
                Roles = roles.Select(r => new AdminRoleVm { Id = r.Id, Name = r.Name }).ToList()
            };

            return View(vm);
        }

        // ===== ACTIONS =====

        [HttpPost("SetRole")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetRole(int userId, int roleId)
        {
            if (!Enum.IsDefined(typeof(Roles), roleId))
                return BadRequest("Nepoznata rola.");

            var user = await _context.User.Include(u => u.Role).FirstOrDefaultAsync(u => u.UserId == userId);
            if (user == null) return NotFound();

            var oldRole = user.RoleId;
            user.RoleId = roleId;
            await _context.SaveChangesAsync();

            await LogAsync("SET_ROLE", userId, user.Mail, $"RoleId: {oldRole} -> {roleId}");

            TempData["Msg"] = $"Rola promenjena za {user.Username}.";
            return RedirectBackToUserOrIndex(userId);
        }

        [HttpPost("ToggleActive")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleActive(int userId)
        {
            var user = await _context.User.FindAsync(userId);
            if (user == null) return NotFound();

            user.IsActive = user.IsActive == 1 ? 0 : 1;
            await _context.SaveChangesAsync();

            await LogAsync("TOGGLE_ACTIVE", userId, user.Mail, $"IsActive = {user.IsActive}");

            TempData["Msg"] = $"Active status promenjen za {user.Username}.";
            return RedirectBackToUserOrIndex(userId);
        }

        [HttpPost("GrantPremium")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GrantPremium(int userId, int days)
        {
            days = Math.Clamp(days, 1, 365);

            var user = await _context.User.FindAsync(userId);
            if (user == null) return NotFound();

            user.IsPremium = true;
            user.PremiumUntil = DateTime.UtcNow.AddDays(days);
            await _context.SaveChangesAsync();

            await LogAsync("GRANT_PREMIUM", userId, user.Mail, $"days={days}, until={user.PremiumUntil:O}");

            TempData["Msg"] = $"Premium dodeljen za {user.Username} ({days} dana).";
            return RedirectBackToUserOrIndex(userId);
        }

        [HttpPost("RevokePremium")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RevokePremium(int userId)
        {
            var user = await _context.User.FindAsync(userId);
            if (user == null) return NotFound();

            user.IsPremium = false;
            user.PremiumUntil = null;
            await _context.SaveChangesAsync();

            await LogAsync("REVOKE_PREMIUM", userId, user.Mail);

            TempData["Msg"] = $"Premium uklonjen za {user.Username}.";
            return RedirectBackToUserOrIndex(userId);
        }

        [HttpPost("ResetPassword")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(int userId, string newPassword)
        {
            newPassword = (newPassword ?? "").Trim();
            if (newPassword.Length < 6)
                return BadRequest("Lozinka mora imati minimum 6 karaktera.");

            var user = await _context.User.FindAsync(userId);
            if (user == null) return NotFound();

            var hasher = new PasswordHasher<User>();
            user.Password = hasher.HashPassword(user, newPassword);
            await _context.SaveChangesAsync();

            await LogAsync("RESET_PASSWORD", userId, user.Mail);

            TempData["Msg"] = $"Lozinka resetovana za {user.Username}.";
            return RedirectToAction(nameof(UserDetails), new { id = userId });
        }

        [HttpPost("DeleteUser")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteUser(int userId)
        {
            var user = await _context.User.FindAsync(userId);
            if (user == null) return NotFound();

            var currentId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (currentId == userId.ToString())
                return BadRequest("Ne možeš obrisati samog sebe.");

            _context.User.Remove(user);
            await _context.SaveChangesAsync();

            await LogAsync("DELETE_USER", userId, user.Mail);

            TempData["Msg"] = $"Korisnik obrisan: {user.Username}.";
            return RedirectToAction(nameof(Index));
        }

        // ===== LOGS =====
        [HttpGet("Logs")]
        public async Task<IActionResult> Logs(int page = 1, int pageSize = 30)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 10, 100);

            var query = _context.AdminActionLogs.AsNoTracking().OrderByDescending(x => x.CreatedUtc);

            var total = await query.CountAsync();
            var totalPages = (int)Math.Ceiling(total / (double)pageSize);
            if (totalPages > 0) page = Math.Min(page, totalPages);

            var logs = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

            var vm = new AdminLogsVm
            {
                Page = page,
                PageSize = pageSize,
                Total = total,
                TotalPages = totalPages,
                Logs = logs
            };

            return View(vm);
        }

        // ===== HELPERS =====
        private async Task<AdminStatsVm> GetStatsAsync()
        {
            var now = DateTime.UtcNow;
            var totalUsers = await _context.User.CountAsync();
            var activeUsers = await _context.User.CountAsync(u => u.IsActive == 1);
            var premiumUsers = await _context.User.CountAsync(u => u.IsPremium && u.PremiumUntil != null && u.PremiumUntil >= now);
            return new AdminStatsVm { TotalUsers = totalUsers, ActiveUsers = activeUsers, PremiumUsers = premiumUsers };
        }

        private IActionResult RedirectBackToUserOrIndex(int userId)
        {
            var referer = Request.Headers.Referer.ToString();
            if (!string.IsNullOrWhiteSpace(referer) &&
                referer.Contains("/AdminPanel/UserDetails/", StringComparison.OrdinalIgnoreCase))
            {
                return RedirectToAction(nameof(UserDetails), new { id = userId });
            }

            return RedirectToAction(nameof(Index));
        }

        private async Task LogAsync(string action, int? targetUserId = null, string? targetEmail = null, string? details = null)
        {
            var actorIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int.TryParse(actorIdStr, out var actorId);
            var actorEmail = User.FindFirstValue(ClaimTypes.Email) ?? "";

            _context.AdminActionLogs.Add(new AdminActionLog
            {
                ActorUserId = actorId,
                ActorEmail = actorEmail,
                TargetUserId = targetUserId,
                TargetEmail = targetEmail,
                Action = action,
                Details = details,
                CreatedUtc = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();
        }
    }

    // ===== VIEW MODELS =====
    public class AdminDashboardVm
    {
        public string? Q { get; set; }
        public int? RoleId { get; set; }
        public bool? Premium { get; set; }
        public bool? Active { get; set; }

        public int Page { get; set; }
        public int PageSize { get; set; }
        public int Total { get; set; }
        public int TotalPages { get; set; }

        public AdminStatsVm Stats { get; set; } = new();
        public List<AdminRoleVm> Roles { get; set; } = new();
        public List<AdminUserRowVm> Users { get; set; } = new();
    }

    public class AdminStatsVm
    {
        public int TotalUsers { get; set; }
        public int ActiveUsers { get; set; }
        public int PremiumUsers { get; set; }
    }

    public class AdminRoleVm
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class AdminUserRowVm
    {
        public int UserId { get; set; }
        public string Username { get; set; } = "";
        public string Mail { get; set; } = "";
        public int RoleId { get; set; }
        public string RoleName { get; set; } = "";
        public bool IsActive { get; set; }
        public bool IsPremium { get; set; }
        public DateTime? PremiumUntil { get; set; }

        public static AdminUserRowVm From(User u) => new AdminUserRowVm
        {
            UserId = u.UserId,
            Username = u.Username,
            Mail = u.Mail,
            RoleId = u.RoleId,
            RoleName = u.Role?.Name ?? ((Roles)u.RoleId).ToString(),
            IsActive = u.IsActive == 1,
            IsPremium = u.IsPremium,
            PremiumUntil = u.PremiumUntil
        };
    }

    public class AdminUserDetailsVm
    {
        public AdminUserRowVm User { get; set; } = new();
        public List<AdminRoleVm> Roles { get; set; } = new();
    }

    public class AdminLogsVm
    {
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int Total { get; set; }
        public int TotalPages { get; set; }
        public List<AdminActionLog> Logs { get; set; } = new();
    }
}
