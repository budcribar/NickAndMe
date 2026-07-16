using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FilmStudio.Core.Models;
using FilmStudio.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

public interface IJobProgressSink
{
    Task OnJobUpdatedAsync(JobSnapshot snapshot, CancellationToken ct = default);
    Task OnJobLogAsync(string message, CancellationToken ct = default);
}

/// <summary>
/// C# film job orchestrator: loads blueprint clips and generates missing ones via Grok.
/// Full Python feature parity (multi-ref, identity packer, WIP remux) remains available via Python;
/// this is the native backend path for multi-user / Blazor / SignalR.
/// </summary>
public sealed class FilmJobService
{
    private readonly ProjectStore _projects;
    private readonly GrokVideoClient _grok;
    private readonly CostReportService _costs;
    private readonly FilmStudioOptions _opts;
    private readonly ILogger<FilmJobService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<string> _logLines = new();
    private CancellationTokenSource? _cts;
    private JobSnapshot _snapshot = new() { Status = "idle" };
    private IJobProgressSink? _sink;

    public FilmJobService(
        ProjectStore projects,
        GrokVideoClient grok,
        CostReportService costs,
        IOptions<FilmStudioOptions> opts,
        ILogger<FilmJobService> log)
    {
        _projects = projects;
        _grok = grok;
        _costs = costs;
        _opts = opts.Value;
        _log = log;
    }

    public void SetProgressSink(IJobProgressSink sink) => _sink = sink;

    public JobSnapshot GetSnapshot() => Clone(_snapshot);

    public bool IsRunning =>
        string.Equals(_snapshot.Status, "running", StringComparison.OrdinalIgnoreCase);

    public async Task CancelAsync()
    {
        _cts?.Cancel();
        await AppendLogAsync("Cancel requested…");
        await UpdateAsync(s => s.Message = "Cancel requested — finishing current step if possible…");
    }

