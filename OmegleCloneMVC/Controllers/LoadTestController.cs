using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR.Client;
using System.Diagnostics;

namespace OmegleCloneMVC.Controllers
{
    [ApiController]
    [Route("loadtest")]
    public class LoadTestController : ControllerBase
    {
        private static readonly object _sync = new();
        private static List<HubConnection> _hubs = new();

        private static bool IsDevelopment()
        {
            // radi i bez DI
            var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
            return env.Equals("Development", StringComparison.OrdinalIgnoreCase);
        }

        // GET /loadtest/start/600
        [HttpGet("start/{count}")]
        public async Task<IActionResult> Start(int count = 200)
        {
            if (!IsDevelopment())
                return BadRequest("Load test allowed only in Development.");

            // stop old
            List<HubConnection> old;
            lock (_sync)
            {
                old = _hubs;
                _hubs = new List<HubConnection>();
            }

            foreach (var h in old)
            {
                try { await h.StopAsync(); } catch { }
                try { await h.DisposeAsync(); } catch { }
            }

            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var hubUrl = $"{baseUrl}/chatHub?gender=male&genderFilter=any&interest=";

            int connected = 0, failed = 0;
            var sw = Stopwatch.StartNew();

            var throttler = new SemaphoreSlim(50);
            var tasks = new List<Task>(count);

            for (int i = 0; i < count; i++)
            {
                await throttler.WaitAsync();

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var hub = new HubConnectionBuilder()
                            .WithUrl(hubUrl)
                            .WithAutomaticReconnect()
                            .Build();

                        await hub.StartAsync();
                        await hub.InvokeAsync("StartMatch");

                        Interlocked.Increment(ref connected);

                        lock (_sync) _hubs.Add(hub);
                    }
                    catch
                    {
                        Interlocked.Increment(ref failed);
                    }
                    finally
                    {
                        throttler.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);
            sw.Stop();

            return Ok(new
            {
                requested = count,
                connected,
                failed,
                timeSeconds = sw.Elapsed.TotalSeconds
            });
        }

        // GET /loadtest/blast?seconds=10&rate=2
        // rate = poruke u sekundi po konekciji
        [HttpGet("blast")]
        public async Task<IActionResult> Blast(int seconds = 10, int rate = 2)
        {
            if (!IsDevelopment())
                return BadRequest("Load test allowed only in Development.");

            List<HubConnection> hubs;
            lock (_sync) hubs = _hubs.ToList();

            if (hubs.Count == 0)
                return BadRequest("Run /loadtest/start/{count} first.");

            var until = DateTime.UtcNow.AddSeconds(seconds);
            int sent = 0, failed = 0;

            while (DateTime.UtcNow < until)
            {
                var tickStart = DateTime.UtcNow;

                var tasks = hubs.Select(async h =>
                {
                    for (int i = 0; i < rate; i++)
                    {
                        try
                        {
                            await h.InvokeAsync("SendMessage", "hi");
                            Interlocked.Increment(ref sent);
                        }
                        catch
                        {
                            Interlocked.Increment(ref failed);
                        }
                    }
                });

                await Task.WhenAll(tasks);

                var elapsedMs = (DateTime.UtcNow - tickStart).TotalMilliseconds;
                var delay = 1000 - (int)elapsedMs;
                if (delay > 0) await Task.Delay(delay);
            }

            return Ok(new
            {
                connections = hubs.Count,
                seconds,
                ratePerConnection = rate,
                sent,
                failed
            });
        }

        // GET /loadtest/nextstorm?seconds=10&rate=1
        // rate = koliko puta u sekundi po konekciji pozove Next()
        [HttpGet("nextstorm")]
        public async Task<IActionResult> NextStorm(int seconds = 10, int rate = 1)
        {
            if (!IsDevelopment())
                return BadRequest("Load test allowed only in Development.");

            List<HubConnection> hubs;
            lock (_sync) hubs = _hubs.ToList();

            if (hubs.Count == 0)
                return BadRequest("Run /loadtest/start/{count} first.");

            var until = DateTime.UtcNow.AddSeconds(seconds);
            int calls = 0, failed = 0;

            while (DateTime.UtcNow < until)
            {
                var tickStart = DateTime.UtcNow;

                var tasks = hubs.Select(async h =>
                {
                    for (int i = 0; i < rate; i++)
                    {
                        try
                        {
                            await h.InvokeAsync("Next");
                            Interlocked.Increment(ref calls);
                        }
                        catch
                        {
                            Interlocked.Increment(ref failed);
                        }
                    }
                });

                await Task.WhenAll(tasks);

                var elapsedMs = (DateTime.UtcNow - tickStart).TotalMilliseconds;
                var delay = 1000 - (int)elapsedMs;
                if (delay > 0) await Task.Delay(delay);
            }

            return Ok(new
            {
                connections = hubs.Count,
                seconds,
                ratePerConnection = rate,
                calls,
                failed
            });
        }

        // GET /loadtest/stop
        [HttpGet("stop")]
        public async Task<IActionResult> Stop()
        {
            if (!IsDevelopment())
                return BadRequest("Load test allowed only in Development.");

            List<HubConnection> hubs;
            lock (_sync)
            {
                hubs = _hubs;
                _hubs = new List<HubConnection>();
            }

            foreach (var h in hubs)
            {
                try { await h.StopAsync(); } catch { }
                try { await h.DisposeAsync(); } catch { }
            }

            return Ok(new { stopped = hubs.Count });
        }
    }
}
