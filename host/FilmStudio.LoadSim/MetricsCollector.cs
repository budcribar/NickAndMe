using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace FilmStudio.LoadSim;

public sealed class MetricsCollector
{
    private readonly ConcurrentBag<Sample> _samples = new();
    private long _healthOk;
    private long _healthFail;
    private long _jobsSubmitted;
    private long _jobsRejected; // 409 lock or capacity
    private long _jobs5xx;
    private int _peakApiInFlight;
    private int _configuredMaxVideo;

    public void Record(
        string action,
        int statusCode,
        long latencyMs,
        bool intentionalConflict = false)
    {
        _samples.Add(new Sample
        {
            Action = action,
            StatusCode = statusCode,
            LatencyMs = latencyMs,
            IntentionalConflict = intentionalConflict,
            At = DateTimeOffset.UtcNow,
        });

        if (action == "health")
        {
            if (statusCode is >= 200 and < 300) Interlocked.Increment(ref _healthOk);
            else Interlocked.Increment(ref _healthFail);
        }

        if (action is "gen" or "remux")
        {
            if (statusCode is >= 200 and < 300) Interlocked.Increment(ref _jobsSubmitted);
            else if (statusCode == 409) Interlocked.Increment(ref _jobsRejected);
            else if (statusCode >= 500) Interlocked.Increment(ref _jobs5xx);
        }
        else if (statusCode >= 500)
        {
            Interlocked.Increment(ref _jobs5xx);
        }
    }

    public void NoteServerCapacity(int maxVideoInFlight, int? apiInFlight)
    {
        _configuredMaxVideo = Math.Max(_configuredMaxVideo, maxVideoInFlight);
        if (apiInFlight is int n)
        {
            int cur;
            do { cur = _peakApiInFlight; }
            while (n > cur && Interlocked.CompareExchange(ref _peakApiInFlight, n, cur) != cur);
        }
    }

    public LoadSimResults Build(SimOptions opts, TimeSpan elapsed)
    {
        var snap = Snapshot(opts, elapsed);
        return new LoadSimResults
        {
            Users = opts.Users,
            DurationSec = opts.DurationSec,
            ElapsedSec = elapsed.TotalSeconds,
            Scenario = opts.Scenario,
            ProjectId = opts.ProjectId,
            BaseUrl = opts.BaseUrl,
            Actions = snap.ActionsByType,
            Http = new HttpStats
            {
                Total = snap.ActionsTotal,
                Errors = snap.Errors,
                ErrorRate = snap.ErrorRate,
                Intentional409 = snap.Intentional409,
                P50Ms = snap.P50Ms,
                P95Ms = snap.P95Ms,
                P99Ms = snap.P99Ms,
                BrowseP50Ms = snap.BrowseP50Ms,
                BrowseP95Ms = snap.BrowseP95Ms,
            },
            Jobs = new JobStats
            {
                Submitted = snap.JobsSubmitted,
                Rejected = snap.JobsRejected,
                Server5xx = snap.Jobs5xx,
            },
            Health = new HealthStats
            {
                Ok = snap.HealthOk,
                Fail = snap.HealthFail,
            },
            Server = new ServerStats
            {
                ConfiguredMaxVideoInFlight = snap.ConfiguredMaxVideoInFlight,
                PeakApiInFlight = snap.PeakApiInFlight,
            },
            Gates = new List<GateResult>(),
        };
    }

