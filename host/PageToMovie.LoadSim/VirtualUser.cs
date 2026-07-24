using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PageToMovie.Core.Auth;

namespace PageToMovie.LoadSim;

public sealed class VirtualUser
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly int _index;
    private readonly string _userId;
    private readonly SimOptions _opts;
    private readonly MetricsCollector _metrics;
    private readonly HttpClient _http;
    private readonly Random _rng;
    private int _sceneCount = 1;
    private int _gensDone;

    public string UserId => _userId;

    public VirtualUser(int index, SimOptions opts, MetricsCollector metrics, HttpClient http)
    {
        _index = index;
        _userId = $"u{index:D3}";
        _opts = opts;
        _metrics = metrics;
        _http = http;
        _rng = new Random(HashCode.Combine(index, Environment.TickCount));
    }

    /// <summary>
    /// HTTP ready: open connection, prove /health, light-warm project paths, discover scenes.
    /// Does <b>not</b> record metrics (outside the stress clock).
    /// </summary>
    public async Task ReadyAsync(CancellationToken ct)
    {
        var health = await GetAsync("/health", ct);
        if (health is < 200 or >= 300)
            throw new InvalidOperationException($"ready /health → HTTP {health}");

        // Warm server caches / connections (not timed)
        _ = await GetAsync("/api/projects", ct);
        _ = await GetAsync($"/api/projects/{Esc(_opts.ProjectId)}/scenes?light=1", ct);
        await DiscoverScenesAsync(ct);
        _ = await GetAsync("/api/capacity", ct);
    }

    /// <summary>Measured stress loop. Call only after the global ready barrier releases.</summary>
    public async Task RunStressAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var think = _opts.ThinkTimeMs <= 0
                ? 0
                : Math.Max(0, _opts.ThinkTimeMs + _rng.Next(-_opts.ThinkTimeMs / 4, _opts.ThinkTimeMs / 4 + 1));
            if (think > 0)
            {
                try { await Task.Delay(think, ct); }
                catch (OperationCanceledException) { break; }
            }

            var action = PickAction();
            try
            {
                await ExecuteAsync(action, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _metrics.Record(action, -1, 0);
                Console.Error.WriteLine($"[{_userId}] {action}: {ex.Message}");
            }
        }
    }

    private string PickAction()
    {
        return _opts.Scenario.ToLowerInvariant() switch
        {
            "browse" => "browse",
            "play" => "play",
            "gen" => "gen",
            "remux" => "remux",
            "review" => "review",
            _ => WeightedPick(),
        };
    }

    private string WeightedPick()
    {
        var items = new (string Name, double W)[]
        {
            ("browse", Math.Max(0, _opts.BrowseWeight)),
            ("play", Math.Max(0, _opts.PlayWeight)),
            ("gen", Math.Max(0, _opts.GenWeight)),
            ("review", Math.Max(0, _opts.ReviewWeight)),
            ("remux", Math.Max(0, _opts.RemuxWeight)),
        };
        var total = items.Sum(i => i.W);
        if (total <= 0) return "browse";
        var r = _rng.NextDouble() * total;
        var acc = 0.0;
        foreach (var (name, w) in items)
        {
            acc += w;
            if (r <= acc) return name;
        }
        return "browse";
    }

    private async Task ExecuteAsync(string action, CancellationToken ct)
    {
        switch (action)
        {
            case "browse":
                await BrowseAsync(ct);
                break;
            case "play":
                await PlayAsync(ct);
                break;
            case "gen":
                await GenAsync(ct);
                break;
            case "review":
                await ReviewAsync(ct);
                break;
            case "remux":
                await RemuxAsync(ct);
                break;
            default:
                await BrowseAsync(ct);
                break;
        }

        // Capacity samples (any VU, ~10%) so peakApiInFlight/cap appear in results
        if (_rng.NextDouble() < 0.10)
            await SampleCapacityAsync(ct);
    }

    private async Task BrowseAsync(CancellationToken ct)
    {
        // light=1 skips ffprobe on scene list/detail — full probes destroy p95 under 100 VUs
        await TimedAsync("health", () => GetAsync("/health", ct), ct);
        await TimedAsync("projects", () => GetAsync("/api/projects", ct), ct);
        await TimedAsync("scenes",
            () => GetAsync($"/api/projects/{Esc(_opts.ProjectId)}/scenes?light=1", ct), ct);
        var sn = AssignedScene();
        await TimedAsync("scene_detail",
            () => GetAsync($"/api/projects/{Esc(_opts.ProjectId)}/scenes/{sn}?light=1", ct), ct);
        await TimedAsync("browse", () => GetAsync("/api/capacity", ct), ct);
    }

    private async Task PlayAsync(CancellationToken ct)
    {
        var sn = AssignedScene();
        var clip = 1;
        // Range request first bytes of clip or composite
        var path = $"/api/projects/{Esc(_opts.ProjectId)}/scenes/{sn}/clips/{clip}/video";
        await TimedAsync("play", async () =>
        {
            using var req = CreateRequest(HttpMethod.Get, path);
            req.Headers.Range = new RangeHeaderValue(0, 1023);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            // 404 is ok if clip missing — still counts as latency sample, not hard error for load
            if ((int)resp.StatusCode is 404 or 416)
            {
                // try composite
                using var req2 = CreateRequest(HttpMethod.Get,
                    $"/api/projects/{Esc(_opts.ProjectId)}/scenes/{sn}/composite");
                req2.Headers.Range = new RangeHeaderValue(0, 1023);
                using var resp2 = await _http.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, ct);
                return (int)resp2.StatusCode is 404 or 416 ? 200 : (int)resp2.StatusCode;
            }
            return (int)resp.StatusCode;
        }, ct);
    }

    private async Task GenAsync(CancellationToken ct)
    {
        if (_gensDone >= _opts.MaxGenPerUser)
        {
            await BrowseAsync(ct);
            return;
        }

        var sn = AssignedScene();
        await TimedAsync("gen", async () =>
        {
            using var req = CreateRequest(HttpMethod.Post, "/api/jobs/gen-scene");
            req.Content = JsonContent.Create(new
            {
                projectId = _opts.ProjectId,
                scene = sn,
                onlyMissing = true,
            });
            using var resp = await _http.SendAsync(req, ct);
            var code = (int)resp.StatusCode;
            if (code is >= 200 and < 300)
                Interlocked.Increment(ref _gensDone);
            return code;
        }, ct, intentionalConflictOn409: true);
    }

    private async Task ReviewAsync(CancellationToken ct)
    {
        var sn = AssignedScene();
        await TimedAsync("review", async () =>
        {
            using var req = CreateRequest(HttpMethod.Post,
                $"/api/projects/{Esc(_opts.ProjectId)}/clips/review");
            req.Content = JsonContent.Create(new
            {
                projectId = _opts.ProjectId,
                scene = sn,
                clip = 1,
                status = "pass",
                note = "loadsim",
            });
            using var resp = await _http.SendAsync(req, ct);
            return (int)resp.StatusCode;
        }, ct);
    }

    private async Task RemuxAsync(CancellationToken ct)
    {
        var sn = AssignedScene();
        await TimedAsync("remux", async () =>
        {
            using var req = CreateRequest(HttpMethod.Post, "/api/jobs/remux");
            req.Content = JsonContent.Create(new
            {
                projectId = _opts.ProjectId,
                scene = sn,
                rebuildWip = false,
            });
            using var resp = await _http.SendAsync(req, ct);
            return (int)resp.StatusCode;
        }, ct, intentionalConflictOn409: true);
    }

    private async Task SampleCapacityAsync(CancellationToken ct)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, "/api/capacity");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            var max = 0;
            var running = 0;
            if (root.TryGetProperty("capacity", out var cap))
            {
                if (cap.TryGetProperty("maxVideoInFlight", out var m) && m.TryGetInt32(out var mi))
                    max = mi;
                else if (cap.TryGetProperty("MaxVideoInFlight", out var m2) && m2.TryGetInt32(out var mi2))
                    max = mi2;
            }
            if (root.TryGetProperty("runningCount", out var rc) && rc.TryGetInt32(out var ri))
                running = ri;
            else if (root.TryGetProperty("running", out var run) && run.ValueKind == JsonValueKind.True)
                running = 1;
            _metrics.NoteServerCapacity(max, running);
        }
        catch
        {
            // ignore
        }
    }

    private async Task DiscoverScenesAsync(CancellationToken ct)
    {
        try
        {
            using var req = CreateRequest(
                HttpMethod.Get,
                $"/api/projects/{Esc(_opts.ProjectId)}/scenes?light=1");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("sceneCount", out var sc) && sc.GetInt32() > 0)
                _sceneCount = sc.GetInt32();
            else if (doc.RootElement.TryGetProperty("scenes", out var arr) &&
                     arr.ValueKind == JsonValueKind.Array)
                _sceneCount = Math.Max(1, arr.GetArrayLength());
        }
        catch
        {
            _sceneCount = 1;
        }
    }

    private int AssignedScene()
    {
        if (_opts.ForceLockCollisions)
            return 1;
        return (_index % Math.Max(1, _sceneCount)) + 1;
    }

    private async Task TimedAsync(
        string action,
        Func<Task<int>> work,
        CancellationToken ct,
        bool intentionalConflictOn409 = false)
    {
        var sw = Stopwatch.StartNew();
        int code;
        try
        {
            code = await work();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            code = -1;
        }
        sw.Stop();
        var intentional = intentionalConflictOn409 && code == 409;
        _metrics.Record(action, code, sw.ElapsedMilliseconds, intentional);
    }

    private async Task<int> GetAsync(string path, CancellationToken ct)
    {
        using var req = CreateRequest(HttpMethod.Get, path);
        // Headers-only then drain body so connections return to the pool promptly
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.Content is not null)
            await resp.Content.CopyToAsync(Stream.Null, ct);
        return (int)resp.StatusCode;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.TryAddWithoutValidation(AuthHeaders.UserId, _userId);
        req.Headers.TryAddWithoutValidation(AuthHeaders.ApiKey, $"sim-{_userId}");
        return req;
    }

    private static string Esc(string s) => Uri.EscapeDataString(s);
}
