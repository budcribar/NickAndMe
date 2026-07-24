namespace PageToMovie.Api.Services;

/// <summary>
/// Rolling HTTP request counters for admin (detect LoadSim traffic).
/// Hot path: lock-free increments into fixed 1-second buckets (lock only when a bucket rolls).
/// Window math (last 5s / 30s) runs only in <see cref="Snapshot"/> — not on every request.
/// </summary>
public sealed class HttpRequestMetrics
{
    private const int WindowSeconds = 30;

    private long _total;
    private readonly Bucket[] _ring = new Bucket[WindowSeconds];
    private readonly object _roll = new();

    public HttpRequestMetrics()
    {
        for (var i = 0; i < WindowSeconds; i++)
            _ring[i] = new Bucket();
    }

    public void Record(string path)
    {
        Interlocked.Increment(ref _total);
        var kind = Classify(path);
        var sec = Environment.TickCount64 / 1000;
        var bucket = _ring[(int)(sec % WindowSeconds)];

        // At most once per second per slot: clear the bucket for the new second.
        if (Volatile.Read(ref bucket.Second) != sec)
        {
            lock (_roll)
            {
                if (bucket.Second != sec)
                    bucket.Reset(sec);
            }
        }

        Interlocked.Increment(ref bucket.Total);
        if (kind != PrefixKind.Admin)
            Interlocked.Increment(ref bucket.NonAdmin);

        switch (kind)
        {
            case PrefixKind.Admin:
                Interlocked.Increment(ref bucket.Admin);
                break;
            case PrefixKind.Jobs:
                Interlocked.Increment(ref bucket.Jobs);
                break;
            case PrefixKind.Projects:
                Interlocked.Increment(ref bucket.Projects);
                break;
            case PrefixKind.Api:
                Interlocked.Increment(ref bucket.Api);
                break;
            case PrefixKind.Health:
                Interlocked.Increment(ref bucket.Health);
                break;
            case PrefixKind.Hubs:
                Interlocked.Increment(ref bucket.Hubs);
                break;
            default:
                Interlocked.Increment(ref bucket.Other);
                break;
        }
    }

    public HttpTrafficSnapshot Snapshot()
    {
        var nowSec = Environment.TickCount64 / 1000;
        long last5 = 0, last30 = 0, nonAdmin5 = 0, nonAdmin30 = 0;
        long admin5 = 0, jobs5 = 0, projects5 = 0, api5 = 0, health5 = 0, hubs5 = 0, other5 = 0;
        long admin30 = 0, jobs30 = 0, projects30 = 0, api30 = 0, health30 = 0, hubs30 = 0, other30 = 0;

        foreach (var bucket in _ring)
        {
            var sec = Volatile.Read(ref bucket.Second);
            if (sec == 0)
                continue;
            var age = nowSec - sec;
            // Ignore empty/stale slots (rolled out of the 30s window).
            if (age < 0 || age >= WindowSeconds)
                continue;

            var total = Interlocked.Read(ref bucket.Total);
            var nonAdmin = Interlocked.Read(ref bucket.NonAdmin);
            var admin = Interlocked.Read(ref bucket.Admin);
            var jobs = Interlocked.Read(ref bucket.Jobs);
            var projects = Interlocked.Read(ref bucket.Projects);
            var api = Interlocked.Read(ref bucket.Api);
            var health = Interlocked.Read(ref bucket.Health);
            var hubs = Interlocked.Read(ref bucket.Hubs);
            var other = Interlocked.Read(ref bucket.Other);

            last30 += total;
            nonAdmin30 += nonAdmin;
            admin30 += admin;
            jobs30 += jobs;
            projects30 += projects;
            api30 += api;
            health30 += health;
            hubs30 += hubs;
            other30 += other;

            if (age < 5)
            {
                last5 += total;
                nonAdmin5 += nonAdmin;
                admin5 += admin;
                jobs5 += jobs;
                projects5 += projects;
                api5 += api;
                health5 += health;
                hubs5 += hubs;
                other5 += other;
            }
        }

        return new HttpTrafficSnapshot
        {
            TotalLifetime = Interlocked.Read(ref _total),
            RequestsLast5Sec = (int)Math.Min(int.MaxValue, last5),
            RequestsLast30Sec = (int)Math.Min(int.MaxValue, last30),
            NonAdminLast5Sec = (int)Math.Min(int.MaxValue, nonAdmin5),
            NonAdminLast30Sec = (int)Math.Min(int.MaxValue, nonAdmin30),
            ByPrefixLast5Sec = ToPrefixMap(admin5, jobs5, projects5, api5, health5, hubs5, other5),
            ByPrefixLast30Sec = ToPrefixMap(admin30, jobs30, projects30, api30, health30, hubs30, other30),
        };
    }

