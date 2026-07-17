using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FilmStudio.LoadSim;

// Top-level statements

var opts = SimOptions.Parse(args);
var exitCode = 2;

try
{
    exitCode = await RunAsync(opts);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: {ex}");
    exitCode = 2;
}

if (exitCode != 0)
    PauseIfInteractive("LoadSim exited with errors. Check messages above.");

return exitCode;

static async Task<int> RunAsync(SimOptions opts)
{
    Console.WriteLine($"FilmStudio.LoadSim → {opts.BaseUrl}");
    Console.WriteLine($"  users={opts.Users} duration={opts.DurationSec}s scenario={opts.Scenario} project={opts.ProjectId}");
    Console.WriteLine($"  cwd={Directory.GetCurrentDirectory()}");
    Console.WriteLine($"  waitForApi={opts.WaitForApiSec}s");

    if (!opts.AllowRealProject &&
        (string.Equals(opts.ProjectId, "Buster", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(opts.ProjectId, "NickAndMe", StringComparison.OrdinalIgnoreCase)))
    {
        Console.Error.WriteLine(
            $"Setup: refusing real project '{opts.ProjectId}'. " +
            $"Use '{ProjectSandbox.DefaultSandboxId}' (default) or pass --allowRealProject.");
        return 2;
    }

    if (opts.PrepareSandbox)
    {
        try
        {
            var workspace = ProjectSandbox.FindWorkspaceRoot(opts.WorkspaceRoot);
            if (workspace is null)
            {
                Console.Error.WriteLine(
                    "Setup: could not find workspace root (folder with projects/). Pass --workspace PATH.");
                return 2;
            }

            opts.WorkspaceRoot = workspace;
            Console.WriteLine($"  workspace={workspace}");
            ProjectSandbox.Ensure(
                workspace,
                opts.SourceProjectId,
                opts.ProjectId,
                refresh: opts.RefreshSandbox);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Setup: sandbox prepare failed: {ex.Message}");
            return 2;
        }
    }
    else
    {
        Console.WriteLine($"  project={opts.ProjectId} (checked-in sandbox; no recopy)");
    }

    using var http = new HttpClient
    {
        BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(15),
    };

    // Wait for API — VS multi-start often launches LoadSim before Api is listening
    Console.WriteLine($"  waiting for API {opts.BaseUrl}/health (up to {opts.WaitForApiSec}s)…");
    var healthOk = false;
    var useFakes = false;
    var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, opts.WaitForApiSec));
    Exception? lastErr = null;
    var attempt = 0;
    while (DateTimeOffset.UtcNow < deadline)
    {
        attempt++;
        try
        {
            using var health = await http.GetAsync("health");
            if (health.IsSuccessStatusCode)
            {
                await using var stream = await health.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                useFakes = doc.RootElement.TryGetProperty("useFakes", out var uf) && uf.GetBoolean();
                healthOk = true;
                Console.WriteLine($"  health ok after {attempt} attempt(s) · useFakes={useFakes}");
                break;
            }

            lastErr = new Exception($"/health returned {(int)health.StatusCode}");
            Console.WriteLine($"  … attempt {attempt}: HTTP {(int)health.StatusCode}");
        }
        catch (Exception ex)
        {
            lastErr = ex;
            if (attempt == 1 || attempt % 5 == 0)
                Console.WriteLine($"  … attempt {attempt}: {ex.Message}");
        }

        await Task.Delay(1000);
    }

    if (!healthOk)
    {
        Console.Error.WriteLine(
            $"Setup: API not reachable at {opts.BaseUrl} after {opts.WaitForApiSec}s. " +
            $"Last error: {lastErr?.Message ?? "unknown"}. " +
            "Start FilmStudio.Api first (profile 'http (fakes)'), or increase --waitForApiSec.");
        return 2;
    }

    if (opts.RequireFakes && !useFakes && !opts.IKnowWhatImDoing &&
        opts.Scenario is not ("browse" or "play"))
    {
        var genWeight = opts.Scenario == "mixed" ? opts.GenWeight : opts.Scenario == "gen" ? 1.0 : 0;
        if (genWeight > 0)
        {
            Console.Error.WriteLine(
                "Setup: API UseFakes=false but scenario includes gen. " +
                "Start Api with profile 'http (fakes)' or set FILMSTUDIO_USE_FAKES=true.");
            return 2;
        }
    }

    // Ensure project exists on the API
    try
    {
        using var projResp = await http.GetAsync("api/projects");
        projResp.EnsureSuccessStatusCode();
        await using var ps = await projResp.Content.ReadAsStreamAsync();
        using var pdoc = await JsonDocument.ParseAsync(ps);
        var found = false;
        if (pdoc.RootElement.TryGetProperty("projects", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in arr.EnumerateArray())
            {
                var id = p.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.Equals(id, opts.ProjectId, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }
        }

        if (!found)
        {
            Console.Error.WriteLine(
                $"Setup: project '{opts.ProjectId}' not listed by API. " +
                $"Ensure folder projects/{opts.ProjectId} exists under the API workspace. " +
                "Restart Api after adding the project.");
            return 2;
        }

        // Activate sandbox so gen/remux resolve paths
        using var act = await http.PostAsync(
            $"api/projects/{Uri.EscapeDataString(opts.ProjectId)}/activate",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Console.WriteLine(act.IsSuccessStatusCode
            ? $"  activated project {opts.ProjectId}"
            : $"  warn: activate {opts.ProjectId} → {(int)act.StatusCode}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Setup: project check failed: {ex.Message}");
        return 2;
    }

    if (opts.WarmupSec > 0)
    {
        Console.WriteLine($"  warmup {opts.WarmupSec}s…");
        await Task.Delay(TimeSpan.FromSeconds(opts.WarmupSec));
    }

    var metrics = new MetricsCollector();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(opts.DurationSec));
    var started = DateTimeOffset.UtcNow;
    var runId = Guid.NewGuid().ToString("N")[..12];

    Console.WriteLine($"  starting {opts.Users} virtual users for {opts.DurationSec}s… runId={runId}");
    var tasks = Enumerable.Range(0, opts.Users)
        .Select(i =>
        {
            var client = new HttpClient
            {
                BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromMinutes(2),
            };
            var vu = new VirtualUser(i, opts, metrics, client);
            return Task.Run(async () =>
            {
                try { await vu.RunAsync(cts.Token); }
                finally { client.Dispose(); }
            }, CancellationToken.None);
        })
        .ToArray();

    // Live telemetry → admin dashboard
    using var reportCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
    var reportTask = ReportProgressLoopAsync(opts, metrics, runId, started, reportCts.Token);

    Console.WriteLine("  running… (admin /admin shows live LoadSim charts)");
    try
    {
        await Task.WhenAll(tasks);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Run error: {ex.Message}");
    }

    reportCts.Cancel();
    try { await reportTask; } catch { /* ignore */ }

    var elapsed = DateTimeOffset.UtcNow - started;
    var results = metrics.Build(opts, elapsed);
    var passed = GateEvaluator.Evaluate(results, opts);

    // Final snapshot for admin
    await PostProgressAsync(opts, metrics, runId, started, "finished", passed);

    var jsonOpts = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    var outPath = Path.GetFullPath(opts.OutPath);
    await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(results, jsonOpts));

    Console.WriteLine();
    Console.WriteLine("=== LoadSim summary ===");
    Console.WriteLine($"  elapsed={results.ElapsedSec:0.0}s actions={results.Http.Total}");
    Console.WriteLine($"  errorRate={results.Http.ErrorRate:P2} (excl. 409={results.Http.Intentional409})");
    Console.WriteLine($"  latency p50={results.Http.P50Ms}ms p95={results.Http.P95Ms}ms browseP95={results.Http.BrowseP95Ms}ms");
    Console.WriteLine($"  jobs submitted={results.Jobs.Submitted} rejected={results.Jobs.Rejected} 5xx={results.Jobs.Server5xx}");
    Console.WriteLine($"  health ok={results.Health.Ok} fail={results.Health.Fail}");
    Console.WriteLine($"  peakApiInFlight={results.Server.PeakApiInFlight} cap={results.Server.ConfiguredMaxVideoInFlight}");
    Console.WriteLine();
    Console.WriteLine("Gates:");
    foreach (var g in results.Gates)
        Console.WriteLine($"  {(g.Pass ? "PASS" : "FAIL")} {g.Name}: {g.Detail}");
    Console.WriteLine();
    Console.WriteLine($"Results → {outPath}");
    Console.WriteLine(passed ? "RESULT: PASS" : "RESULT: FAIL");

    if (results.Http.Total == 0)
    {
        Console.Error.WriteLine("WARN: zero actions recorded — VUs never completed requests.");
        return 2;
    }

    return passed ? 0 : 1;
}

