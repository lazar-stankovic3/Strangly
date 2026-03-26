using Microsoft.EntityFrameworkCore;
using OmegleCloneMVC.Data;

public class PremiumExpiryHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PremiumExpiryHostedService> _logger;

    public PremiumExpiryHostedService(IServiceScopeFactory scopeFactory, ILogger<PremiumExpiryHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // radi na svakih 1h
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OmegleCloneMVCContext>();

                var nowUtc = DateTime.UtcNow;

                var expiredUsers = await db.User
                    .Where(u => u.IsPremium && u.PremiumUntil != null && u.PremiumUntil < nowUtc)
                    .ToListAsync(stoppingToken);

                if (expiredUsers.Count > 0)
                {
                    foreach (var user in expiredUsers)
                    {
                        user.IsPremium = false;
                        user.PremiumUntil = null;
                    }

                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PremiumExpiryHostedService error");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
