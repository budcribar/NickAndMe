using System.Diagnostics;
using PageToMovie.Api.Hubs;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace PageToMovie.Api.Services;

/// <summary>Pushes server metrics to SignalR group <c>admin:ops</c> every ~2s (Phase C).</summary>
public sealed class AdminMetricsPushService : BackgroundService
{
    private readonly IHubContext<JobHub> _hub;
    private readonly IServerMetricsService _metrics;
    private readonly IJobStore _jobs;
    private readonly ILockService _locks;
    private readonly IOptions<PageToMovieOptions> _opts;
    private readonly IHostEnvironment _env;
    private readonly ProcessHistoryStore _processHistory;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private readonly bool _useFakes;

    public AdminMetricsPushService(
        IHubContext<JobHub> hub,
        IServerMetricsService metrics,
        IJobStore jobs,
        ILockService locks,
        IOptions<PageToMovieOptions> opts,
        IHostEnvironment env,
        IConfiguration config,
        ProcessHistoryStore processHistory)
    {
        _hub = hub;
        _metrics = metrics;
        _jobs = jobs;
        _locks = locks;
        _opts = opts;
        _env = env;
        _processHistory = processHistory;
        _useFakes = opts.Value.UseFakes
            || string.Equals(Environment.GetEnvironmentVariable("PageToMovie_USE_FAKES"), "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("PageToMovie_USE_FAKES"), "true", StringComparison.OrdinalIgnoreCase)
            || config.GetValue("PageToMovie:UseFakes", false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                _processHistory.Sample();
                var snap = BuildSnapshot();
                await _hub.Clients.Group(JobHub.AdminOpsGroup)
                    .SendAsync(JobHubEvents.AdminState, snap, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // keep ticking
            }
        }
    }

    public ServerMetricsSnapshot BuildSnapshot()
    {
        var o = _opts.Value;
        var cap = o.Capacity ?? new CapacityOptions();
        var proc = Process.GetCurrentProcess();
        return _metrics.GetSnapshot(
            _jobs,
            _locks,
            new CapacityOptionsSnapshot
            {
                MaxVideoInFlight = cap.MaxVideoInFlight,
                MaxVideoInFlightPerUser = cap.MaxVideoInFlightPerUser,
                MaxFfmpegInFlight = cap.MaxFfmpegInFlight,
                MaxQueuePerUser = cap.MaxQueuePerUser,
            },
            new ProcessMetricsSnapshot
            {
                UptimeSec = (long)(DateTimeOffset.UtcNow - _startedUtc).TotalSeconds,
                WorkingSetMb = Math.Round(proc.WorkingSet64 / (1024.0 * 1024.0), 1),
                GcHeapMb = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 1),
                ThreadCount = proc.Threads.Count,
                Environment = _env.EnvironmentName,
                UseFakes = o.UseFakes || _useFakes,
            });
    }
}