static async Task ReportProgressLoopAsync(
    SimOptions opts,
    MetricsCollector metrics,
    string runId,
    DateTimeOffset started,
    CancellationToken ct)
{
    try
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct))
            await PostProgressAsync(opts, metrics, runId, started, "running", passed: null);
    }
    catch (OperationCanceledException) { /* done */ }
}

static async Task PostProgressAsync(
    SimOptions opts,
    MetricsCollector metrics,
    string runId,
    DateTimeOffset started,
    string status,
    bool? passed)
{
    try
    {
        var elapsed = DateTimeOffset.UtcNow - started;
        var snap = metrics.Snapshot(opts, elapsed);
        var dto = new FilmStudio.Core.Models.LoadSimProgressDto
        {
            RunId = runId,
            Status = status,
            Users = opts.Users,
            DurationSec = opts.DurationSec,
            ElapsedSec = elapsed.TotalSeconds,
            Scenario = opts.Scenario,
            ProjectId = opts.ProjectId,
            BaseUrl = opts.BaseUrl,
            ActionsTotal = snap.ActionsTotal,
            ActionsPerSec = snap.ActionsPerSec,
            Errors = snap.Errors,
            ErrorRate = snap.ErrorRate,
            Intentional409 = snap.Intentional409,
            P50Ms = snap.P50Ms,
            P95Ms = snap.P95Ms,
            BrowseP50Ms = snap.BrowseP50Ms,
            BrowseP95Ms = snap.BrowseP95Ms,
            JobsSubmitted = snap.JobsSubmitted,
            JobsRejected = snap.JobsRejected,
            Jobs5xx = snap.Jobs5xx,
            HealthOk = snap.HealthOk,
            HealthFail = snap.HealthFail,
            PeakApiInFlight = snap.PeakApiInFlight,
            ConfiguredMaxVideoInFlight = snap.ConfiguredMaxVideoInFlight,
            ActionsByType = snap.ActionsByType,
            Passed = passed,
            At = DateTimeOffset.UtcNow,
        };

        using var client = new HttpClient
        {
            BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        using var resp = await client.PostAsJsonAsync("api/loadsim/progress", dto);
        // ignore non-success — admin is best-effort
    }
    catch
    {
        // best-effort telemetry
    }
}

static void PauseIfInteractive(string message)
{
    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
        return;
    if (Console.IsInputRedirected)
        return;
    Console.Error.WriteLine(message);
    Console.WriteLine("Press Enter to close…");
    try { Console.ReadLine(); } catch { /* ignore */ }
}
