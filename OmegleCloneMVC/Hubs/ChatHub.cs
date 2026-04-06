using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;

namespace OmegleCloneMVC.Hubs
{
    public class ChatHub : Hub
    {
        // ── Limits & rate-limit windows ────────────────────────────
        private const int  MaxMessageLength   = 500;
        private const int  MaxInterestLength  = 32;
        private const long MsgRateLimitMs     = 80;   // ~12 msg/sec
        private const long IceRateLimitMs     = 30;   // ~33 ICE/sec
        private const long TypingRateLimitMs  = 400;  // ~2-3 typing/sec
        private const long NextRateLimitMs    = 1500; // min 1.5 s between Next()
        private const long OnlineBroadcastMs  = 3000; // broadcast online count at most every 3 s

        // ── Online counter ─────────────────────────────────────────
        private static int  _onlineCount;
        private static long _lastOnlineBroadcast;

        // ── Matching (Unsafe members require Sync held) ────────────
        private static readonly object       Sync          = new();
        private static readonly HashSet<string> WaitingPremium = new();
        private static readonly HashSet<string> WaitingFree    = new();

        // ── Per-connection state ───────────────────────────────────
        private static readonly ConcurrentDictionary<string, string> Pairs         = new();
        private static readonly ConcurrentDictionary<string, string> SkipNext      = new();
        private static readonly ConcurrentDictionary<string, string> UserInterests = new();
        private static readonly ConcurrentDictionary<string, string> UserGenders   = new();
        private static readonly ConcurrentDictionary<string, string> UserFilters   = new();
        private static readonly ConcurrentDictionary<string, bool>   UserPremium   = new();

        // ── Rate-limit timestamps ──────────────────────────────────
        private static readonly ConcurrentDictionary<string, long> LastMsg    = new();
        private static readonly ConcurrentDictionary<string, long> LastIce    = new();
        private static readonly ConcurrentDictionary<string, long> LastTyping = new();
        private static readonly ConcurrentDictionary<string, long> LastNext   = new();

        // ── Compiled sanitization regex ────────────────────────────
        private static readonly Regex InterestRe = new(@"[^\p{L}\p{N} ]", RegexOptions.Compiled);

        private record MatchPayload(bool initiator, string partnerGender, string? commonInterest);

        // ── Keep-alive ping ────────────────────────────────────────
        public Task Ping() => Clients.Caller.SendAsync("Pong");

        // ══════════════════════════════════════════════════════════
        // Connect
        // ══════════════════════════════════════════════════════════
        public override async Task OnConnectedAsync()
        {
            var http = Context.GetHttpContext();

            var gender   = Sanitize(http?.Request.Query["gender"],       "male", "female") ?? "";
            var filter   = Sanitize(http?.Request.Query["genderFilter"], "male", "female", "any") ?? "any";
            var interest = SanitizeInterest(http?.Request.Query["interest"]);

            var isPremium = Context.User?.HasClaim("isPremium", "true") == true;
            if (!isPremium) filter = "any";   // enforce: free users always "any"

            UserPremium[Context.ConnectionId]   = isPremium;
            UserInterests[Context.ConnectionId] = interest;
            UserGenders[Context.ConnectionId]   = gender;
            UserFilters[Context.ConnectionId]   = filter;

            var count = Interlocked.Increment(ref _onlineCount);
            await BroadcastOnlineCountIfDue(count);

            await Clients.Caller.SendAsync("Searching");
            await base.OnConnectedAsync();
        }

