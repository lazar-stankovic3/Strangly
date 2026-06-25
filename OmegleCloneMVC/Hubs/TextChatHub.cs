using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OmegleCloneMVC.Data;
using OmegleCloneMVC.Models;

namespace OmegleCloneMVC.Hubs
{
    public class TextChatHub : Hub
    {
        private readonly OmegleCloneMVCContext _db;

        public TextChatHub(OmegleCloneMVCContext db)
        {
            _db = db;
        }

        private const int  MaxMessageLength  = 500;
        private const int  MaxInterestLength = 32;
        private const long MsgRateLimitMs    = 80;   // ~12 msg/sec
        private const long TypingRateLimitMs = 400;  // ~2-3/sec
        private const long NextRateLimitMs   = 1500; // min 1.5 s between Next()

        // ── Reporting / abuse ───────────────────────────────────────
        private const int ReportBanThreshold = 5;
        private static readonly TimeSpan ReportWindow = TimeSpan.FromHours(1);
        private static readonly TimeSpan BanDuration   = TimeSpan.FromHours(2);
        private static readonly ConcurrentDictionary<string, string> ConnectionIps = new();

        private static readonly object Sync = new();

        // Waiting uses HashSet for O(1) add/remove/contains (under Sync lock)
        private static readonly HashSet<string>                      Waiting       = new();
        private static readonly ConcurrentDictionary<string, string> Pairs         = new();
        private static readonly ConcurrentDictionary<string, string> UserInterests = new();
        private static readonly ConcurrentDictionary<string, string> SkipNext      = new();

        private static readonly ConcurrentDictionary<string, long> LastMsg    = new();
        private static readonly ConcurrentDictionary<string, long> LastTyping = new();
        private static readonly ConcurrentDictionary<string, long> LastNext   = new();

        private static readonly Regex InterestRe = new(@"[^\p{L}\p{N} ]", RegexOptions.Compiled);

        // ══════════════════════════════════════════════════════════
        // Connect
        // ══════════════════════════════════════════════════════════
        public override async Task OnConnectedAsync()
        {
            var http = Context.GetHttpContext();
            var ip = http?.Connection.RemoteIpAddress?.ToString();

            if (ChatAbuseGuard.IsBanned(ip))
            {
                await Clients.Caller.SendAsync("Banned");
                Context.Abort();
                return;
            }

            if (!string.IsNullOrWhiteSpace(ip))
                ConnectionIps[Context.ConnectionId] = ip;

            var interest = SanitizeInterest(http?.Request.Query["interest"].ToString());

            UserInterests[Context.ConnectionId] = interest;

            string? partner = null;

            lock (Sync)
            {
                partner = FindPartner(Context.ConnectionId, interest);
                if (partner is not null)
                {
                    Waiting.Remove(partner);
                    Pairs[Context.ConnectionId] = partner;
                    Pairs[partner]              = Context.ConnectionId;
                }
                else
                {
                    Waiting.Add(Context.ConnectionId);
                }
            }

            if (partner is not null)
            {
                await Clients.Client(partner).SendAsync("ReceiveMessage", "✅ Partner connected.");
                await Clients.Caller.SendAsync("ReceiveMessage", "✅ Partner connected.");
            }

            await base.OnConnectedAsync();
        }

        // ══════════════════════════════════════════════════════════
        // Disconnect
        // ══════════════════════════════════════════════════════════
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            string? partner = null;
            string? partnerNewMatch = null;

            lock (Sync)
            {
                Waiting.Remove(Context.ConnectionId);

                if (Pairs.TryRemove(Context.ConnectionId, out partner) && partner is not null)
                {
                    Pairs.TryRemove(partner, out _);

                    if (UserInterests.ContainsKey(partner))
                    {
                        Waiting.Add(partner);

                        UserInterests.TryGetValue(partner, out var partnerInterest);
                        partnerNewMatch = FindPartner(partner, partnerInterest ?? "");
                        if (partnerNewMatch is not null)
                        {
                            Waiting.Remove(partner);
                            Waiting.Remove(partnerNewMatch);
                            Pairs[partner]            = partnerNewMatch;
                            Pairs[partnerNewMatch]     = partner;
                        }
                    }
                }
            }

            PurgeConnectionState(Context.ConnectionId);

            if (partner is not null)
                await Clients.Client(partner).SendAsync("PartnerDisconnected");

            if (partnerNewMatch is not null)
            {
                await Clients.Client(partner!).SendAsync("ReceiveMessage", "✅ Partner connected.");
                await Clients.Client(partnerNewMatch).SendAsync("ReceiveMessage", "✅ Partner connected.");
            }

            await base.OnDisconnectedAsync(exception);
        }

        // ══════════════════════════════════════════════════════════
        // SendMessage
        // ══════════════════════════════════════════════════════════
        public async Task SendMessage(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;

            var now = Environment.TickCount64;
            if (now - LastMsg.GetOrAdd(Context.ConnectionId, 0L) < MsgRateLimitMs) return;
            LastMsg[Context.ConnectionId] = now;

            msg = StripControlChars(msg);
            if (msg.Length > MaxMessageLength) msg = msg[..MaxMessageLength];
            if (string.IsNullOrWhiteSpace(msg)) return;

            if (!Pairs.TryGetValue(Context.ConnectionId, out var partner)) return;
            await Clients.Client(partner).SendAsync("ReceiveMessage", msg);
        }

