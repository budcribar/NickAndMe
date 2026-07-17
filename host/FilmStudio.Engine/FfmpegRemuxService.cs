using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FilmStudio.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

/// <summary>
/// FFmpeg scene remux + WIP movie rebuild.
/// Resolves ffmpeg from: config path → NuGet-shipped Resources/ffmpeg.exe
/// (Soenneker.Libraries.FFmpeg) → PATH.
/// Streams stderr/stdout progress to <paramref name="onProgress"/> (SignalR job log).
/// </summary>
public sealed class FfmpegRemuxService
{
    private static readonly Regex DurationRe = new(
        @"Duration:\s*(\d{1,2}):(\d{2}):(\d{2}(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TimeEqualsRe = new(
        @"time=\s*(\d{1,2}):(\d{2}):(\d{2}(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FrameRe = new(
        @"frame=\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SpeedRe = new(
        @"speed=\s*([\d.]+x?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ProjectStore _projects;
    private readonly FilmStudioOptions _opts;
    private readonly ILogger<FfmpegRemuxService> _log;
    private string? _resolvedPath;
    private readonly object _resolveLock = new();

    public FfmpegRemuxService(
        ProjectStore projects,
        IOptions<FilmStudioOptions> opts,
        ILogger<FfmpegRemuxService> log)
    {
        _projects = projects;
        _opts = opts.Value;
        _log = log;
    }

    /// <summary>Resolved ffmpeg executable path (absolute when possible).</summary>
    public string FfmpegPath => ResolveFfmpegPath();

    public bool IsAvailable()
    {
        try
        {
            var path = ResolveFfmpegPath();
            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Concat clips for a scene into assets/video/scene_XX.mp4.</summary>
    public async Task<string?> RemuxSceneAsync(
        string projectId,
        int sceneNum,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        EnsureAvailable(onProgress);

        var projectDir = _projects.GetProjectDir(projectId);
        var videoDir = Path.Combine(projectDir, "assets", "video");
        Directory.CreateDirectory(videoDir);

        var clips = ListSceneClipFiles(projectId, videoDir, sceneNum);
        if (clips.Count == 0)
            throw new InvalidOperationException(
                $"No clip files for scene {sceneNum} under {videoDir} " +
                $"(expected scene_{sceneNum:D2}_clip_XX.mp4 only — not .native sidecars)");

        onProgress?.Invoke($"Remux S{sceneNum:D2}: {clips.Count} clip(s) via {Path.GetFileName(FfmpegPath)}…");
        onProgress?.Invoke("Probing clip durations…");
        var totalSec = await EstimateTotalDurationAsync(clips, onProgress, ct);
        if (totalSec is > 0)
            onProgress?.Invoke($"Estimated total duration ~{FormatHms(totalSec.Value)}");

        var listFile = Path.Combine(videoDir, $"_concat_s{sceneNum:D2}.txt");
        var sb = new StringBuilder();
        foreach (var c in clips)
        {
            var escaped = c.Replace("\\", "/").Replace("'", "'\\''");
            sb.AppendLine($"file '{escaped}'");
        }
        await File.WriteAllTextAsync(listFile, sb.ToString(), ct);

        var outPath = Path.Combine(videoDir, $"scene_{sceneNum:D2}.mp4");
        var args = $"-y -f concat -safe 0 -i \"{listFile}\" -c copy \"{outPath}\"";
        var (exit, log) = await RunFfmpegAsync(
            args, projectDir, ct, onProgress, totalSec, label: $"S{sceneNum:D2} copy");
        try { File.Delete(listFile); } catch { /* ignore */ }

        if (exit != 0)
        {
            onProgress?.Invoke("Concat copy failed — re-encoding…");
            await File.WriteAllTextAsync(listFile, sb.ToString(), ct);
            args =
                $"-y -f concat -safe 0 -i \"{listFile}\" -c:v libx264 -preset veryfast -crf 20 " +
                $"-c:a aac -b:a 160k \"{outPath}\"";
            (exit, log) = await RunFfmpegAsync(
                args, projectDir, ct, onProgress, totalSec, label: $"S{sceneNum:D2} encode");
            try { File.Delete(listFile); } catch { /* ignore */ }
        }

        if (exit != 0 || !File.Exists(outPath) || new FileInfo(outPath).Length < 1024)
            throw new InvalidOperationException($"FFmpeg remux failed for scene {sceneNum}: {TrimLog(log)}");

        WriteSceneSourcesManifest(outPath, clips, totalSec);
        if (totalSec is > 0)
            MediaDurationProbe.WriteDurationSidecar(outPath, totalSec.Value);
        onProgress?.Invoke($"Remuxed → {Path.GetFileName(outPath)} ({clips.Count} clip(s))");
        return outPath;
    }

    public static string SceneSourcesManifestPath(string compositePath) =>
        compositePath + ".sources.json";

    private static void WriteSceneSourcesManifest(
        string compositePath,
        IReadOnlyList<string> clipFiles,
        double? totalDurationSeconds = null)
    {
        try
        {
            var entries = clipFiles.Select(f =>
            {
                var fi = new FileInfo(f);
                return new Dictionary<string, object?>
                {
                    ["name"] = fi.Name,
                    ["bytes"] = fi.Length,
                    ["mtimeUtc"] = fi.LastWriteTimeUtc.ToString("o"),
                };
            }).ToList();
            var doc = new Dictionary<string, object?>
            {
                ["builtAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["count"] = entries.Count,
                ["clips"] = entries,
                ["strict"] = true, // exact scene_SS_clip_CC.mp4 only
                ["totalDurationSeconds"] = totalDurationSeconds is > 0
                    ? Math.Round(totalDurationSeconds.Value, 3)
                    : null,
            };
            File.WriteAllText(
                SceneSourcesManifestPath(compositePath),
                JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        catch
        {
            // Non-fatal
        }
    }

    /// <summary>
    /// True if composite is missing, clips are newer, has no sources manifest (legacy/polluted remux),
    /// or manifest clip set does not match current exact clips (blueprint-filtered).
    /// </summary>
    public bool IsSceneCompositeStale(string projectId, int sceneNum)
    {
        var projectDir = _projects.GetProjectDir(projectId);
        var videoDir = Path.Combine(projectDir, "assets", "video");
        var expected = ListSceneClipFiles(projectId, videoDir, sceneNum);
        if (expected.Count == 0)
            return false; // nothing to remux

        var composite = _projects.ResolveCompositePath(projectId, sceneNum);
        if (composite is null || !File.Exists(composite))
            return true;

        var maxClip = expected.Max(f => new FileInfo(f).LastWriteTimeUtc);
        if (maxClip > new FileInfo(composite).LastWriteTimeUtc.AddSeconds(1))
            return true;

        // Legacy composites (e.g. included .native.mp4) have no strict manifest → dirty
        var manifestPath = SceneSourcesManifestPath(composite);
        // Also check path next to remux output name
        var remuxOut = Path.Combine(videoDir, $"scene_{sceneNum:D2}.mp4");
        if (!File.Exists(manifestPath) && File.Exists(remuxOut))
            manifestPath = SceneSourcesManifestPath(remuxOut);

        if (!File.Exists(manifestPath))
            return true;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("clips", out var clipsEl) ||
                clipsEl.ValueKind != JsonValueKind.Array)
                return true;

            var recorded = new List<string>();
            foreach (var el in clipsEl.EnumerateArray())
            {
                if (el.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                    recorded.Add(name);
            }

            var expectedNames = expected
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Cast<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var recSorted = recorded.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (!expectedNames.SequenceEqual(recSorted, StringComparer.OrdinalIgnoreCase))
                return true;

            // Per-file size/mtime drift
            foreach (var path in expected)
            {
                var name = Path.GetFileName(path);
                var fi = new FileInfo(path);
                foreach (var el in clipsEl.EnumerateArray())
                {
                    if (!el.TryGetProperty("name", out var n) ||
                        !string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    long bytes = 0;
                    if (el.TryGetProperty("bytes", out var b) && b.TryGetInt64(out var bl))
                        bytes = bl;
                    if (bytes != fi.Length)
                        return true;
                    break;
                }
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Concat scene/clip files into WIP movie path from config.</summary>
    public async Task<string?> RebuildWipAsync(
        string projectId,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        EnsureAvailable(onProgress);

        var projectDir = _projects.GetProjectDir(projectId);
        var cfg = LoadConfig(projectDir);
        var wipRel = cfg.TryGetValue("wip_movie_path", out var w)
            ? w?.ToString() ?? "assets/movie_wip.mp4"
            : "assets/movie_wip.mp4";
        var wipPath = Path.IsPathRooted(wipRel)
            ? wipRel
            : Path.Combine(projectDir, wipRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(wipPath)!);

        var videoDir = Path.Combine(projectDir, "assets", "video");
        // Prefer Stage 2 scene order / set when blueprint exists
        var sceneFiles = _projects.ListWipSourceFilesForProject(projectId);
        if (sceneFiles.Count == 0)
            sceneFiles = ListWipSourceFiles(videoDir);

        if (sceneFiles.Count == 0)
            throw new InvalidOperationException("No scene or clip videos found to build WIP.");

        onProgress?.Invoke($"WIP rebuild from {sceneFiles.Count} file(s) via {Path.GetFileName(FfmpegPath)}…");
        onProgress?.Invoke("Probing input durations…");
        var totalSec = await EstimateTotalDurationAsync(sceneFiles, onProgress, ct);
        if (totalSec is > 0)
            onProgress?.Invoke($"Estimated total duration ~{FormatHms(totalSec.Value)}");

        var listFile = Path.Combine(videoDir, "_concat_wip.txt");
        var sb = new StringBuilder();
        foreach (var c in sceneFiles)
        {
            var escaped = c.Replace("\\", "/").Replace("'", "'\\''");
            sb.AppendLine($"file '{escaped}'");
        }
        await File.WriteAllTextAsync(listFile, sb.ToString(), ct);

        var args = $"-y -f concat -safe 0 -i \"{listFile}\" -c copy \"{wipPath}\"";
        var (exit, log) = await RunFfmpegAsync(
            args, projectDir, ct, onProgress, totalSec, label: "WIP copy");
        if (exit != 0)
        {
            onProgress?.Invoke("WIP stream-copy failed — re-encoding…");
            args =
                $"-y -f concat -safe 0 -i \"{listFile}\" -c:v libx264 -preset veryfast -crf 20 " +
                $"-c:a aac -b:a 160k \"{wipPath}\"";
            (exit, log) = await RunFfmpegAsync(
                args, projectDir, ct, onProgress, totalSec, label: "WIP encode");
        }
        try { File.Delete(listFile); } catch { /* ignore */ }

        if (exit != 0 || !File.Exists(wipPath))
            throw new InvalidOperationException($"FFmpeg WIP rebuild failed: {TrimLog(log)}");

        WriteWipSourcesManifest(projectId, wipPath, sceneFiles);
        onProgress?.Invoke($"WIP → {wipPath} ({sceneFiles.Count} source(s))");
        return wipPath;
    }

    /// <summary>
    /// Ordered inputs for WIP concat: scene composites first, else exact clip files.
    /// Shared by rebuild and freshness checks (add/delete detection).
    /// </summary>
    public static List<string> ListWipSourceFiles(string videoDir)
    {
        if (!Directory.Exists(videoDir))
            return new List<string>();

        var sceneFiles = Directory.GetFiles(videoDir, "scene_*.mp4")
            .Where(f => RegexSceneOnly(Path.GetFileName(f)))
            .Where(f =>
            {
                try { return new FileInfo(f).Length >= 1024; }
                catch { return false; }
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sceneFiles.Count > 0)
            return sceneFiles;

        return Directory.GetFiles(videoDir, "scene_*_clip_*.mp4")
            .Where(f => IsExactClipFileName(Path.GetFileName(f)))
            .Where(f =>
            {
                try { return new FileInfo(f).Length >= 1024; }
                catch { return false; }
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Sidecar next to WIP listing concat sources (for add/delete/outdate detection).</summary>
    public static string WipSourcesManifestPath(string wipPath) =>
        wipPath + ".sources.json";

    private void WriteWipSourcesManifest(
        string projectId,
        string wipPath,
        IReadOnlyList<string> sourceFiles)
    {
        try
        {
            var entries = sourceFiles.Select(f =>
            {
                var fi = new FileInfo(f);
                return new Dictionary<string, object?>
                {
                    ["name"] = fi.Name,
                    ["bytes"] = fi.Length,
                    ["mtimeUtc"] = fi.LastWriteTimeUtc.ToString("o"),
                };
            }).ToList();

            var sceneNumbers = _projects.GetBlueprintSceneNumbers(projectId)
                               ?? sourceFiles
                                   .Select(f => Path.GetFileName(f))
                                   .Where(n => n is not null && RegexSceneOnly(n))
                                   .Select(n => int.TryParse(n!.AsSpan(6, 2), out var sn) ? sn : 0)
                                   .Where(n => n > 0)
                                   .Distinct()
                                   .OrderBy(n => n)
                                   .ToList();

            string? bpMtime = null;
            var bpPath = _projects.FindBlueprintPath(projectId);
            if (bpPath is not null && File.Exists(bpPath))
                bpMtime = new FileInfo(bpPath).LastWriteTimeUtc.ToString("o");

            var doc = new Dictionary<string, object?>
            {
                ["builtAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["count"] = entries.Count,
                ["sources"] = entries,
                ["sceneNumbers"] = sceneNumbers,
                ["blueprintMtimeUtc"] = bpMtime,
            };
            var path = WipSourcesManifestPath(wipPath);
            File.WriteAllText(path,
                JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        catch
        {
            // Non-fatal — freshness falls back to mtime heuristics
        }
    }

    private void EnsureAvailable(Action<string>? onProgress)
    {
        if (IsAvailable())
        {
            onProgress?.Invoke($"ffmpeg: {FfmpegPath}");
            return;
        }

        throw new InvalidOperationException(
            "ffmpeg not found. Expected NuGet-shipped Resources/ffmpeg.exe " +
            "(Soenneker.Libraries.FFmpeg), FilmStudio:FfmpegPath, or ffmpeg on PATH.");
    }

    /// <summary>
    /// Resolution order:
    /// 1) FilmStudio:FfmpegPath when set to an existing file (or usable name)
    /// 2) App output Resources/ffmpeg.exe (Soenneker package content)
    /// 3) Same folder as the host assembly
    /// 4) Engine assembly Resources/
    /// 5) Bare "ffmpeg" (PATH)
    /// </summary>
    private string ResolveFfmpegPath()
    {
        if (_resolvedPath is not null)
            return _resolvedPath;

        lock (_resolveLock)
        {
            if (_resolvedPath is not null)
                return _resolvedPath;

            var candidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(_opts.FfmpegPath))
            {
                var configured = _opts.FfmpegPath.Trim();
                if (File.Exists(configured))
                    candidates.Add(Path.GetFullPath(configured));
                else if (!string.Equals(configured, "ffmpeg", StringComparison.OrdinalIgnoreCase))
                {
                    var rel = Path.Combine(AppContext.BaseDirectory, configured);
                    if (File.Exists(rel))
                        candidates.Add(Path.GetFullPath(rel));
                }
            }

            var bases = new[]
            {
                AppContext.BaseDirectory,
                Path.GetDirectoryName(typeof(FfmpegRemuxService).Assembly.Location) ?? "",
                Directory.GetCurrentDirectory(),
            }.Where(b => !string.IsNullOrWhiteSpace(b)).Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var root in bases)
            {
                candidates.Add(Path.Combine(root, "Resources", "ffmpeg.exe"));
                candidates.Add(Path.Combine(root, "ffmpeg.exe"));
                candidates.Add(Path.Combine(root, "bin", "ffmpeg.exe"));
                candidates.Add(Path.Combine(root, "ffmpeg", "ffmpeg.exe"));
            }

            try
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var pkgRoot = Path.Combine(userProfile, ".nuget", "packages", "soenneker.libraries.ffmpeg");
                if (Directory.Exists(pkgRoot))
                {
                    foreach (var verDir in Directory.GetDirectories(pkgRoot)
                                 .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                    {
                        candidates.Add(Path.Combine(verDir, "content", "Resources", "ffmpeg.exe"));
                        candidates.Add(Path.Combine(verDir, "contentFiles", "any", "net9.0", "Resources", "ffmpeg.exe"));
                    }
                }
            }
            catch { /* ignore */ }

            foreach (var c in candidates)
            {
                try
                {
                    if (File.Exists(c) && new FileInfo(c).Length > 100_000)
                    {
                        _resolvedPath = Path.GetFullPath(c);
                        _log.LogInformation("Using bundled/local ffmpeg: {Path}", _resolvedPath);
                        return _resolvedPath;
                    }
                }
                catch { /* ignore */ }
            }

            _resolvedPath = string.IsNullOrWhiteSpace(_opts.FfmpegPath) ? "ffmpeg" : _opts.FfmpegPath.Trim();
            return _resolvedPath;
        }
    }

    private static bool RegexSceneOnly(string name) =>
        Regex.IsMatch(name, @"^scene_\d{2}\.mp4$", RegexOptions.IgnoreCase);

    /// <summary>Strict: scene_01_clip_02.mp4 only — not scene_01_clip_02.mp4.native.mp4.</summary>
    private static readonly Regex ExactClipNameRe = new(
        @"^scene_(\d{2})_clip_(\d{2})\.mp4$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool IsExactClipFileName(string? fileName) =>
        !string.IsNullOrEmpty(fileName) && ExactClipNameRe.IsMatch(fileName);

    /// <summary>
    /// Clips for scene remux: only exact <c>scene_SS_clip_CC.mp4</c> names (≥1KB).
    /// When Stage 2 blueprint lists <c>veo_clips</c>, only those clip numbers are included
    /// (drops orphans like an old clip_04 and never picks .native sidecars).
    /// </summary>
    private List<string> ListSceneClipFiles(string projectId, string videoDir, int sceneNum)
    {
        if (!Directory.Exists(videoDir)) return new();

        var byClip = new SortedDictionary<int, string>();
        foreach (var f in Directory.EnumerateFiles(videoDir, $"scene_{sceneNum:D2}_clip_*.mp4"))
        {
            var name = Path.GetFileName(f);
            var m = ExactClipNameRe.Match(name);
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, out var sn) || sn != sceneNum) continue;
            if (!int.TryParse(m.Groups[2].Value, out var cn) || cn <= 0) continue;
            try
            {
                if (new FileInfo(f).Length < 1024) continue;
            }
            catch { continue; }

            byClip[cn] = f;
        }

        var allowed = TryBlueprintClipNumbers(projectId, sceneNum);
        if (allowed is { Count: > 0 })
        {
            return allowed
                .Where(cn => byClip.ContainsKey(cn))
                .OrderBy(cn => cn)
                .Select(cn => byClip[cn])
                .ToList();
        }

        return byClip.Values.ToList();
    }

    /// <summary>Clip numbers from blueprint veo_clips for this scene, or null if unknown.</summary>
    private HashSet<int>? TryBlueprintClipNumbers(string projectId, int sceneNum)
    {
        try
        {
            using var bp = _projects.LoadBlueprint(projectId);
            if (bp is null) return null;
            if (!bp.RootElement.TryGetProperty("scenes", out var scenes) ||
                scenes.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var s in scenes.EnumerateArray())
            {
                var sn = s.TryGetProperty("scene_number", out var snEl) && snEl.TryGetInt32(out var v)
                    ? v
                    : 0;
                if (sn != sceneNum) continue;
                if (!s.TryGetProperty("veo_clips", out var clips) ||
                    clips.ValueKind != JsonValueKind.Array)
                    return null;

                var set = new HashSet<int>();
                foreach (var c in clips.EnumerateArray())
                {
                    if (c.TryGetProperty("clip_number", out var cnEl) && cnEl.TryGetInt32(out var cn) && cn > 0)
                        set.Add(cn);
                }
                return set.Count > 0 ? set : null;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Blueprint clip filter unavailable for S{Scene}", sceneNum);
        }
        return null;
    }

    private static Dictionary<string, object?> LoadConfig(string projectDir)
    {
        var path = Path.Combine(projectDir, "pipeline_config.json");
        if (!File.Exists(path)) return new();
        try
        {
            return GrokChatClient.ParseJsonObject(File.ReadAllText(path));
        }
        catch { return new(); }
    }

    /// <summary>
    /// Sum per-file durations via <c>ffmpeg -i</c> Duration lines (no ffprobe required).
    /// </summary>
    private async Task<double?> EstimateTotalDurationAsync(
        IReadOnlyList<string> files,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        if (files.Count == 0) return null;
        double total = 0;
        var got = 0;
        // Cap probe cost for huge films
        var toProbe = files.Count <= 40 ? files : files.Take(40).ToList();
        for (var i = 0; i < toProbe.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var d = await ProbeDurationSecondsAsync(toProbe[i], ct);
            if (d is > 0)
            {
                total += d.Value;
                got++;
            }
            if (i == 0 || (i + 1) % 5 == 0 || i + 1 == toProbe.Count)
                onProgress?.Invoke($"  probe {i + 1}/{toProbe.Count}…");
        }
        if (got == 0) return null;
        if (toProbe.Count < files.Count && got > 0)
        {
            // Scale average for unprobed tail
            var avg = total / got;
            total += avg * (files.Count - toProbe.Count);
        }
        return total;
    }

    private async Task<double?> ProbeDurationSecondsAsync(string mediaPath, CancellationToken ct)
    {
        try
        {
            var exe = ResolveFfmpegPath();
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                // ffmpeg prints metadata to stderr and exits non-zero without an output
                Arguments = $"-hide_banner -i \"{mediaPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var text = await stderrTask + "\n" + await stdoutTask;
            var m = DurationRe.Match(text);
            if (!m.Success) return null;
            return ParseHms(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Run ffmpeg with <c>-progress pipe:1</c>, stream lines to onProgress for SignalR.
    /// </summary>
    private async Task<(int Exit, string Log)> RunFfmpegAsync(
        string arguments,
        string workingDir,
        CancellationToken ct,
        Action<string>? onProgress = null,
        double? totalDurationSec = null,
        string label = "ffmpeg")
    {
        var exe = ResolveFfmpegPath();
        // -progress pipe:1 → key=value on stdout; -nostats quiet classic bar; keep errors on stderr
        var fullArgs = $"-hide_banner -loglevel info -progress pipe:1 -nostats {arguments}";
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = fullArgs,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {exe}");

        var log = new StringBuilder();
        var state = new ProgressState { TotalSec = totalDurationSec };
        var lastEmit = DateTime.UtcNow.AddSeconds(-1);

        void HandleLine(string? line, bool isStderr)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (log)
            {
                log.AppendLine(line);
                if (log.Length > 32_000)
                    log.Remove(0, log.Length - 24_000);
            }

            // Capture Duration from stderr if we didn't probe
            if (isStderr && state.TotalSec is null or <= 0)
            {
                var dm = DurationRe.Match(line);
                if (dm.Success)
                {
                    var d = ParseHms(dm.Groups[1].Value, dm.Groups[2].Value, dm.Groups[3].Value);
                    if (d is > 0)
                        state.TotalSec = (state.TotalSec ?? 0) + d.Value;
                }
            }

            var updated = ApplyProgressLine(line, state);
            if (!updated && !IsInterestingLogLine(line))
                return;

            // Throttle UI: at most ~4 updates/sec unless done
            var now = DateTime.UtcNow;
            var isEnd = line.Contains("progress=end", StringComparison.OrdinalIgnoreCase);
            if (!isEnd && (now - lastEmit).TotalMilliseconds < 250)
                return;
            lastEmit = now;

            var msg = FormatProgressMessage(label, state);
            if (!string.IsNullOrEmpty(msg))
                onProgress?.Invoke(msg);
            else if (IsInterestingLogLine(line))
                onProgress?.Invoke($"[{label}] {TrimOneLine(line, 160)}");
        }

        var stdoutTask = Task.Run(async () =>
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var line = await proc.StandardOutput.ReadLineAsync(ct);
                if (line is null) break;
                HandleLine(line, isStderr: false);
            }
        }, ct);

        var stderrTask = Task.Run(async () =>
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var line = await proc.StandardError.ReadLineAsync(ct);
                if (line is null) break;
                HandleLine(line, isStderr: true);
            }
        }, ct);

        await using var reg = ct.Register(() =>
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }
        });

        try
        {
            await proc.WaitForExitAsync(ct);
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }

        var fullLog = log.ToString().Trim();
        if (proc.ExitCode != 0)
            _log.LogWarning("ffmpeg exit {Code}: {Log}", proc.ExitCode, TrimLog(fullLog));
        else
            onProgress?.Invoke($"[{label}] complete");

        return (proc.ExitCode, fullLog);
    }

    private static bool ApplyProgressLine(string line, ProgressState state)
    {
        var updated = false;
        // -progress pipe:1 key=value
        if (line.StartsWith("out_time_ms=", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(line.AsSpan("out_time_ms=".Length), out var ms) && ms >= 0)
        {
            state.OutSec = ms / 1_000_000.0;
            updated = true;
        }
        else if (line.StartsWith("out_time=", StringComparison.OrdinalIgnoreCase))
        {
            var t = line["out_time=".Length..].Trim();
            // HH:MM:SS.microseconds
            var parts = t.Split(':');
            if (parts.Length == 3 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            {
                state.OutSec = h * 3600 + m * 60 + s;
                updated = true;
            }
        }
        else if (line.StartsWith("frame=", StringComparison.OrdinalIgnoreCase) &&
                 int.TryParse(line.AsSpan("frame=".Length), out var fr))
        {
            state.Frame = fr;
            updated = true;
        }
        else if (line.StartsWith("fps=", StringComparison.OrdinalIgnoreCase) &&
                 double.TryParse(line.AsSpan("fps=".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
        {
            state.Fps = fps;
            updated = true;
        }
        else if (line.StartsWith("speed=", StringComparison.OrdinalIgnoreCase))
        {
            state.Speed = line["speed=".Length..].Trim();
            updated = true;
        }
        else if (line.StartsWith("progress=", StringComparison.OrdinalIgnoreCase))
        {
            state.Phase = line["progress=".Length..].Trim();
            updated = true;
        }
        else
        {
            // Classic stats on one line: frame=  42 fps=... time=00:00:01.23 speed=1.2x
            var tm = TimeEqualsRe.Match(line);
            if (tm.Success)
            {
                state.OutSec = ParseHms(tm.Groups[1].Value, tm.Groups[2].Value, tm.Groups[3].Value);
                updated = true;
            }
            var fm = FrameRe.Match(line);
            if (fm.Success && int.TryParse(fm.Groups[1].Value, out var f2))
            {
                state.Frame = f2;
                updated = true;
            }
            var sm = SpeedRe.Match(line);
            if (sm.Success)
            {
                state.Speed = sm.Groups[1].Value;
                updated = true;
            }
        }
        return updated;
    }

    private static string FormatProgressMessage(string label, ProgressState state)
    {
        if (state.OutSec is null && state.Frame is null && string.IsNullOrEmpty(state.Speed))
            return "";

        var parts = new List<string> { $"[{label}]" };
        if (state.OutSec is double outSec)
        {
            var timePart = FormatHms(outSec);
            if (state.TotalSec is > 0.5)
            {
                var pct = Math.Clamp(outSec / state.TotalSec.Value * 100.0, 0, 100);
                parts.Add($"{timePart} / {FormatHms(state.TotalSec.Value)} ({pct:0.0}%)");
            }
            else
            {
                parts.Add($"time {timePart}");
            }
        }
        if (state.Frame is int fr)
            parts.Add($"frame {fr}");
        if (!string.IsNullOrEmpty(state.Speed))
            parts.Add($"speed {state.Speed}");
        if (state.Fps is > 0)
            parts.Add($"{state.Fps:0.#} fps");
        if (string.Equals(state.Phase, "end", StringComparison.OrdinalIgnoreCase))
            parts.Add("done");
        return string.Join(" · ", parts);
    }

    private static bool IsInterestingLogLine(string line)
    {
        if (line.Length < 4) return false;
        // Skip pure progress key=value noise for raw log (already summarized)
        if (line.Contains('=', StringComparison.Ordinal) &&
            (line.StartsWith("out_time", StringComparison.OrdinalIgnoreCase) ||
             line.StartsWith("frame=", StringComparison.OrdinalIgnoreCase) ||
             line.StartsWith("fps=", StringComparison.OrdinalIgnoreCase) ||
             line.StartsWith("speed=", StringComparison.OrdinalIgnoreCase) ||
             line.StartsWith("bitrate=", StringComparison.OrdinalIgnoreCase) ||
             line.StartsWith("total_size=", StringComparison.OrdinalIgnoreCase) ||
             line.StartsWith("dup_frames=", StringComparison.OrdinalIgnoreCase) ||
             line.StartsWith("drop_frames=", StringComparison.OrdinalIgnoreCase) ||
             line.StartsWith("stream_", StringComparison.OrdinalIgnoreCase) ||
             line.StartsWith("progress=", StringComparison.OrdinalIgnoreCase)))
            return false;

        return line.Contains("error", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Error", StringComparison.Ordinal)
               || line.Contains("warning", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Opening", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Output #", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Stream mapping", StringComparison.OrdinalIgnoreCase)
               || line.StartsWith("Input #", StringComparison.OrdinalIgnoreCase)
               || line.Contains("Duration:", StringComparison.OrdinalIgnoreCase);
    }

    private static double? ParseHms(string h, string m, string s)
    {
        if (!double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out var hh)) return null;
        if (!double.TryParse(m, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm)) return null;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var ss)) return null;
        return hh * 3600 + mm * 60 + ss;
    }

    private static string FormatHms(double sec)
    {
        if (sec < 0) sec = 0;
        var ts = TimeSpan.FromSeconds(sec);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        return $"{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 100:D1}";
    }

    private static string TrimOneLine(string s, int n)
    {
        s = s.Trim();
        return s.Length <= n ? s : s[..n] + "…";
    }

    private static string TrimLog(string log) =>
        log.Length <= 600 ? log : log[^600..];

    private sealed class ProgressState
    {
        public double? TotalSec { get; set; }
        public double? OutSec { get; set; }
        public int? Frame { get; set; }
        public double? Fps { get; set; }
        public string? Speed { get; set; }
        public string? Phase { get; set; }
    }
}