        // ══════════════════════════════════════════════════════════
        // Disconnect
        // ══════════════════════════════════════════════════════════
        public override async Task OnDisconnectedAsync(Exception? ex)
        {
            string? partner          = null;
            string? partnerNewMatch  = null;

            lock (Sync)
            {
                RemoveWaitingUnsafe(Context.ConnectionId);
                SkipNext.TryRemove(Context.ConnectionId, out _);

                if (Pairs.TryRemove(Context.ConnectionId, out partner) && partner is not null)
                {
                    Pairs.TryRemove(partner, out _);
                    SkipNext[partner] = Context.ConnectionId;

                    if (UserGenders.ContainsKey(partner))
                    {
                        UserPremium.TryGetValue(partner, out var pPremium);
                        AddWaitingUnsafe(partner, pPremium);

                        partnerNewMatch = FindPartnerUnsafe(partner, pPremium);
                        if (partnerNewMatch is not null)
                        {
                            RemoveWaitingUnsafe(partner);
                            RemoveWaitingUnsafe(partnerNewMatch);
                            Pairs[partner]         = partnerNewMatch;
                            Pairs[partnerNewMatch] = partner;
                        }
                    }
                }
            }

            // Clean up all per-connection state
            PurgeConnectionState(Context.ConnectionId);

            // Notify partner
            if (partner is not null)
                await Clients.Client(partner).SendAsync("PartnerDisconnected");

            if (partnerNewMatch is not null)
                await NotifyPaired(partner!, partnerNewMatch);
            else if (partner is not null)
                await Clients.Client(partner).SendAsync("Searching");

            // Decrement (never go below zero)
            int count;
            do { count = _onlineCount; }
            while (count > 0 && Interlocked.CompareExchange(ref _onlineCount, count - 1, count) != count);
            await BroadcastOnlineCountIfDue(Math.Max(0, count - 1));

            await base.OnDisconnectedAsync(ex);
        }

        // ══════════════════════════════════════════════════════════
        // StartMatch
        // ══════════════════════════════════════════════════════════
        public async Task StartMatch()
        {
            string? partner = null;
            UserPremium.TryGetValue(Context.ConnectionId, out var isPremium);

            lock (Sync)
            {
                if (Pairs.ContainsKey(Context.ConnectionId)) return; // already paired

                AddWaitingUnsafe(Context.ConnectionId, isPremium);
                partner = FindPartnerUnsafe(Context.ConnectionId, isPremium);

                if (partner is not null)
                {
                    RemoveWaitingUnsafe(Context.ConnectionId);
                    RemoveWaitingUnsafe(partner);
                    Pairs[Context.ConnectionId] = partner;
                    Pairs[partner]              = Context.ConnectionId;
                }
            }

            if (partner is null)
            {
                await Clients.Caller.SendAsync("Searching");
                return;
            }

            await NotifyPaired(Context.ConnectionId, partner);
        }

        // ══════════════════════════════════════════════════════════
        // Next
        // ══════════════════════════════════════════════════════════
        public async Task Next()
        {
            // Server-side rate limit: prevent rapid cycling
            var now  = Environment.TickCount64;
            var last = LastNext.GetOrAdd(Context.ConnectionId, 0L);
            if (now - last < NextRateLimitMs) return;
            LastNext[Context.ConnectionId] = now;

            string? oldPartner = null;
            string? newPartner = null;
            UserPremium.TryGetValue(Context.ConnectionId, out var isPremium);

            lock (Sync)
            {
                if (Pairs.TryRemove(Context.ConnectionId, out oldPartner) && oldPartner is not null)
                {
                    Pairs.TryRemove(oldPartner, out _);
                    SkipNext[Context.ConnectionId] = oldPartner;
                    SkipNext[oldPartner]           = Context.ConnectionId;
                }

                AddWaitingUnsafe(Context.ConnectionId, isPremium);

                if (oldPartner is not null && UserGenders.ContainsKey(oldPartner))
                {
                    UserPremium.TryGetValue(oldPartner, out var opPremium);
                    AddWaitingUnsafe(oldPartner, opPremium);
                }

                newPartner = FindPartnerUnsafe(Context.ConnectionId, isPremium);
                if (newPartner is not null)
                {
                    RemoveWaitingUnsafe(Context.ConnectionId);
                    RemoveWaitingUnsafe(newPartner);
                    Pairs[Context.ConnectionId] = newPartner;
                    Pairs[newPartner]           = Context.ConnectionId;
                }
            }

            if (oldPartner is not null)
                await Clients.Client(oldPartner).SendAsync("PartnerDisconnected");

            await Clients.Caller.SendAsync("PartnerDisconnected");

            if (newPartner is null)
            {
                await Clients.Caller.SendAsync("Searching");
                return;
            }

            await NotifyPaired(Context.ConnectionId, newPartner);
        }

