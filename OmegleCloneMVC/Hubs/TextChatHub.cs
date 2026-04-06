using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;

namespace OmegleCloneMVC.Hubs
{
    public class TextChatHub : Hub
    {
        private const int  MaxMessageLength  = 500;
        private const int  MaxInterestLength = 32;
        private const long MsgRateLimitMs    = 80;   // ~12 msg/sec
        private const long TypingRateLimitMs = 400;  // ~2-3/sec
        private const long NextRateLimitMs   = 1500; // min 1.5 s between Next()

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
            var interest = SanitizeInterest(
                Context.GetHttpContext()?.Request.Query["interest"].ToString());

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

            lock (Sync)
            {
                if (Pairs.TryRemove(Context.ConnectionId, out partner) && partner is not null)
                    Pairs.TryRemove(partner, out _);
                else
                    Waiting.Remove(Context.ConnectionId);
            }

            PurgeConnectionState(Context.ConnectionId);

            if (partner is not null)
                await Clients.Client(partner).SendAsync("PartnerDisconnected");

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
