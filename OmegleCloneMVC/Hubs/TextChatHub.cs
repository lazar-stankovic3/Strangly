using Microsoft.AspNetCore.SignalR;

namespace OmegleCloneMVC.Hubs
{
    public class TextChatHub : Hub
    {
        private static readonly object Sync = new();

        private static readonly Dictionary<string, string> UserInterests = new();
        private static readonly List<string> Waiting = new();
        private static readonly Dictionary<string, string> Pairs = new();

        public override async Task OnConnectedAsync()
        {
            var interest = (Context.GetHttpContext()?.Request.Query["interest"].ToString() ?? "")
                .Trim().ToLowerInvariant();

            string? partner = null;

            lock (Sync)
            {
                UserInterests[Context.ConnectionId] = interest;

                // pokušaj match: prvo isti interest, pa fallback bilo ko
                partner = Waiting.FirstOrDefault(w =>
                    w != Context.ConnectionId &&
                    UserInterests.ContainsKey(w) &&
                    !string.IsNullOrWhiteSpace(interest) &&
                    UserInterests[w] == interest);

                if (partner == null && Waiting.Count > 0)
                    partner = Waiting[0];

                if (partner != null)
                {
                    Waiting.Remove(partner);
                    Pairs[Context.ConnectionId] = partner;
                    Pairs[partner] = Context.ConnectionId;
                }
                else
                {
                    if (!Waiting.Contains(Context.ConnectionId))
                        Waiting.Add(Context.ConnectionId);
                }
            }

            if (partner != null)
            {
                await Clients.Client(partner).SendAsync("ReceiveMessage", "✅ Partner povezan.");
                await Clients.Client(Context.ConnectionId).SendAsync("ReceiveMessage", "✅ Partner povezan.");
            }

            await base.OnConnectedAsync();
        }

        public async Task SendMessage(string msg)
        {
            string? partner;
            lock (Sync)
            {
                Pairs.TryGetValue(Context.ConnectionId, out partner);
            }

            if (partner != null)
                await Clients.Client(partner).SendAsync("ReceiveMessage", msg);
        }

        public async Task SendTyping()
        {
            string? partner;
            lock (Sync)
            {
                Pairs.TryGetValue(Context.ConnectionId, out partner);
            }

            if (partner != null)
                await Clients.Client(partner).SendAsync("ReceiveTyping");
        }

        // ===== NEXT =====
        public async Task Next()
        {
            string? partner = null;

            lock (Sync)
            {
                if (Pairs.TryGetValue(Context.ConnectionId, out partner))
                {
                    Pairs.Remove(partner);
                    Pairs.Remove(Context.ConnectionId);

                    if (!Waiting.Contains(partner))
                        Waiting.Add(partner);
                }

                if (!Waiting.Contains(Context.ConnectionId))
                    Waiting.Add(Context.ConnectionId);
            }

            if (partner != null)
                await Clients.Client(partner).SendAsync("PartnerDisconnected");

            await Clients.Caller.SendAsync("PartnerDisconnected");
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            string? partner = null;

            lock (Sync)
            {
                if (Pairs.TryGetValue(Context.ConnectionId, out partner))
                {
                    Pairs.Remove(partner);
                    Pairs.Remove(Context.ConnectionId);
                }
                else
                {
                    Waiting.Remove(Context.ConnectionId);
                }

                UserInterests.Remove(Context.ConnectionId);
            }

            if (partner != null)
                await Clients.Client(partner).SendAsync("PartnerDisconnected");

            await base.OnDisconnectedAsync(exception);
        }
    }
}