    private static Dictionary<string, int> ToPrefixMap(
        long admin, long jobs, long projects, long api, long health, long hubs, long other)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Add(d, "/api/admin/*", admin);
        Add(d, "/api/jobs/*", jobs);
        Add(d, "/api/projects/*", projects);
        Add(d, "/api/*", api);
        Add(d, "/health", health);
        Add(d, "/hubs/*", hubs);
        Add(d, "other", other);
        return d;
    }

    private static void Add(Dictionary<string, int> d, string key, long n)
    {
        if (n > 0)
            d[key] = (int)Math.Min(int.MaxValue, n);
    }

    private static PrefixKind Classify(string path)
    {
        if (string.IsNullOrEmpty(path))
            return PrefixKind.Other;

        // Avoid allocating a split array when there is no query string.
        var q = path.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
            path = path[..q];

        // Some hosts / reverse proxies report path without a leading slash.
        if (path.Length > 0 && path[0] != '/')
            path = "/" + path;

        if (path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase))
            return PrefixKind.Admin;
        if (path.StartsWith("/api/jobs", StringComparison.OrdinalIgnoreCase))
            return PrefixKind.Jobs;
        if (path.StartsWith("/api/projects", StringComparison.OrdinalIgnoreCase))
            return PrefixKind.Projects;
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/api", StringComparison.OrdinalIgnoreCase))
            return PrefixKind.Api;
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
            return PrefixKind.Health;
        if (path.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase))
            return PrefixKind.Hubs;
        return PrefixKind.Other;
    }

    /// <summary>Test hook: classify a request path the same way as <see cref="Record"/>.</summary>
    public static string ClassifyPathForTests(string path) => Classify(path) switch
    {
        PrefixKind.Admin => "admin",
        PrefixKind.Jobs => "jobs",
        PrefixKind.Projects => "projects",
        PrefixKind.Api => "api",
        PrefixKind.Health => "health",
        PrefixKind.Hubs => "hubs",
        _ => "other",
    };

    private enum PrefixKind : byte
    {
        Other = 0,
        Admin,
        Jobs,
        Projects,
        Api,
        Health,
        Hubs,
    }

    private sealed class Bucket
    {
        public long Second;
        public long Total;
        public long NonAdmin;
        public long Admin;
        public long Jobs;
        public long Projects;
        public long Api;
        public long Health;
        public long Hubs;
        public long Other;

        public void Reset(long sec)
        {
            Second = sec;
            Total = 0;
            NonAdmin = 0;
            Admin = 0;
            Jobs = 0;
            Projects = 0;
            Api = 0;
            Health = 0;
            Hubs = 0;
            Other = 0;
        }
    }
}

public sealed class HttpTrafficSnapshot
{
    public long TotalLifetime { get; set; }
    public int RequestsLast5Sec { get; set; }
    public int RequestsLast30Sec { get; set; }
    public int NonAdminLast5Sec { get; set; }
    public int NonAdminLast30Sec { get; set; }
    public Dictionary<string, int> ByPrefixLast5Sec { get; set; } = new();
    public Dictionary<string, int> ByPrefixLast30Sec { get; set; } = new();
}

public sealed class HttpRequestMetricsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HttpRequestMetrics _metrics;

    public HttpRequestMetricsMiddleware(RequestDelegate next, HttpRequestMetrics metrics)
    {
        _next = next;
        _metrics = metrics;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        _metrics.Record(ctx.Request.Path.Value ?? "/");
        await _next(ctx);
    }
}