        // ══════════════════════════════════════════════════════════
        // Signaling & chat
        // ══════════════════════════════════════════════════════════
        public async Task SendMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            var now = Environment.TickCount64;
            if (now - LastMsg.GetOrAdd(Context.ConnectionId, 0L) < MsgRateLimitMs) return;
            LastMsg[Context.ConnectionId] = now;

            message = StripControlChars(message);
            if (message.Length > MaxMessageLength)
                message = message[..MaxMessageLength];
            if (string.IsNullOrWhiteSpace(message)) return;

            if (!Pairs.TryGetValue(Context.ConnectionId, out var partner)) return;
            await Clients.Client(partner).SendAsync("ReceiveMessage", message);
        }

        public async Task SendOffer(object? offer)
        {
            if (offer is null) return;
            if (!Pairs.TryGetValue(Context.ConnectionId, out var partner)) return;
            await Clients.Client(partner).SendAsync("ReceiveOffer", offer);
        }

        public async Task SendAnswer(object? answer)
        {
            if (answer is null) return;
            if (!Pairs.TryGetValue(Context.ConnectionId, out var partner)) return;
            await Clients.Client(partner).SendAsync("ReceiveAnswer", answer);
        }

        public async Task SendIceCandidate(object? candidate)
        {
            if (candidate is null) return;

            var now = Environment.TickCount64;
            if (now - LastIce.GetOrAdd(Context.ConnectionId, 0L) < IceRateLimitMs) return;
            LastIce[Context.ConnectionId] = now;

            if (!Pairs.TryGetValue(Context.ConnectionId, out var partner)) return;
            await Clients.Client(partner).SendAsync("ReceiveIceCandidate", candidate);
        }

        public async Task SendTyping()
        {
            var now = Environment.TickCount64;
            if (now - LastTyping.GetOrAdd(Context.ConnectionId, 0L) < TypingRateLimitMs) return;
            LastTyping[Context.ConnectionId] = now;

            if (!Pairs.TryGetValue(Context.ConnectionId, out var partner)) return;
            await Clients.Client(partner).SendAsync("ReceiveTyping");
        }

        // ══════════════════════════════════════════════════════════
        // Internal – matching (all require Sync lock)
        // ══════════════════════════════════════════════════════════
        private static void AddWaitingUnsafe(string id, bool premium)
        {
            WaitingPremium.Remove(id);
            WaitingFree.Remove(id);
            if (premium) WaitingPremium.Add(id);
            else WaitingFree.Add(id);
        }

        private static void RemoveWaitingUnsafe(string id)
        {
            WaitingPremium.Remove(id);
            WaitingFree.Remove(id);
        }

        private string? FindPartnerUnsafe(string callerId, bool callerPremium)
        {
            // Premium searches premium first, then free; free searches free first, then premium
            var first  = callerPremium ? WaitingPremium : WaitingFree;
            var second = callerPremium ? WaitingFree    : WaitingPremium;
            return ScanSetUnsafe(callerId, first) ?? ScanSetUnsafe(callerId, second);
        }

        private string? ScanSetUnsafe(string callerId, HashSet<string> set)
        {
            UserInterests.TryGetValue(callerId, out var callerInterest);
            UserGenders.TryGetValue(callerId, out var callerGender);
            UserFilters.TryGetValue(callerId, out var callerFilter);
            UserPremium.TryGetValue(callerId, out var callerPremium);
            SkipNext.TryGetValue(callerId, out var skipId);

            List<string>? stale = null;

            foreach (var cid in set)
            {
                if (cid == callerId) continue;
                if (skipId is not null && cid == skipId) continue;
                if (Pairs.ContainsKey(cid)) continue;

                // Stale: user already cleaned up (e.g. crashed)
                if (!UserGenders.ContainsKey(cid))
                {
                    (stale ??= new()).Add(cid);
                    continue;
                }

                if (!Compatible(callerGender, callerFilter, callerPremium, cid, callerInterest))
                    continue;

                if (stale is not null) foreach (var s in stale) set.Remove(s);
                return cid;
            }

            if (stale is not null) foreach (var s in stale) set.Remove(s);
            return null;
        }