    /// <summary>Lightweight snapshot for live admin telemetry.</summary>
    public LiveSnapshot Snapshot(SimOptions opts, TimeSpan elapsed)
    {
        var all = _samples.ToArray();
        var byAction = all
            .GroupBy(s => s.Action, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var counted = all.Where(s => !s.IntentionalConflict).ToArray();
        var errors = counted.Count(s => s.StatusCode >= 400 || s.StatusCode < 0);
        var errorRate = counted.Length == 0 ? 0 : (double)errors / counted.Length;

        var browse = all.Where(s => s.Action is "browse" or "health" or "projects" or "scenes" or "scene_detail")
            .Select(s => s.LatencyMs)
            .OrderBy(x => x)
            .ToArray();
        var allLat = all.Where(s => s.LatencyMs >= 0).Select(s => s.LatencyMs).OrderBy(x => x).ToArray();
        var sec = Math.Max(0.001, elapsed.TotalSeconds);

        return new LiveSnapshot
        {
            ActionsByType = byAction,
            ActionsTotal = all.Length,
            ActionsPerSec = all.Length / sec,
            Errors = errors,
            ErrorRate = errorRate,
            Intentional409 = all.Count(s => s.IntentionalConflict),
            P50Ms = Percentile(allLat, 0.50),
            P95Ms = Percentile(allLat, 0.95),
            P99Ms = Percentile(allLat, 0.99),
            BrowseP50Ms = Percentile(browse, 0.50),
            BrowseP95Ms = Percentile(browse, 0.95),
            JobsSubmitted = (int)Interlocked.Read(ref _jobsSubmitted),
            JobsRejected = (int)Interlocked.Read(ref _jobsRejected),
            Jobs5xx = (int)Interlocked.Read(ref _jobs5xx),
            HealthOk = (int)Interlocked.Read(ref _healthOk),
            HealthFail = (int)Interlocked.Read(ref _healthFail),
            ConfiguredMaxVideoInFlight = _configuredMaxVideo,
            PeakApiInFlight = _peakApiInFlight,
        };
    }

    public sealed class LiveSnapshot
    {
        public Dictionary<string, int> ActionsByType { get; set; } = new();
        public int ActionsTotal { get; set; }
        public double ActionsPerSec { get; set; }
        public int Errors { get; set; }
        public double ErrorRate { get; set; }
        public int Intentional409 { get; set; }
        public long P50Ms { get; set; }
        public long P95Ms { get; set; }
        public long P99Ms { get; set; }
        public long BrowseP50Ms { get; set; }
        public long BrowseP95Ms { get; set; }
        public int JobsSubmitted { get; set; }
        public int JobsRejected { get; set; }
        public int Jobs5xx { get; set; }
        public int HealthOk { get; set; }
        public int HealthFail { get; set; }
        public int ConfiguredMaxVideoInFlight { get; set; }
        public int PeakApiInFlight { get; set; }
    }

    private static long Percentile(long[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var idx = (int)Math.Ceiling(p * sorted.Length) - 1;
        idx = Math.Clamp(idx, 0, sorted.Length - 1);
        return sorted[idx];
    }

    private sealed class Sample
    {
        public string Action { get; set; } = "";
        public int StatusCode { get; set; }
        public long LatencyMs { get; set; }
        public bool IntentionalConflict { get; set; }
        public DateTimeOffset At { get; set; }
    }
}

public sealed class LoadSimResults
{
    public int Users { get; set; }
    public int DurationSec { get; set; }
    public double ElapsedSec { get; set; }
    public string Scenario { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public Dictionary<string, int> Actions { get; set; } = new();
    public HttpStats Http { get; set; } = new();
    public JobStats Jobs { get; set; } = new();
    public HealthStats Health { get; set; } = new();
    public ServerStats Server { get; set; } = new();
    public List<GateResult> Gates { get; set; } = new();
    public bool Passed { get; set; }
}

public sealed class HttpStats
{
    public int Total { get; set; }
    public int Errors { get; set; }
    public double ErrorRate { get; set; }
    public int Intentional409 { get; set; }
    public long P50Ms { get; set; }
    public long P95Ms { get; set; }
    public long P99Ms { get; set; }
    public long BrowseP50Ms { get; set; }
    public long BrowseP95Ms { get; set; }
}

public sealed class JobStats
{
    public int Submitted { get; set; }
    public int Rejected { get; set; }
    public int Server5xx { get; set; }
}

public sealed class HealthStats
{
    public int Ok { get; set; }
    public int Fail { get; set; }
}

public sealed class ServerStats
{
    public int ConfiguredMaxVideoInFlight { get; set; }
    public int PeakApiInFlight { get; set; }
}

public sealed class GateResult
{
    public string Name { get; set; } = "";
    public bool Pass { get; set; }
    public string Detail { get; set; } = "";
}

public static class GateEvaluator
{
    public static bool Evaluate(LoadSimResults r, SimOptions opts)
    {
        var gates = new List<GateResult>();

        gates.Add(new GateResult
        {
            Name = "http_error_rate",
            Pass = r.Http.ErrorRate <= opts.MaxErrorRate,
            Detail = $"errorRate={r.Http.ErrorRate:P2} (max {opts.MaxErrorRate:P2}); intentional409={r.Http.Intentional409}",
        });

        gates.Add(new GateResult
        {
            Name = "health",
            Pass = r.Health.Fail == 0 && r.Health.Ok > 0,
            Detail = $"ok={r.Health.Ok} fail={r.Health.Fail}",
        });

        gates.Add(new GateResult
        {
            Name = "browse_p95",
            Pass = r.Http.BrowseP95Ms <= opts.MaxBrowseP95Ms,
            Detail = $"browseP95={r.Http.BrowseP95Ms}ms (max {opts.MaxBrowseP95Ms}ms)",
        });

        gates.Add(new GateResult
        {
            Name = "no_5xx",
            Pass = r.Jobs.Server5xx == 0,
            Detail = $"server5xx={r.Jobs.Server5xx}",
        });

        if (r.Server.ConfiguredMaxVideoInFlight > 0 && r.Server.PeakApiInFlight > 0)
        {
            gates.Add(new GateResult
            {
                Name = "peak_inflight_vs_cap",
                Pass = r.Server.PeakApiInFlight <= r.Server.ConfiguredMaxVideoInFlight + 2, // small slack
                Detail = $"peakApiInFlight={r.Server.PeakApiInFlight} cap={r.Server.ConfiguredMaxVideoInFlight}",
            });
        }

        r.Gates = gates;
        r.Passed = gates.All(g => g.Pass);
        return r.Passed;
    }
}
