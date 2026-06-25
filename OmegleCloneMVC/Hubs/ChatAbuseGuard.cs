using System.Collections.Concurrent;

namespace OmegleCloneMVC.Hubs
{
    /// <summary>
    /// Shared between ChatHub and TextChatHub: tracks temporary IP bans triggered by
    /// repeated reports, so a banned IP is locked out of both video and text chat.
    /// </summary>
    internal static class ChatAbuseGuard
    {
        private static readonly ConcurrentDictionary<string, DateTime> BannedUntilUtc = new();

        public static bool IsBanned(string? ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            if (!BannedUntilUtc.TryGetValue(ip, out var until)) return false;
            if (until > DateTime.UtcNow) return true;
            BannedUntilUtc.TryRemove(ip, out _);
            return false;
        }

        public static void Ban(string ip, TimeSpan duration) =>
            BannedUntilUtc[ip] = DateTime.UtcNow.Add(duration);
    }
}
