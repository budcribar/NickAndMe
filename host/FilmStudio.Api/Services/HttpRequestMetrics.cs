using System.Collections.Concurrent;

namespace FilmStudio.Api.Services;

/// <summary>Rolling HTTP request counters for admin (detect LoadSim traffic).</summary>
public sealed class HttpRequestMetrics
{
    private long _total;
    private readonly ConcurrentDictionary<string, long> _byPrefix = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(long Ticks, string Prefix)> _recent = new();
    private readonly object _prune = new();

    public void Record(string path)
    {
        Interlocked.Increment(ref _total);
        var prefix = Classify(path);
        _byPrefix.AddOrUpdate(prefix, 1, (_, n) => n + 1);
        var ticks = Environment.TickCount64;
        _recent.Enqueue((ticks, prefix));
        Prune(ticks);
    }

    public HttpTrafficSnapshot Snapshot()
    {
        var now = Environment.TickCount64;
        Prune(now);
        var last5 = _recent.Where(x => now - x.Ticks <= 5_000).ToList();
        var last30 = _recent.Where(x => now - x.Ticks <= 30_000).ToList();
        var byPrefix5 = last5
            .GroupBy(x => x.Prefix, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var byPrefix30 = last30
            .GroupBy(x => x.Prefix, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return new HttpTrafficSnapshot
        {
            TotalLifetime = Interlocked.Read(ref _total),
            RequestsLast5Sec = last5.Count,
            RequestsLast30Sec = last30.Count,
            ByPrefixLast5Sec = byPrefix5,
            ByPrefixLast30Sec = byPrefix30,
            // Exclude admin polls so LoadSim is obvious
            NonAdminLast5Sec = last5.Count(x =>
                !x.Prefix.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase)),
            NonAdminLast30Sec = last30.Count(x =>
                !x.Prefix.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase)),
        };
    }

    private void Prune(long now)
    {
        lock (_prune)
        {
            while (_recent.TryPeek(out var head) && now - head.Ticks > 30_000)
                _recent.TryDequeue(out _);
        }
    }

    private static string Classify(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        path = path.Split('?', 2)[0];
        if (path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase)) return "/api/admin/*";
        if (path.StartsWith("/api/jobs", StringComparison.OrdinalIgnoreCase)) return "/api/jobs/*";
        if (path.StartsWith("/api/projects", StringComparison.OrdinalIgnoreCase)) return "/api/projects/*";
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return "/api/*";
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)) return "/health";
        if (path.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase)) return "/hubs/*";
        return path.Length > 40 ? path[..40] : path;
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