    public async Task StartSceneGenAsync(StartSceneGenRequest req)
    {
        if (!await _gate.WaitAsync(0))
            throw new InvalidOperationException("A generation job is already running.");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunSceneGenAsync(req, ct);
            }
            finally
            {
                _gate.Release();
            }
        }, CancellationToken.None);

        await Task.CompletedTask;
    }

    public async Task StartBatchGenAsync(StartBatchGenRequest req)
    {
        if (req.Scenes is null || req.Scenes.Count == 0)
            throw new InvalidOperationException("At least one scene is required.");

        if (!await _gate.WaitAsync(0))
            throw new InvalidOperationException("A generation job is already running.");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunBatchGenAsync(req, ct);
            }
            finally
            {
                _gate.Release();
            }
        }, CancellationToken.None);

        await Task.CompletedTask;
    }

    /// <summary>Run Python Stage 1 (book → scenes.json). Requires XAI_API_KEY and python on PATH.</summary>
    public async Task StartStage1Async(StartStage1Request req)
    {
        if (!await _gate.WaitAsync(0))
            throw new InvalidOperationException("A generation job is already running.");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try { await RunStage1Async(req, ct); }
            finally { _gate.Release(); }
        }, CancellationToken.None);

        await Task.CompletedTask;
    }

    /// <summary>Run Python Stage 2 planner (scenes.json → blueprint). No API key required.</summary>
    public async Task StartStage2Async(StartStage2Request req)
    {
        if (!await _gate.WaitAsync(0))
            throw new InvalidOperationException("A generation job is already running.");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try { await RunStage2Async(req, ct); }
            finally { _gate.Release(); }
        }, CancellationToken.None);

        await Task.CompletedTask;
    }

    /// <summary>Generate 3 portrait variants via Python character design (needs XAI_API_KEY).</summary>
    public async Task StartCharacterVariantsAsync(StartCharacterVariantsRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.CharKey))
            throw new InvalidOperationException("charKey required");

        if (!await _gate.WaitAsync(0))
            throw new InvalidOperationException("A generation job is already running.");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try { await RunCharacterVariantsAsync(req, ct); }
            finally { _gate.Release(); }
        }, CancellationToken.None);

        await Task.CompletedTask;
    }

    /// <summary>Synchronous character lock/unlock via Python CLI (updates engine state).</summary>
    public async Task<string> RunCharacterDesignActionAsync(
        string projectId,
        string action,
        string charKey,
        int variantIndex = 1,
        string? imagePath = null,
        CancellationToken ct = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("A generation job is already running.");

        var root = _projects.WorkspaceRoot;
        var script = Path.Combine(root, "scripts", "character_design_cli.py");
        if (!File.Exists(script))
            throw new InvalidOperationException($"character_design_cli.py not found: {script}");

        var args = new StringBuilder();
        args.Append($"\"{script}\" --project \"{projectId}\" {action} --char \"{charKey}\"");
        if (action == "lock-variant")
            args.Append($" --variant-index {Math.Clamp(variantIndex, 1, 3)}");
        if (action == "lock-image" && !string.IsNullOrWhiteSpace(imagePath))
            args.Append($" --image \"{imagePath}\"");

        var (exit, stdout) = await RunPythonCaptureAsync(args.ToString(), root, ct);
        var last = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.Contains("\"ok\""))
            ?? stdout.Trim();
        if (exit != 0)
            throw new InvalidOperationException(TryParseCliError(last) ?? $"character design failed (exit {exit}): {last}");
        if (last.Contains("\"ok\": false", StringComparison.OrdinalIgnoreCase) ||
            last.Contains("\"ok\":false", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(TryParseCliError(last) ?? last);
        return last;
    }

    private async Task RunCharacterVariantsAsync(StartCharacterVariantsRequest req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        _projects.Activate(projectId);

        _snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "character",
            ProjectId = projectId,
            CharKey = req.CharKey,
            Message = $"Generating portraits for {req.CharKey}…",
            Index = 0,
            Total = 3,
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        await PublishAsync();

        try
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XAI_API_KEY")))
                throw new InvalidOperationException("XAI_API_KEY is not set (required for portrait generation).");

            var root = _projects.WorkspaceRoot;
            var script = Path.Combine(root, "scripts", "character_design_cli.py");
            if (!File.Exists(script))
                throw new InvalidOperationException($"character_design_cli.py not found: {script}");

            // -u = unbuffered so SignalR gets live Python print lines
            var args =
                $"-u \"{script}\" --project \"{projectId}\" generate --char \"{req.CharKey}\"";
            await AppendLogAsync($"Character design: python {args}");
            await UpdateAsync(s => s.Message = "Calling Grok image model (book refs when available)…");

            var exit = await RunPythonAsync(args, root, ct, onLine: line =>
            {
                // Map CLI/engine lines → coarse progress for the UI
                if (line.Contains("[progress]", StringComparison.OrdinalIgnoreCase))
                {
                    if (line.Contains("book_refs", StringComparison.OrdinalIgnoreCase))
                        _ = UpdateAsync(s => { s.Index = Math.Max(s.Index, 0); s.Message = line; });
                    else if (line.Contains("api", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("edit", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("generate", StringComparison.OrdinalIgnoreCase))
                        _ = UpdateAsync(s => { s.Index = Math.Max(s.Index, 1); s.Message = "Grok image request in flight…"; });
                    else if (line.Contains("saved", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("variant", StringComparison.OrdinalIgnoreCase))
                    {
                        // try "variant 2" / "variant_02" / "2/3"
                        var idx = TryParseVariantProgress(line);
                        _ = UpdateAsync(s =>
                        {
                            if (idx > 0) s.Index = Math.Clamp(idx, 0, 3);
                            s.Message = line;
                        });
                    }
                }
                else if (line.Contains("Character design", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("[Character design]", StringComparison.OrdinalIgnoreCase))
                {
                    _ = UpdateAsync(s =>
                    {
                        s.Index = Math.Max(s.Index, 1);
                        s.Message = line.Trim();
                    });
                }
                else if (line.Contains("_variant_0", StringComparison.OrdinalIgnoreCase))
                {
                    var idx = TryParseVariantProgress(line);
                    if (idx > 0)
                        _ = UpdateAsync(s => { s.Index = idx; s.Message = $"Saved variant {idx}/3"; });
                }
            });

            if (exit == 0)
            {
                await UpdateAsync(s => s.Index = 3);
                await FinishAsync("done", $"Variants ready for {req.CharKey}");
            }
            else
            {
                await FinishAsync("error", $"Portrait generation failed (exit {exit})", $"exit {exit}");
            }
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Character variants failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private static int TryParseVariantProgress(string line)
    {
        // character_buster_variant_02.png / variant 2 / 2/3
        var m = System.Text.RegularExpressions.Regex.Match(
            line, @"variant[_\s-]*0*([1-3])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
            return n;
        m = System.Text.RegularExpressions.Regex.Match(line, @"\b([1-3])\s*/\s*3\b");
        if (m.Success && int.TryParse(m.Groups[1].Value, out n))
            return n;
        return 0;
    }

    private static string? TryParseCliError(string line)
    {
        try
        {
            var start = line.IndexOf('{');
            if (start < 0) return line;
            using var doc = JsonDocument.Parse(line[start..]);
            if (doc.RootElement.TryGetProperty("error", out var e))
                return e.GetString();
        }
        catch { /* ignore */ }
        return line.Length > 300 ? line[..300] : line;
    }

    private async Task RunStage1Async(StartStage1Request req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        _projects.Activate(projectId);

        _snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "stage1",
            ProjectId = projectId,
            Message = "Starting Stage 1…",
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        await PublishAsync();

        try
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XAI_API_KEY")))
                throw new InvalidOperationException("XAI_API_KEY is not set (required for Stage 1 LLM).");

            var root = _projects.WorkspaceRoot;
            var script = Path.Combine(root, "scripts", "two_stage_adaptation", "run_stage1_from_book.py");
            if (!File.Exists(script))
                throw new InvalidOperationException($"Stage 1 script not found: {script}");

            var outPath = _projects.ResolveScenesJsonPath(projectId);
            var args = new StringBuilder();
            args.Append($"\"{script}\" --project \"{projectId}\"");
            args.Append($" --out \"{outPath}\"");
            args.Append($" --model \"{(string.IsNullOrWhiteSpace(req.Model) ? "grok-4.5" : req.Model)}\"");
            args.Append($" --chunk-pages {Math.Clamp(req.ChunkPages, 5, 30)}");
            if (req.TotalMinutes is int mins && mins > 0)
                args.Append($" --total-minutes {Math.Clamp(mins, 3, 180)}");
            if (req.Resume)
                args.Append(" --resume");
            if (req.MaxChunks > 0)
                args.Append($" --max-chunks {req.MaxChunks}");

            await AppendLogAsync($"Stage 1: python {args}");
            var exit = await RunPythonAsync(args.ToString(), root, ct);
            if (exit == 0)
                await FinishAsync("done", "Stage 1 complete");
            else if (exit == 2)
                await FinishAsync("done", "Stage 1 finished with verification warnings (exit 2)");
            else
                await FinishAsync("error", $"Stage 1 failed (exit {exit})", $"exit {exit}");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Stage 1 failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task RunStage2Async(StartStage2Request req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        _projects.Activate(projectId);

        _snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "stage2",
            ProjectId = projectId,
            Message = "Starting Stage 2 planner…",
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        await PublishAsync();

        try
        {
            var root = _projects.WorkspaceRoot;
            var script = Path.Combine(root, "scripts", "two_stage_adaptation", "stage2_plan_grok.py");
            if (!File.Exists(script))
                throw new InvalidOperationException($"Stage 2 script not found: {script}");

            var stage1 = _projects.ResolveScenesJsonPath(projectId);
            if (!File.Exists(stage1))
                throw new InvalidOperationException($"Stage 1 bible not found: {stage1}");

            var outPath = _projects.FindBlueprintPath(projectId)
                ?? Path.Combine(_projects.GetProjectDir(projectId), "blueprint.clips.grok.json");

            // Backup existing blueprint
            if (File.Exists(outPath))
            {
                var bak = outPath + $".bak_pre_stage2_{DateTime.Now:yyyyMMdd_HHmmss}";
                File.Copy(outPath, bak, overwrite: true);
                await AppendLogAsync($"Backed up blueprint → {Path.GetFileName(bak)}");
            }

            var resolution = string.IsNullOrWhiteSpace(req.Resolution) ? "720p" : req.Resolution;
            var scenes = string.IsNullOrWhiteSpace(req.Scenes) ? "all" : req.Scenes;
            var args =
                $"\"{script}\" --stage1 \"{stage1}\" --out \"{outPath}\" " +
                $"--resolution \"{resolution}\" --scenes \"{scenes}\"";

            await AppendLogAsync($"Stage 2: python {args}");
            var exit = await RunPythonAsync(args.ToString(), root, ct);
            if (exit == 0)
            {
                // Refresh counts for message
                var s2 = _projects.GetAdaptationStatus(projectId).Stage2;
                await FinishAsync(
                    "done",
                    $"Stage 2 complete: {s2.Stage2Scenes} scenes · {s2.Stage2Clips} clips");
            }
            else
            {
                await FinishAsync("error", $"Stage 2 failed (exit {exit})", $"exit {exit}");
            }
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Stage 2 failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task<int> RunPythonAsync(
        string arguments,
        string workingDir,
        CancellationToken ct,
        Action<string>? onLine = null)
    {
        var (exit, _) = await RunPythonCaptureAsync(arguments, workingDir, ct, logToJob: true, onLine: onLine);
        return exit;
    }

    private async Task<(int Exit, string Stdout)> RunPythonCaptureAsync(
        string arguments,
        string workingDir,
        CancellationToken ct,
        bool logToJob = false,
        Action<string>? onLine = null)
    {
        var python = string.IsNullOrWhiteSpace(_opts.PythonExecutable)
            ? "python"
            : _opts.PythonExecutable;

        // If caller already passed "-u script…", don't double-wrap
        var args = arguments.TrimStart();
        if (!args.StartsWith("-u ", StringComparison.Ordinal) &&
            !args.StartsWith("-u\"", StringComparison.Ordinal))
        {
            // Prefer unbuffered child when running scripts for live SignalR logs
            if (args.Contains(".py", StringComparison.OrdinalIgnoreCase))
                args = "-u " + args;
        }

        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        var key = Environment.GetEnvironmentVariable("XAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(key) && !psi.Environment.ContainsKey("XAI_API_KEY"))
            psi.Environment["XAI_API_KEY"] = key;

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdout = new StringBuilder();

        proc.OutputDataReceived += async (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            stdout.AppendLine(e.Data);
            try { onLine?.Invoke(e.Data); } catch { /* ignore progress parse errors */ }
            if (logToJob)
                await AppendLogAsync(e.Data);
        };
        proc.ErrorDataReceived += async (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            stdout.AppendLine(e.Data);
            try { onLine?.Invoke(e.Data); } catch { /* ignore */ }
            if (logToJob)
                await AppendLogAsync(e.Data);
        };
        proc.Exited += (_, _) => tcs.TrySetResult(proc.ExitCode);

        if (!proc.Start())
            throw new InvalidOperationException($"Failed to start {python}");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await using var reg = ct.Register(() =>
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }
        });

        var exit = await tcs.Task.WaitAsync(ct);
        await Task.Delay(100, CancellationToken.None);
        return (exit, stdout.ToString());
    }

    private async Task RunBatchGenAsync(StartBatchGenRequest req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        _projects.Activate(projectId);

        var scenes = req.Scenes.Distinct().OrderBy(s => s).ToList();
        _snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "batch",
            ProjectId = projectId,
            Message = $"Batch: {scenes.Count} scene(s)…",
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        await PublishAsync();

        try
        {
            if (!_grok.IsConfigured)
                throw new InvalidOperationException("XAI_API_KEY is not set.");

            using var bp = _projects.LoadBlueprint(projectId)
                ?? throw new InvalidOperationException(
                    $"No Stage 2 blueprint for project {projectId}. Run Stage 2 first.");

            var projectDir = _projects.GetProjectDir(projectId);
            Directory.CreateDirectory(Path.Combine(projectDir, "assets", "video"));

            // Pre-count work units
            var work = new List<(int Scene, int Clip, JsonElement ClipEl)>();
            foreach (var sn in scenes)
            {
                var sceneEl = FindScene(bp.RootElement, sn);
                if (sceneEl is null)
                {
                    await AppendLogAsync($"Scene {sn}: not in blueprint — skip");
                    continue;
                }
                if (!sceneEl.Value.TryGetProperty("veo_clips", out var clipsEl) ||
                    clipsEl.ValueKind != JsonValueKind.Array)
                {
                    await AppendLogAsync($"Scene {sn}: no veo_clips — skip");
                    continue;
                }

                foreach (var c in clipsEl.EnumerateArray())
                {
                    var cn = c.TryGetProperty("clip_number", out var n) && n.TryGetInt32(out var v) ? v : 0;
                    if (cn <= 0) continue;
                    var path = Path.Combine(projectDir, "assets", "video", $"scene_{sn:D2}_clip_{cn:D2}.mp4");
                    var missing = !File.Exists(path) || new FileInfo(path).Length < 1024;
                    if (!req.OnlyMissing || missing)
                        work.Add((sn, cn, c.Clone()));
                }
            }

            if (work.Count == 0)
            {
                await AppendLogAsync("Batch: nothing to generate (only_missing).");
                await FinishAsync("done", "No clips to generate");
                return;
            }

            await UpdateAsync(s =>
            {
                s.Total = work.Count;
                s.Index = 0;
                s.Message = $"Batch: {work.Count} clip(s) across {scenes.Count} scene(s)";
            });
            await AppendLogAsync(_snapshot.Message!);

            var done = 0;
            var failed = 0;
            for (var i = 0; i < work.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (sn, cn, clip) = work[i];
                await UpdateAsync(s =>
                {
                    s.Index = i + 1;
                    s.Scene = sn;
                    s.Clip = cn;
                    s.Message = $"Generating S{sn:D2} C{cn} ({i + 1}/{work.Count})…";
                });
                await AppendLogAsync(_snapshot.Message!);

                try
                {
                    await GenerateOneClipAsync(projectDir, sn, cn, clip, ct);
                    done++;
                    await AppendLogAsync($"Done S{sn:D2} C{cn}");
                }
                catch (OperationCanceledException)
                {
                    await FinishAsync("cancelled", "Cancelled by user");
                    return;
                }
                catch (Exception ex)
                {
                    failed++;
                    _log.LogError(ex, "Clip S{Scene}C{Clip} failed", sn, cn);
                    await AppendLogAsync($"Failed S{sn:D2} C{cn}: {ex.Message}");
                }
            }

            var status = failed > 0 && done == 0 ? "error" : "done";
            var msg = failed > 0
                ? $"Batch finished with errors ({done} ok, {failed} failed)"
                : $"Batch finished ({done} clip(s))";
            await FinishAsync(status, msg, failed > 0 ? msg : null);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Batch gen failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task RunSceneGenAsync(StartSceneGenRequest req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        _projects.Activate(projectId);

        _snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "scene",
            ProjectId = projectId,
            Scene = req.Scene,
            Message = "Starting…",
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        await PublishAsync();

        try
        {
            if (!_grok.IsConfigured)
                throw new InvalidOperationException("XAI_API_KEY is not set.");

            using var bp = _projects.LoadBlueprint(projectId)
                ?? throw new InvalidOperationException(
                    $"No Stage 2 blueprint for project {projectId}. Run Stage 2 first.");

            var sceneEl = FindScene(bp.RootElement, req.Scene)
                ?? throw new InvalidOperationException($"Scene {req.Scene} not in blueprint.");

            if (!sceneEl.TryGetProperty("veo_clips", out var clipsEl) ||
                clipsEl.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"Scene {req.Scene} has no veo_clips.");
            }

            var clips = clipsEl.EnumerateArray().ToList();
            var projectDir = _projects.GetProjectDir(projectId);
            var videoDir = Path.Combine(projectDir, "assets", "video");
            Directory.CreateDirectory(videoDir);

            var todo = new List<(int ClipNum, JsonElement Clip)>();
            foreach (var c in clips)
            {
                var cn = c.TryGetProperty("clip_number", out var n) && n.TryGetInt32(out var v) ? v : 0;
                if (cn <= 0) continue;
                var path = Path.Combine(videoDir, $"scene_{req.Scene:D2}_clip_{cn:D2}.mp4");
                var missing = !File.Exists(path) || new FileInfo(path).Length < 1024;
                if (!req.OnlyMissing || missing)
                    todo.Add((cn, c.Clone()));
            }

            if (todo.Count == 0)
            {
                await AppendLogAsync($"Scene {req.Scene}: nothing to generate (only_missing).");
                await FinishAsync("done", "No clips to generate");
                return;
            }

            var startMsg = $"Scene {req.Scene}: {todo.Count} clip(s)";
            await UpdateAsync(s =>
            {
                s.Total = todo.Count;
                s.Index = 0;
                s.Message = startMsg;
            });
            await AppendLogAsync(startMsg);

            var done = 0;
            var failed = 0;
            for (var i = 0; i < todo.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (cn, clip) = todo[i];
                await UpdateAsync(s =>
                {
                    s.Index = i + 1;
                    s.Clip = cn;
                    s.Message = $"Generating S{req.Scene:D2} C{cn} ({i + 1}/{todo.Count})…";
                });
                await AppendLogAsync(_snapshot.Message!);

                try
                {
                    await GenerateOneClipAsync(projectDir, req.Scene, cn, clip, ct);
                    done++;
                    await AppendLogAsync($"Done S{req.Scene:D2} C{cn}");
                }
                catch (OperationCanceledException)
                {
                    await FinishAsync("cancelled", "Cancelled by user");
                    return;
                }
                catch (Exception ex)
                {
                    failed++;
                    _log.LogError(ex, "Clip S{Scene}C{Clip} failed", req.Scene, cn);
                    await AppendLogAsync($"Failed S{req.Scene:D2} C{cn}: {ex.Message}");
                }
            }

            var status = failed > 0 && done == 0 ? "error" : "done";
            var msg = failed > 0
                ? $"Finished with errors ({done} ok, {failed} failed)"
                : $"Generation finished ({done} clip(s))";
            await FinishAsync(status, msg, failed > 0 ? msg : null);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scene gen failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task GenerateOneClipAsync(
        string projectDir,
        int scene,
        int clip,
        JsonElement clipEl,
        CancellationToken ct)
    {
        var prompt = clipEl.TryGetProperty("visual_prompt", out var vp)
            ? vp.GetString() ?? ""
            : "";
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("clip missing visual_prompt");

        // Style lock reinforce for humans (port of product rule)
        if (prompt.Contains("Character_Mom", StringComparison.Ordinal) ||
            prompt.Contains("Character_Daddy", StringComparison.Ordinal) ||
            prompt.Contains("Character_Mom", StringComparison.OrdinalIgnoreCase))
        {
            if (!prompt.Contains("STYLE LOCK", StringComparison.OrdinalIgnoreCase))
            {
                prompt =
                    "STYLE LOCK: stylized 3D animated children's picture-book CG " +
                    "(same render family as the cartoon dog) -- not photoreal, not live-action. " +
                    prompt;
            }
        }

        if (prompt.Length > 4000)
            prompt = prompt[..3990] + "…";

        var duration = _opts.DefaultDurationSeconds;
        if (clipEl.TryGetProperty("duration_seconds", out var d) && d.TryGetInt32(out var ds))
            duration = Math.Clamp(ds, 1, 15);
        // ref-to-video max often 10; text-only keep config default
        duration = Math.Min(duration, 10);

        var model = _opts.DefaultModel;
        var resolution = _opts.DefaultResolution;

        await AppendLogAsync($"  [Grok] Submit S{scene:D2}C{clip} duration={duration}s res={resolution}");
        var requestId = await _grok.SubmitGenerationAsync(prompt, duration, resolution, model, ct);
        await AppendLogAsync($"  [Grok] request_id={requestId}");

        var url = await _grok.PollForVideoUrlAsync(
            requestId,
            msg => { _ = AppendLogAsync($"  [Grok] {msg}"); },
            ct);

        var outPath = Path.Combine(
            projectDir, "assets", "video", $"scene_{scene:D2}_clip_{clip:D2}.mp4");
        await _grok.DownloadToFileAsync(url, outPath, ct);
        await AppendLogAsync($"  [Grok] saved {outPath}");

        try
        {
            var cont = clipEl.TryGetProperty("veo_continuation_source", out var ce)
                ? ce.GetString() ?? "none"
                : "none";
            var projectId = _snapshot.ProjectId ?? _projects.ActiveProjectId;
            _costs.RecordVideoGeneration(
                projectId,
                scene,
                clip,
                duration,
                resolution,
                model,
                hasRefImage: false,
                isExtend: string.Equals(cont, "extend_previous", StringComparison.OrdinalIgnoreCase),
                requestId: requestId);
            await AppendLogAsync($"  [Cost] tracked list-rate for S{scene:D2}C{clip}");
        }
        catch (Exception ex)
        {
            await AppendLogAsync($"  [Cost] ledger write skipped: {ex.Message}");
        }
    }

    private static JsonElement? FindScene(JsonElement root, int sceneNum)
    {
        if (!root.TryGetProperty("scenes", out var scenes) ||
            scenes.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var s in scenes.EnumerateArray())
        {
            if (s.TryGetProperty("scene_number", out var n) && n.TryGetInt32(out var sn) && sn == sceneNum)
                return s;
        }
        return null;
    }

    private async Task AppendLogAsync(string message)
    {
        _logLines.Enqueue(message);
        await UpdateAsync(s =>
        {
            s.Log.Add(message);
            if (s.Log.Count > 80)
                s.Log = s.Log.TakeLast(80).ToList();
            s.Message = message;
        });
        if (_sink is not null)
            await _sink.OnJobLogAsync(message);
    }

    private async Task UpdateAsync(Action<JobSnapshot> mutate)
    {
        mutate(_snapshot);
        await PublishAsync();
    }

    private async Task FinishAsync(string status, string message, string? error = null)
    {
        await UpdateAsync(s =>
        {
            s.Status = status;
            s.Message = message;
            s.Error = error;
            s.FinishedAt = DateTimeOffset.UtcNow;
        });
        await AppendLogAsync(message);
    }

    private async Task PublishAsync()
    {
        if (_sink is not null)
            await _sink.OnJobUpdatedAsync(Clone(_snapshot));
    }

    private static JobSnapshot Clone(JobSnapshot s) => new()
    {
        Status = s.Status,
        Kind = s.Kind,
        Message = s.Message,
        ProjectId = s.ProjectId,
        CharKey = s.CharKey,
        Scene = s.Scene,
        Clip = s.Clip,
        Index = s.Index,
        Total = s.Total,
        Log = s.Log.ToList(),
        Error = s.Error,
        StartedAt = s.StartedAt,
        FinishedAt = s.FinishedAt,
    };
}
