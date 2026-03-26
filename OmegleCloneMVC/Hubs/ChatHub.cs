using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace OmegleCloneMVC.Hubs
{
    public class ChatHub : Hub
    {
        private static int OnlineCount = 0;
        private static readonly object Sync = new();

        private static readonly ConcurrentDictionary<string, string> UserInterests = new();
        private static readonly ConcurrentDictionary<string, string> UserGenders = new();
        private static readonly ConcurrentDictionary<string, string> UserGenderFilters = new();
        private static readonly ConcurrentDictionary<string, bool> UserIsPremium = new();

        private static readonly ConcurrentDictionary<string, string> Pairs = new();
        private static readonly ConcurrentDictionary<string, string> SkipNextPartner = new();

        // waiting (FAST)
        private static readonly HashSet<string> WaitingPremium = new();
        private static readonly HashSet<string> WaitingFree = new();

        // anti-spam / throttles
        private static readonly ConcurrentDictionary<string, long> LastTypingMs = new();
        private static readonly ConcurrentDictionary<string, long> LastIceMs = new();
        private static readonly ConcurrentDictionary<string, long> LastMsgMs = new();

        private record MatchFoundPayload(bool initiator, string partnerGender, string? commonInterest);

        public Task Ping() => Clients.Caller.SendAsync("Pong", DateTime.UtcNow.ToString("O"));

        public override async Task OnConnectedAsync()
        {
            var http = Context.GetHttpContext();

            var gender = (http?.Request.Query["gender"].ToString() ?? "").Trim().ToLowerInvariant();
            var interest = (http?.Request.Query["interest"].ToString() ?? "").Trim().ToLowerInvariant();
            var genderFilter = (http?.Request.Query["genderFilter"].ToString() ?? "").Trim().ToLowerInvariant();

            // validate / normalize
            gender = gender is "male" or "female" ? gender : "";
            genderFilter = genderFilter is "male" or "female" or "any" ? genderFilter : "any";
            if (interest.Length > 32) interest = interest[..32];

            // premium: NO DB (claim from RefreshPremium)
            var isPremium = Context.User?.HasClaim("isPremium", "true") == true;

            // ENFORCE: free user => any
            if (!isPremium) genderFilter = "any";

            UserIsPremium[Context.ConnectionId] = isPremium;
            UserInterests[Context.ConnectionId] = interest;
            UserGenders[Context.ConnectionId] = gender;
            UserGenderFilters[Context.ConnectionId] = genderFilter;

            System.Threading.Interlocked.Increment(ref OnlineCount);
            await Clients.All.SendAsync("UpdateOnlineUsers", OnlineCount);

            await Clients.Caller.SendAsync("Searching");

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? ex)
        {
            string? partner = null;
            string? partnersNewPartner = null;

            lock (Sync)
            {
                RemoveFromWaitingUnsafe(Context.ConnectionId);
                SkipNextPartner.TryRemove(Context.ConnectionId, out _);

                if (Pairs.TryGetValue(Context.ConnectionId, out partner))
                {
                    Pairs.TryRemove(partner, out _);
                    Pairs.TryRemove(Context.ConnectionId, out _);
                    SkipNextPartner[partner] = Context.ConnectionId;
                }

                if (!string.IsNullOrEmpty(partner) && UserGenders.ContainsKey(partner))
                {
                    UserIsPremium.TryGetValue(partner, out var pPremium);
                    AddToWaitingUnsafe(partner, pPremium);

                    partnersNewPartner = TryFindPartnerPriorityUnsafe(partner, pPremium);
                    if (partnersNewPartner != null)
                    {
                        RemoveFromWaitingUnsafe(partner);
                        RemoveFromWaitingUnsafe(partnersNewPartner);

                        Pairs[partner] = partnersNewPartner;
                        Pairs[partnersNewPartner] = partner;
                    }
                }
            }

            if (!string.IsNullOrEmpty(partner))
                await Clients.Client(partner).SendAsync("PartnerDisconnected");

            if (!string.IsNullOrEmpty(partnersNewPartner) && !string.IsNullOrEmpty(partner))
                await NotifyPaired(partner, partnersNewPartner);
            else if (!string.IsNullOrEmpty(partner))
                await Clients.Client(partner).SendAsync("Searching");

            System.Threading.Interlocked.Decrement(ref OnlineCount);
            await Clients.All.SendAsync("UpdateOnlineUsers", OnlineCount);

            // cleanup state
            UserInterests.TryRemove(Context.ConnectionId, out _);
            UserGenders.TryRemove(Context.ConnectionId, out _);
            UserGenderFilters.TryRemove(Context.ConnectionId, out _);
            UserIsPremium.TryRemove(Context.ConnectionId, out _);

            LastTypingMs.TryRemove(Context.ConnectionId, out _);
            LastIceMs.TryRemove(Context.ConnectionId, out _);
            LastMsgMs.TryRemove(Context.ConnectionId, out _);

            await base.OnDisconnectedAsync(ex);
        }

        public async Task StartMatch()
        {
            if (!UserGenders.TryGetValue(Context.ConnectionId, out var g) || string.IsNullOrWhiteSpace(g))
            {
                await Clients.Caller.SendAsync("Searching");
                return;
            }

            string? partner = null;
            UserIsPremium.TryGetValue(Context.ConnectionId, out var callerPremium);

            lock (Sync)
            {
                if (Pairs.ContainsKey(Context.ConnectionId))
                    return;

                AddToWaitingUnsafe(Context.ConnectionId, callerPremium);

                partner = TryFindPartnerPriorityUnsafe(Context.ConnectionId, callerPremium);
                if (partner != null)
                {
                    RemoveFromWaitingUnsafe(Context.ConnectionId);
                    RemoveFromWaitingUnsafe(partner);

                    Pairs[Context.ConnectionId] = partner;
                    Pairs[partner] = Context.ConnectionId;
                }
            }

            if (partner == null)
            {
                await Clients.Caller.SendAsync("Searching");
                return;
            }

            await NotifyPaired(Context.ConnectionId, partner);
        }

        public async Task Next()
        {
            string? oldPartner = null;
            string? newPartner = null;

            UserIsPremium.TryGetValue(Context.ConnectionId, out var callerPremium);

            lock (Sync)
            {
                if (Pairs.TryGetValue(Context.ConnectionId, out oldPartner))
                {
                    Pairs.TryRemove(oldPartner, out _);
                    Pairs.TryRemove(Context.ConnectionId, out _);

                    SkipNextPartner[Context.ConnectionId] = oldPartner;
                    SkipNextPartner[oldPartner] = Context.ConnectionId;
                }

                AddToWaitingUnsafe(Context.ConnectionId, callerPremium);

                if (!string.IsNullOrEmpty(oldPartner) && UserGenders.ContainsKey(oldPartner))
                {
                    UserIsPremium.TryGetValue(oldPartner, out var partnerPremium);
                    AddToWaitingUnsafe(oldPartner, partnerPremium);
                }

                newPartner = TryFindPartnerPriorityUnsafe(Context.ConnectionId, callerPremium);
                if (newPartner != null)
                {
                    RemoveFromWaitingUnsafe(Context.ConnectionId);
                    RemoveFromWaitingUnsafe(newPartner);

                    Pairs[Context.ConnectionId] = newPartner;
                    Pairs[newPartner] = Context.ConnectionId;
                }
            }

            if (!string.IsNullOrEmpty(oldPartner))
                await Clients.Client(oldPartner).SendAsync("PartnerDisconnected");

            await Clients.Caller.SendAsync("PartnerDisconnected");

            if (newPartner == null)
            {
                await Clients.Caller.SendAsync("Searching");
                return;
            }

            await NotifyPaired(Context.ConnectionId, newPartner);
        }

        // ===== signaling/chat =====

        public async Task SendMessage(string message)
        {
            // rate-limit: ~12 msg/sec per user
            var now = Environment.TickCount64;
            var last = LastMsgMs.GetOrAdd(Context.ConnectionId, 0);
            if (now - last < 80) return;
            LastMsgMs[Context.ConnectionId] = now;

            if (Pairs.TryGetValue(Context.ConnectionId, out var partner))
                await Clients.Client(partner).SendAsync("ReceiveMessage", message);
        }

        public async Task SendOffer(object offer)
        {
            if (Pairs.TryGetValue(Context.ConnectionId, out var partner))
                await Clients.Client(partner).SendAsync("ReceiveOffer", offer);
        }

        public async Task SendAnswer(object answer)
        {
            if (Pairs.TryGetValue(Context.ConnectionId, out var partner))
                await Clients.Client(partner).SendAsync("ReceiveAnswer", answer);
        }

        public async Task SendIceCandidate(object candidate)
        {
            // throttle ICE spam
            var now = Environment.TickCount64;
            var last = LastIceMs.GetOrAdd(Context.ConnectionId, 0);
            if (now - last < 20) return; // max ~50/sec
            LastIceMs[Context.ConnectionId] = now;

            if (Pairs.TryGetValue(Context.ConnectionId, out var partner))
                await Clients.Client(partner).SendAsync("ReceiveIceCandidate", candidate);
        }

        public async Task SendTyping()
        {
            // throttle typing
            var now = Environment.TickCount64;
            var last = LastTypingMs.GetOrAdd(Context.ConnectionId, 0);
            if (now - last < 400) return;
            LastTypingMs[Context.ConnectionId] = now;

            if (Pairs.TryGetValue(Context.ConnectionId, out var partner))
                await Clients.Client(partner).SendAsync("ReceiveTyping");
        }

        // Optional: quick debug (useful during local load test)
        public Task<object> DebugState()
        {
            lock (Sync)
            {
                return Task.FromResult<object>(new
                {
                    online = OnlineCount,
                    pairs = Pairs.Count / 2,
                    waitingPremium = WaitingPremium.Count,
                    waitingFree = WaitingFree.Count
                });
            }
        }

        // ===== waiting helpers (Sync lock only) =====

        private static void AddToWaitingUnsafe(string id, bool premium)
        {
            WaitingPremium.Remove(id);
            WaitingFree.Remove(id);

            if (premium) WaitingPremium.Add(id);
            else WaitingFree.Add(id);
        }

        private static void RemoveFromWaitingUnsafe(string id)
        {
            WaitingPremium.Remove(id);
            WaitingFree.Remove(id);
        }

        private string? TryFindPartnerPriorityUnsafe(string callerId, bool callerPremium)
        {
            var first = callerPremium ? WaitingPremium : WaitingFree;
            var second = callerPremium ? WaitingFree : WaitingPremium;

            var p = TryFindInSetUnsafe(callerId, first);
            return p ?? TryFindInSetUnsafe(callerId, second);
        }

        private string? TryFindInSetUnsafe(string callerId, HashSet<string> set)
        {
            UserInterests.TryGetValue(callerId, out var callerInterest);
            UserGenders.TryGetValue(callerId, out var callerGender);
            UserGenderFilters.TryGetValue(callerId, out var callerFilter);
            UserIsPremium.TryGetValue(callerId, out var callerPremium);

            SkipNextPartner.TryGetValue(callerId, out var skipId);

            List<string>? stale = null;

            foreach (var candidate in set)
            {
                if (candidate == callerId) continue;
                if (!string.IsNullOrEmpty(skipId) && candidate == skipId) continue;
                if (Pairs.ContainsKey(candidate)) continue;

                if (!UserGenders.ContainsKey(candidate))
                {
                    stale ??= new List<string>(4);
                    stale.Add(candidate);
                    continue;
                }

                if (!IsCompatible(callerGender, callerFilter, callerPremium, candidate, callerInterest))
                    continue;

                if (stale != null)
                {
                    foreach (var s in stale) set.Remove(s);
                }

                return candidate;
            }

            if (stale != null)
            {
                foreach (var s in stale) set.Remove(s);
            }

            return null;
        }

        private bool IsCompatible(string? callerGender, string? callerFilter, bool callerPremium, string candidateId, string? callerInterest)
        {
            UserInterests.TryGetValue(candidateId, out var candidateInterest);
            UserGenders.TryGetValue(candidateId, out var candidateGender);
            UserGenderFilters.TryGetValue(candidateId, out var candidateFilter);
            UserIsPremium.TryGetValue(candidateId, out var candidatePremium);

            // interest match only if BOTH specified
            if (!string.IsNullOrWhiteSpace(callerInterest) &&
                !string.IsNullOrWhiteSpace(candidateInterest) &&
                !callerInterest.Equals(candidateInterest, StringComparison.OrdinalIgnoreCase))
                return false;

            // caller filter works only if premium
            if (callerPremium && !string.IsNullOrWhiteSpace(callerFilter) && callerFilter != "any")
            {
                if (!string.Equals(candidateGender, callerFilter, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // candidate filter works only if premium
            if (candidatePremium && !string.IsNullOrWhiteSpace(candidateFilter) && candidateFilter != "any")
            {
                if (!string.Equals(callerGender, candidateFilter, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private async Task NotifyPaired(string a, string b)
        {
            SkipNextPartner.TryRemove(a, out _);
            SkipNextPartner.TryRemove(b, out _);

            var aGender = UserGenders.TryGetValue(a, out var ag) ? ag : "";
            var bGender = UserGenders.TryGetValue(b, out var bg) ? bg : "";

            var aInterest = UserInterests.TryGetValue(a, out var ai) ? ai : "";
            var bInterest = UserInterests.TryGetValue(b, out var bi) ? bi : "";

            string? common = null;
            if (!string.IsNullOrWhiteSpace(aInterest) &&
                !string.IsNullOrWhiteSpace(bInterest) &&
                aInterest.Equals(bInterest, StringComparison.OrdinalIgnoreCase))
            {
                common = aInterest;
            }

            var aInitiator = string.CompareOrdinal(a, b) < 0;

            await Clients.Client(a).SendAsync("MatchFound", new MatchFoundPayload(aInitiator, bGender, common));
            await Clients.Client(b).SendAsync("MatchFound", new MatchFoundPayload(!aInitiator, aGender, common));
        }
    }
}