        // ══════════════════════════════════════════════════════════
        // SendTyping
        // ══════════════════════════════════════════════════════════
        public async Task SendTyping()
        {
            var now = Environment.TickCount64;
            if (now - LastTyping.GetOrAdd(Context.ConnectionId, 0L) < TypingRateLimitMs) return;
            LastTyping[Context.ConnectionId] = now;

            if (!Pairs.TryGetValue(Context.ConnectionId, out var partner)) return;
            await Clients.Client(partner).SendAsync("ReceiveTyping");
        }

        // ══════════════════════════════════════════════════════════
        // Next
        // ══════════════════════════════════════════════════════════
        public async Task Next()
        {
            var now = Environment.TickCount64;
            if (now - LastNext.GetOrAdd(Context.ConnectionId, 0L) < NextRateLimitMs) return;
            LastNext[Context.ConnectionId] = now;

            UserInterests.TryGetValue(Context.ConnectionId, out var myInterest);

            string? oldPartner = null;
            string? newPartner = null;

            lock (Sync)
            {
                if (Pairs.TryRemove(Context.ConnectionId, out oldPartner) && oldPartner is not null)
                {
                    Pairs.TryRemove(oldPartner, out _);
                    // Both skip each other on next attempt
                    SkipNext[Context.ConnectionId] = oldPartner;
                    SkipNext[oldPartner]           = Context.ConnectionId;
                    Waiting.Add(oldPartner);
                }

                Waiting.Add(Context.ConnectionId);

                newPartner = FindPartner(Context.ConnectionId, myInterest ?? "");
                if (newPartner is not null)
                {
                    Waiting.Remove(newPartner);
                    Waiting.Remove(Context.ConnectionId);
                    Pairs[Context.ConnectionId] = newPartner;
                    Pairs[newPartner]           = Context.ConnectionId;
                }
            }

            if (oldPartner is not null)
                await Clients.Client(oldPartner).SendAsync("PartnerDisconnected");

            await Clients.Caller.SendAsync("PartnerDisconnected");

            if (newPartner is not null)
            {
                await Clients.Client(newPartner).SendAsync("ReceiveMessage", "✅ Partner connected.");
                await Clients.Caller.SendAsync("ReceiveMessage", "✅ Partner connected.");
            }
        }

        // ══════════════════════════════════════════════════════════
        // Helpers
        // ══════════════════════════════════════════════════════════
        private string? FindPartner(string callerId, string callerInterest)
        {
            SkipNext.TryGetValue(callerId, out var skipId);

            // 1. Interest match
            if (!string.IsNullOrWhiteSpace(callerInterest))
            {
                foreach (var w in Waiting)
                {
                    if (w == callerId || w == skipId) continue;
                    UserInterests.TryGetValue(w, out var wi);
                    if (callerInterest.Equals(wi, StringComparison.OrdinalIgnoreCase))
                        return w;
                }
            }

            // 2. Any available
            foreach (var w in Waiting)
            {
                if (w == callerId || w == skipId) continue;
                return w;
            }

            return null;
        }

        private static void PurgeConnectionState(string id)
        {
            UserInterests.TryRemove(id, out _);
            SkipNext.TryRemove(id, out _);
            LastMsg.TryRemove(id, out _);
            LastTyping.TryRemove(id, out _);
            LastNext.TryRemove(id, out _);
            ConnectionIps.TryRemove(id, out _);
        }

        // ══════════════════════════════════════════════════════════
        // Report
        // ══════════════════════════════════════════════════════════
        public async Task ReportPartner(string? reason)
        {
            if (!Pairs.TryGetValue(Context.ConnectionId, out var partner)) return;

            reason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim();
            if (reason.Length > 64) reason = reason[..64];

            ConnectionIps.TryGetValue(Context.ConnectionId, out var reporterIp);
            ConnectionIps.TryGetValue(partner, out var reportedIp);

            try
            {
                _db.Reports.Add(new Report
                {
                    ReporterIp = reporterIp ?? "unknown",
                    ReportedIp = reportedIp ?? "unknown",
                    Reason     = reason,
                    ChatType   = "text",
                    CreatedUtc = DateTime.UtcNow
                });
                await _db.SaveChangesAsync();

                if (!string.IsNullOrWhiteSpace(reportedIp))
                {
                    var since = DateTime.UtcNow - ReportWindow;
                    var recentCount = await _db.Reports.CountAsync(r => r.ReportedIp == reportedIp && r.CreatedUtc >= since);
                    if (recentCount >= ReportBanThreshold)
                        ChatAbuseGuard.Ban(reportedIp, BanDuration);
                }
            }
            catch
            {
                // Reporting is best-effort; never break the chat flow over a logging failure.
            }

            // Move the reporter on to a new partner, same as a manual Next().
            await Next();
        }

        private static string SanitizeInterest(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            raw = InterestRe.Replace(raw.Trim().ToLowerInvariant(), " ").Trim();
            while (raw.Contains("  ")) raw = raw.Replace("  ", " ");
            return raw.Length > MaxInterestLength ? raw[..MaxInterestLength] : raw;
        }

        private static string StripControlChars(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (c is ' ' or '\t' || (c > '\x1F' && c != '\x7F'))
                    sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
