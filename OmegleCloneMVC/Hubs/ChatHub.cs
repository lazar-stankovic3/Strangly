using System;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OmegleCloneMVC.Data;
using System.Collections.Concurrent;

namespace OmegleCloneMVC.Hubs
{
    public class ChatHub : Hub
    {
        private readonly OmegleCloneMVCContext _context;

        public ChatHub(OmegleCloneMVCContext context)
        {
            _context = context;
        }

        private static int OnlineCount = 0;

        private static ConcurrentDictionary<string, string> UserInterests = new();
        private static ConcurrentDictionary<string, string> UserGenders = new();
        private static ConcurrentDictionary<string, string> Pairs = new();
        private static List<string> WaitingUsers = new();

        public override async Task OnConnectedAsync()
        {
            var gender = Context.GetHttpContext()?.Request.Query["gender"].ToString() ?? "Nepoznat";
            var interest = Context.GetHttpContext()?.Request.Query["interest"].ToString() ?? "";

            // ✔️ Upamti interesovanja i pol
            UserInterests[Context.ConnectionId] = interest;
            UserGenders[Context.ConnectionId] = gender;

            // ✔️ Provera premium korisnika po email claimu
            var email = Context.User?.FindFirst(ClaimTypes.Email)?.Value;
            bool isPremium = false;
            if (!string.IsNullOrEmpty(email))
            {
                var user = await _context.User.FirstOrDefaultAsync(u => u.Mail == email);
                isPremium = user?.IsPremium == true && user.PremiumUntil >= DateTime.Now;
            }

            string partner = null;
            string yourGender = null;
            string partnerGender = null;

            lock (WaitingUsers)
            {
                if (WaitingUsers.Count > 0)
                {
                    // Premium korisnici mogu birati partnera po polu
                    if (isPremium)
                    {
                        var match = WaitingUsers.FirstOrDefault(pid =>
                            UserGenders.ContainsKey(pid) &&
                            UserGenders[pid] == gender && // traži isti pol
                            pid != Context.ConnectionId);

                        if (match != null)
                        {
                            partner = match;
                            WaitingUsers.Remove(match);
                        }
                    }

                    // Ako nema partnera po polu ili nije premium, uzmi prvog dostupnog
                    if (partner == null)
                    {
                        partner = WaitingUsers[0];
                        WaitingUsers.RemoveAt(0);
                    }

                    Pairs[Context.ConnectionId] = partner;
                    Pairs[partner] = Context.ConnectionId;

                    yourGender = UserGenders[Context.ConnectionId];
                    partnerGender = UserGenders[partner];
                }
                else
                {
                    WaitingUsers.Add(Context.ConnectionId);
                }
            }

            if (partner != null)
            {
                await Clients.Client(partner).SendAsync("PartnerGender", yourGender);
                await Clients.Client(Context.ConnectionId).SendAsync("PartnerGender", partnerGender);

                var yourInterest = UserInterests[Context.ConnectionId];
                var partnerInterest = UserInterests[partner];

                if (!string.IsNullOrEmpty(yourInterest) &&
                    !string.IsNullOrEmpty(partnerInterest) &&
                    yourInterest.Equals(partnerInterest, StringComparison.OrdinalIgnoreCase))
                {
                    await Clients.Client(partner).SendAsync("CommonInterest", yourInterest);
                    await Clients.Client(Context.ConnectionId).SendAsync("CommonInterest", yourInterest);
                }

                await Clients.Client(partner).SendAsync("PartnerFound");
                await Clients.Client(Context.ConnectionId).SendAsync("PartnerFound");
            }

            Interlocked.Increment(ref OnlineCount);
            await Clients.All.SendAsync("UpdateOnlineUsers", OnlineCount);

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception ex)
        {
            if (Pairs.TryGetValue(Context.ConnectionId, out var partner))
            {
                await Clients.Client(partner).SendAsync("PartnerDisconnected");
                Pairs.TryRemove(partner, out _);
                Pairs.TryRemove(Context.ConnectionId, out _);
            }
            else
            {
                lock (WaitingUsers)
                {
                    WaitingUsers.Remove(Context.ConnectionId);
                }
            }

            Interlocked.Decrement(ref OnlineCount);
            await Clients.All.SendAsync("UpdateOnlineUsers", OnlineCount);

            UserInterests.TryRemove(Context.ConnectionId, out _);
            UserGenders.TryRemove(Context.ConnectionId, out _);

            await base.OnDisconnectedAsync(ex);
        }

        public async Task SendMessage(string message)
        {
            if (Pairs.TryGetValue(Context.ConnectionId, out var partner))
            {
                await Clients.Client(partner).SendAsync("ReceiveMessage", message);
            }
        }

        public async Task SendOffer(object offer)
        {
            if (Pairs.TryGetValue(Context.ConnectionId, out var partner))
            {
                await Clients.Client(partner).SendAsync("ReceiveOffer", offer);
            }
        }

        public async Task SendAnswer(object answer)
        {
            if (Pairs.TryGetValue(Context.ConnectionId, out var partner))
            {
                await Clients.Client(partner).SendAsync("ReceiveAnswer", answer);
            }
        }

        public async Task SendIceCandidate(object candidate)
        {
            if (Pairs.TryGetValue(Context.ConnectionId, out var partner))
            {
                await Clients.Client(partner).SendAsync("ReceiveIceCandidate", candidate);
            }
        }

        public async Task SendTyping()
        {
            if (Pairs.TryGetValue(Context.ConnectionId, out var partner))
            {
                await Clients.Client(partner).SendAsync("ReceiveTyping");
            }
        }
    }
}