        private bool Compatible(
            string? callerGender, string? callerFilter, bool callerPremium,
            string candidateId, string? callerInterest)
        {
            UserInterests.TryGetValue(candidateId, out var candInterest);
            UserGenders.TryGetValue(candidateId, out var candGender);
            UserFilters.TryGetValue(candidateId, out var candFilter);
            UserPremium.TryGetValue(candidateId, out var candPremium);

            // Interest: block only when BOTH have an interest and they differ
            if (!string.IsNullOrWhiteSpace(callerInterest) &&
                !string.IsNullOrWhiteSpace(candInterest) &&
                !callerInterest.Equals(candInterest, StringComparison.OrdinalIgnoreCase))
                return false;

            // Gender filter (premium only)
            if (callerPremium && callerFilter is not null && callerFilter != "any")
                if (!string.Equals(candGender, callerFilter, StringComparison.OrdinalIgnoreCase))
                    return false;

            if (candPremium && candFilter is not null && candFilter != "any")
                if (!string.Equals(callerGender, candFilter, StringComparison.OrdinalIgnoreCase))
                    return false;

            return true;
        }

        private async Task NotifyPaired(string a, string b)
        {
            SkipNext.TryRemove(a, out _);
            SkipNext.TryRemove(b, out _);

            var aGender   = UserGenders.TryGetValue(a, out var ag) ? ag : "";
            var bGender   = UserGenders.TryGetValue(b, out var bg) ? bg : "";
            var aInterest = UserInterests.TryGetValue(a, out var ai) ? ai : "";
            var bInterest = UserInterests.TryGetValue(b, out var bi) ? bi : "";

            string? common = (!string.IsNullOrWhiteSpace(aInterest) &&
                              !string.IsNullOrWhiteSpace(bInterest) &&
                              aInterest.Equals(bInterest, StringComparison.OrdinalIgnoreCase))
                             ? aInterest : null;

            var aIsInit = string.CompareOrdinal(a, b) < 0;
            await Clients.Client(a).SendAsync("MatchFound", new MatchPayload(aIsInit,  bGender, common));
            await Clients.Client(b).SendAsync("MatchFound", new MatchPayload(!aIsInit, aGender, common));
        }

        // ── Debounced online-count broadcast ───────────────────────
        private async Task BroadcastOnlineCountIfDue(int count)
        {
            var now  = Environment.TickCount64;
            var last = Volatile.Read(ref _lastOnlineBroadcast);
            if (now - last < OnlineBroadcastMs) return;
            // Only one caller wins the CAS; others skip to avoid duplicate broadcasts
            if (Interlocked.CompareExchange(ref _lastOnlineBroadcast, now, last) != last) return;
            await Clients.All.SendAsync("UpdateOnlineUsers", count);
        }

        // ── State cleanup ──────────────────────────────────────────
        private static void PurgeConnectionState(string id)
        {
            UserInterests.TryRemove(id, out _);
            UserGenders.TryRemove(id, out _);
            UserFilters.TryRemove(id, out _);
            UserPremium.TryRemove(id, out _);
            LastMsg.TryRemove(id, out _);
            LastIce.TryRemove(id, out _);
            LastTyping.TryRemove(id, out _);
            LastNext.TryRemove(id, out _);
        }

        // ── Input sanitization ─────────────────────────────────────
        private static string? Sanitize(string? value, params string[] allowed)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim().ToLowerInvariant();
            return allowed.Contains(value) ? value : null;
        }

        private static string SanitizeInterest(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            raw = InterestRe.Replace(raw.Trim().ToLowerInvariant(), " ")
                            .Trim();
            // Collapse multiple spaces
            while (raw.Contains("  ")) raw = raw.Replace("  ", " ");
            return raw.Length > MaxInterestLength ? raw[..MaxInterestLength] : raw;
        }

        private static string StripControlChars(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                // Allow printable chars + safe whitespace
                if (c is ' ' or '\t' || (c > '\x1F' && c != '\x7F'))
                    sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
