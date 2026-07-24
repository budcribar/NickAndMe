using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
// Stopwatch used for ffmpeg telemetry wall time
using FilmStudio.Core.Options;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

/// <summary>
/// FFmpeg scene remux + WIP movie rebuild.
/// Resolves ffmpeg from: config path → NuGet-shipped Resources/ffmpeg.exe
/// (Soenneker.Libraries.FFmpeg) → PATH.
/// Streams stderr/stdout progress to <paramref name="onProgress"/> (SignalR job log).
/// </summary>
public sealed class FfmpegRemuxService : IFfmpegRemux
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
    private readonly EditLogService _editLogs;
    private readonly ProjectTelemetryService _telemetry;
    private readonly FilmStudioOptions _opts;
    private readonly ILogger<FfmpegRemuxService> _log;
    private string? _resolvedPath;
    private readonly object _resolveLock = new();

    // Last remux partition counts for telemetry (set in RemuxSceneAsync)
    private int? _lastIncludedCount;
    private int? _lastExcludedCount;
    private int? _lastRemuxScene;

    private readonly CreditsGeneratorService? _creditsGenerator;

    public FfmpegRemuxService(
        ProjectStore projects,
        EditLogService editLogs,
        ProjectTelemetryService telemetry,
        IOptions<FilmStudioOptions> opts,
        ILogger<FfmpegRemuxService> log,
        CreditsGeneratorService? creditsGenerator = null)
    {
        _projects = projects;
        _editLogs = editLogs;
        _telemetry = telemetry;
        _opts = opts.Value;
        _log = log;
        _creditsGenerator = creditsGenerator;
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
        CancellationToken ct = default,
        bool ignoreAssemblyGate = false)
    {
        EnsureAvailable(onProgress);

        var projectDir = _projects.GetProjectDir(projectId);
        var videoDir = Path.Combine(projectDir, "assets", "video");
        Directory.CreateDirectory(videoDir);

        var allClips = ListSceneClipFiles(projectId, videoDir, sceneNum);
        if (allClips.Count == 0)
            throw new InvalidOperationException(
                $"No clip files for scene {sceneNum} under {videoDir} " +
                $"(expected scene_{sceneNum:D2}_clip_XX.mp4 only — not .native sidecars)");

        // Assembly gate: drop human-fail / unresolved auto-fail clips from the composite.
        var included = new List<AssemblyClipEntry>();
        var excluded = new List<AssemblyClipEntry>();
        var clips = allClips;
        var gateOn = !ignoreAssemblyGate;
        _lastRemuxScene = sceneNum;
        _lastIncludedCount = null;
        _lastExcludedCount = null;
        using var _telScope = _telemetry.UseProject(projectId);
        if (gateOn)
        {
            var partition = PartitionSceneClipsForAssembly(projectId, sceneNum, allClips);
            included = partition.Included;
            excluded = partition.Excluded;
            _lastIncludedCount = included.Count;
            _lastExcludedCount = excluded.Count;
            clips = included
                .Select(e => e.FullPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Cast<string>()
                .ToList();

            if (clips.Count == 0)
            {
                var blocked = excluded.Select(e => $"{e.Key} ({e.Reason})");
                throw new InvalidOperationException(
                    $"Assembly gate: no eligible clips for S{sceneNum:D2}. " +
                    $"Blocked: {string.Join("; ", blocked)}. " +
                    "Pass with override reason, regen, or remux with ignoreAssemblyGate + reason.");
            }

            foreach (var ex in excluded)
            {
                onProgress?.Invoke(
                    $"Assembly gate: exclude {ex.Key} — {ex.Reason}");
            }

            if (excluded.Count > 0)
            {
                onProgress?.Invoke(
                    $"Assembly gate: remux S{sceneNum:D2} with {clips.Count}/{allClips.Count} " +
                    $"clip(s); excluded {excluded.Count}: " +
                    string.Join(", ", excluded.Select(e => e.Key)));
            }
        }
        else
        {
            // Gate ignored: everything in the cut, still record names for the manifest.
            foreach (var path in allClips)
            {
                included.Add(MakeClipEntry(path, sceneNum, "eligible (assembly gate ignored)"));
            }
        }

        onProgress?.Invoke($"Remux S{sceneNum:D2}: {clips.Count} clip(s) via {Path.GetFileName(FfmpegPath)}…");
        onProgress?.Invoke("Probing clip durations…");
        var totalSec = await EstimateTotalDurationAsync(clips, onProgress, ct);
        if (totalSec is > 0)
            onProgress?.Invoke($"Estimated total duration ~{FormatHms(totalSec.Value)}");

        var outPath = Path.Combine(videoDir, $"scene_{sceneNum:D2}.mp4");
        // Multi-clip: re-encode with short audio crossfades so cut joins don't leave dead air.
        // Single clip: stream-copy when possible.
        var (exit, log) = await ConcatVideosAsync(
            clips, outPath, projectDir, videoDir, totalSec, onProgress,
            label: $"S{sceneNum:D2}", ct).ConfigureAwait(false);

        if (exit != 0 || !File.Exists(outPath) || new FileInfo(outPath).Length < 1024)
            throw new InvalidOperationException($"FFmpeg remux failed for scene {sceneNum}: {TrimLog(log)}");

        await WriteSceneSourcesManifestAsync(
            outPath,
            sceneNum,
            included,
            excluded,
            assemblyGate: gateOn,
            totalDurationSeconds: totalSec).ConfigureAwait(false);
        if (totalSec is > 0)
            await MediaDurationProbe.WriteDurationSidecarAsync(outPath, totalSec.Value, ct)
                .ConfigureAwait(false);
        onProgress?.Invoke($"Remuxed → {Path.GetFileName(outPath)} ({clips.Count} clip(s))");
        return outPath;
    }

    public static string SceneSourcesManifestPath(string compositePath) =>
        compositePath + ".sources.json";

    /// <summary>Structured row for scene/WIP assembly manifests (PR4).</summary>
    public sealed class AssemblyClipEntry
    {
        public string Name { get; init; } = "";
        public int Scene { get; init; }
        public int Clip { get; init; }
        public string Reason { get; init; } = "";
        public string Key => $"S{Scene:D2}C{Clip:D2}";
        /// <summary>Absolute path when known (not serialized).</summary>
        public string? FullPath { get; init; }
        public long? Bytes { get; init; }
        public string? MtimeUtc { get; init; }
    }

    /// <summary>
    /// Build scene composite sources document: <c>clips</c> = included only;
    /// <c>included</c>/<c>excluded</c> explain the cut (PR4).
    /// </summary>
    public static Dictionary<string, object?> BuildSceneSourcesDocument(
        int sceneNum,
        IReadOnlyList<AssemblyClipEntry> included,
        IReadOnlyList<AssemblyClipEntry> excluded,
        bool assemblyGate,
        double? totalDurationSeconds = null)
    {
        var clipEntries = included.Select(ToClipFileEntry).ToList();
        return new Dictionary<string, object?>
        {
            ["builtAtUtc"] = DateTime.UtcNow.ToString("o"),
            ["scene"] = sceneNum,
            ["count"] = clipEntries.Count,
            ["clips"] = clipEntries,
            ["included"] = included.Select(ToIncludeExcludeEntry).ToList(),
            ["excluded"] = excluded.Select(ToIncludeExcludeEntry).ToList(),
            ["strict"] = true,
            ["assemblyGate"] = assemblyGate,
            ["totalDurationSeconds"] = totalDurationSeconds is > 0
                ? Math.Round(totalDurationSeconds.Value, 3)
                : null,
        };
    }

    private async Task WriteSceneSourcesManifestAsync(
        string compositePath,
        int sceneNum,
        IReadOnlyList<AssemblyClipEntry> included,
        IReadOnlyList<AssemblyClipEntry> excluded,
        bool assemblyGate,
        double? totalDurationSeconds = null,
        CancellationToken ct = default)
    {
        try
        {
            var doc = BuildSceneSourcesDocument(
                sceneNum, included, excluded, assemblyGate, totalDurationSeconds);
            await File.WriteAllTextAsync(
                SceneSourcesManifestPath(compositePath),
                JsonSerializer.Serialize(doc, JsonDefaults.Indented) + "\n",
                ct).ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal
        }
    }

    private static Dictionary<string, object?> ToClipFileEntry(AssemblyClipEntry e)
    {
        var d = new Dictionary<string, object?>
        {
            ["name"] = e.Name,
            ["scene"] = e.Scene,
            ["clip"] = e.Clip,
        };
        if (e.Bytes is long b) d["bytes"] = b;
        if (e.MtimeUtc is not null) d["mtimeUtc"] = e.MtimeUtc;
        return d;
    }

    private static Dictionary<string, object?> ToIncludeExcludeEntry(AssemblyClipEntry e) =>
        new()
        {
            ["name"] = e.Name,
            ["scene"] = e.Scene,
            ["clip"] = e.Clip,
            ["reason"] = e.Reason,
        };

    private AssemblyClipEntry MakeClipEntry(string path, int sceneNum, string reason)
    {
        TryParseSceneClip(path, out var sn, out var cn);
        if (sn <= 0) sn = sceneNum;
        var fi = new FileInfo(path);
        return new AssemblyClipEntry
        {
            Name = fi.Name,
            Scene = sn,
            Clip = cn,
            Reason = reason,
            FullPath = path,
            Bytes = fi.Exists ? fi.Length : null,
            MtimeUtc = fi.Exists ? fi.LastWriteTimeUtc.ToString("o") : null,
        };
    }

    /// <summary>Split on-disk scene clips into assembly-eligible vs blocked (with reasons).</summary>
    public (List<AssemblyClipEntry> Included, List<AssemblyClipEntry> Excluded)
        PartitionSceneClipsForAssembly(
            string projectId,
            int sceneNum,
            IReadOnlyList<string>? allClips = null)
    {
        var projectDir = _projects.GetProjectDir(projectId);
        var videoDir = Path.Combine(projectDir, "assets", "video");
        allClips ??= ListSceneClipFiles(projectId, videoDir, sceneNum);

        var included = new List<AssemblyClipEntry>();
        var excluded = new List<AssemblyClipEntry>();
        foreach (var path in allClips)
        {
            if (!TryParseSceneClip(path, out var sn, out var cn) || sn != sceneNum)
            {
                included.Add(MakeClipEntry(path, sceneNum, "eligible"));
                continue;
            }

            if (_editLogs.IsClipEligibleForAssembly(projectId, sn, cn, out var reason))
                included.Add(MakeClipEntry(path, sn, "eligible"));
            else
                excluded.Add(MakeClipEntry(path, sn, reason));
        }

        return (included, excluded);
    }

    /// <summary>
    /// True if composite is missing, eligible clips are newer, has no sources manifest,
    /// or manifest included set does not match current assembly-eligible clips (PR4 gate-aware).
    /// </summary>
    public bool IsSceneCompositeStale(string projectId, int sceneNum)
    {
        var projectDir = _projects.GetProjectDir(projectId);
        var videoDir = Path.Combine(projectDir, "assets", "video");
        var allOnDisk = ListSceneClipFiles(projectId, videoDir, sceneNum);
        if (allOnDisk.Count == 0)
            return false; // nothing to remux

        // Stale vs the cut that remux would produce (eligible only), not every file on disk.
        var (included, _) = PartitionSceneClipsForAssembly(projectId, sceneNum, allOnDisk);
        var expected = included
            .Where(e => e.FullPath is not null)
            .Select(e => e.FullPath!)
            .ToList();
        if (expected.Count == 0)
            return true; // all blocked — composite cannot be current

        var composite = _projects.ResolveCompositePath(projectId, sceneNum);
        if (composite is null || !File.Exists(composite))
            return true;

        var maxClip = expected.Max(f => new FileInfo(f).LastWriteTimeUtc);
        if (maxClip > new FileInfo(composite).LastWriteTimeUtc.AddSeconds(1))
            return true;

        // Composites without a sources manifest are treated as dirty
        var manifestPath = SceneSourcesManifestPath(composite);
        // Also check path next to remux output name
        var remuxOut = Path.Combine(videoDir, $"scene_{sceneNum:D2}.mp4");
        if (!File.Exists(manifestPath) && File.Exists(remuxOut))
            manifestPath = SceneSourcesManifestPath(remuxOut);

        if (!File.Exists(manifestPath))
            return true;

        try
        {
            // Small manifest; sync OK for staleness checks (not on Kestrel request path)
            using var doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
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

            // Per-file size/mtime drift for included clips only
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
        using var _telScope = _telemetry.UseProject(projectId);
        _lastRemuxScene = null;
        _lastIncludedCount = null;
        _lastExcludedCount = null;

        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var cfg = await LoadConfigAsync(projectDir, ct).ConfigureAwait(false);
        var wipRel = cfg.TryGetValue("wip_movie_path", out var w)
            ? w?.ToString() ?? "assets/movie_wip.mp4"
            : "assets/movie_wip.mp4";
        var wipPath = Path.IsPathRooted(wipRel)
            ? wipRel
            : Path.Combine(projectDir, wipRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(wipPath)!);

        var videoDir = Path.Combine(projectDir, "assets", "video");

        if (_creditsGenerator is not null && _opts.Credits.AutoAppendCredits)
        {
            if (_creditsGenerator.AreAllScenesComplete(projectId))
            {
                var creditsClip = await _creditsGenerator.EnsureCreditsClipAsync(projectId, FfmpegPath, onProgress, ct).ConfigureAwait(false);
            }
        }

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

        var (exit, log) = await ConcatVideosAsync(
            sceneFiles, wipPath, projectDir, videoDir, totalSec, onProgress,
            label: "WIP", ct).ConfigureAwait(false);

        if (exit != 0 || !File.Exists(wipPath))
            throw new InvalidOperationException($"FFmpeg WIP rebuild failed: {TrimLog(log)}");

        await WriteWipSourcesManifestAsync(projectId, wipPath, sceneFiles).ConfigureAwait(false);
        onProgress?.Invoke($"WIP → {wipPath} ({sceneFiles.Count} source(s))");
        return wipPath;
    }

    /// <summary>
    /// Concat clips/scenes. Prefer short audio crossfade on multi-input joins (reduces dead air at cuts).
    /// Falls back to concat demuxer copy, then plain re-encode.
    /// </summary>
    private async Task<(int Exit, string Log)> ConcatVideosAsync(
        IReadOnlyList<string> inputs,
        string outPath,
        string projectDir,
        string workDir,
        double? totalSec,
        Action<string>? onProgress,
        string label,
        CancellationToken ct)
    {
        if (inputs.Count == 0)
            return (1, "no inputs");

        if (inputs.Count == 1)
        {
            var listFile = Path.Combine(workDir, $"_concat_{label.Replace(' ', '_')}.txt");
            var escaped = inputs[0].Replace("\\", "/").Replace("'", "'\\''");
            await File.WriteAllTextAsync(listFile, $"file '{escaped}'\n", ct);
            var args = $"-y -f concat -safe 0 -i \"{listFile}\" -c copy \"{outPath}\"";
            var r = await RunFfmpegAsync(args, projectDir, ct, onProgress, totalSec, label: $"{label} copy");
            if (r.Exit != 0)
            {
                args =
                    $"-y -f concat -safe 0 -i \"{listFile}\" -c:v libx264 -preset veryfast -crf 20 " +
                    $"-c:a aac -b:a 160k \"{outPath}\"";
                r = await RunFfmpegAsync(args, projectDir, ct, onProgress, totalSec, label: $"{label} encode");
            }
            try { File.Delete(listFile); } catch { /* ignore */ }
            return (r.Exit, r.Log);
        }

        // Multi-input: Try fast stream copy concat demuxer first (instantaneous join for matching stream formats)
        onProgress?.Invoke($"{label}: concat {inputs.Count} clip(s) via fast stream copy demuxer…");
        var multiListFile = Path.Combine(workDir, $"_concat_{label.Replace(' ', '_')}.txt");
        var sb = new StringBuilder();
        foreach (var c in inputs)
        {
            var esc = c.Replace("\\", "/").Replace("'", "'\\''");
            sb.AppendLine($"file '{esc}'");
        }
        await File.WriteAllTextAsync(multiListFile, sb.ToString(), ct).ConfigureAwait(false);

        var copyArgs = $"-y -f concat -safe 0 -i \"{multiListFile}\" -c copy -movflags +faststart \"{outPath}\"";
        var copyRun = await RunFfmpegAsync(copyArgs, projectDir, ct, onProgress, totalSec, label: $"{label} copy").ConfigureAwait(false);

        if (copyRun.Exit == 0 && File.Exists(outPath) && new FileInfo(outPath).Length >= 1024)
        {
            try { File.Delete(multiListFile); } catch { /* ignore */ }
            return (copyRun.Exit, copyRun.Log);
        }

        // Fallback: synchronized hard-cut concat re-encode via filter_complex
        onProgress?.Invoke($"{label}: stream copy unavailable — encoding {inputs.Count} clip(s) with filter_complex…");
        var fc = BuildConcatFilterComplex(inputs.Count);
        var inputArgs = string.Join(" ", inputs.Select(p => $"-i \"{p}\""));
        var concatArgs =
            $"-y {inputArgs} -filter_complex \"{fc}\" -map \"[v]\" -map \"[a]\" " +
            $"-c:v libx264 -preset veryfast -crf 20 -c:a aac -b:a 160k -movflags +faststart \"{outPath}\"";
        var concatRun = await RunFfmpegAsync(
            concatArgs, projectDir, ct, onProgress, totalSec, label: $"{label} concat").ConfigureAwait(false);

        try { File.Delete(multiListFile); } catch { /* ignore */ }
        return (concatRun.Exit, concatRun.Log);
    }

    /// <summary>
    /// Build filter_complex: synchronized hard-cut concat for N video and audio streams.
    /// Preserves 1:1 lip sync across clip boundaries without audio drift.
    /// </summary>
    public static string BuildConcatFilterComplex(int n)
    {
        if (n < 2) throw new ArgumentOutOfRangeException(nameof(n));
        var inputs = string.Join("", Enumerable.Range(0, n).Select(i => $"[{i}:v][{i}:a]"));
        return $"{inputs}concat=n={n}:v=1:a=1[v][a]";
    }

    /// <summary>
    /// Ordered inputs for WIP concat: scene composites first, else exact clip files.
    /// Shared by rebuild and freshness checks (add/delete detection).
    /// </summary>
    public static List<string> ListWipSourceFiles(string videoDir)
    {
        if (!Directory.Exists(videoDir))
            return new List<string>();

        var sceneFiles = new DirectoryInfo(videoDir).GetFiles("scene_*.mp4")
            .Where(f => RegexSceneOnly(f.Name))
            .Where(f =>
            {
                try { return f.Length >= 1024; }
                catch { return false; }
            })
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.FullName)
            .ToList();

        if (sceneFiles.Count > 0)
            return sceneFiles;

        return new DirectoryInfo(videoDir).GetFiles("scene_*_clip_*.mp4")
            .Where(f => IsExactClipFileName(f.Name))
            .Where(f =>
            {
                try { return f.Length >= 1024; }
                catch { return false; }
            })
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.FullName)
            .ToList();
    }

    /// <summary>Sidecar next to WIP listing concat sources (for add/delete/outdate detection).</summary>
    public static string WipSourcesManifestPath(string wipPath) =>
        wipPath + ".sources.json";

    private async Task WriteWipSourcesManifestAsync(
        string projectId,
        string wipPath,
        IReadOnlyList<string> sourceFiles,
        CancellationToken ct = default)
    {
        try
        {
            var entries = sourceFiles.Select(f =>
            {
                var fi = new FileInfo(f);
                var name = fi.Name;
                int? scene = null;
                if (name is not null && RegexSceneOnly(name) &&
                    int.TryParse(name.AsSpan(6, 2), out var sn))
                    scene = sn;
                return new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["scene"] = scene,
                    ["bytes"] = fi.Length,
                    ["mtimeUtc"] = fi.LastWriteTimeUtc.ToString("o"),
                    ["kind"] = scene is not null ? "scene_composite" : "source",
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
            var bpPath = await _projects.FindBlueprintPathAsync(projectId, ct).ConfigureAwait(false);
            if (bpPath is not null && File.Exists(bpPath))
                bpMtime = new FileInfo(bpPath).LastWriteTimeUtc.ToString("o");

            // Note: WIP stitches scene composites; clip-level assembly gate already applied at remux.
            var doc = new Dictionary<string, object?>
            {
                ["builtAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["count"] = entries.Count,
                ["sources"] = entries,
                ["included"] = entries, // WIP inputs are post-gate scene composites
                ["excluded"] = Array.Empty<object>(),
                ["sceneNumbers"] = sceneNumbers,
                ["blueprintMtimeUtc"] = bpMtime,
                ["assemblyGate"] = true,
                ["note"] =
                    "WIP concatenates scene composites. Clip include/exclude decisions live on " +
                    "each scene_XX.mp4.sources.json (assembly gate applied at remux).",
            };
            var path = WipSourcesManifestPath(wipPath);
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(doc, JsonDefaults.Indented) + "\n",
                ct).ConfigureAwait(false);
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

    public static bool IsExactClipFileName(string? fileName) =>
        !string.IsNullOrEmpty(fileName) && ExactClipNameRe.IsMatch(fileName);

    private static bool TryParseSceneClip(string path, out int scene, out int clip)
    {
        scene = 0;
        clip = 0;
        var name = Path.GetFileName(path);
        var m = ExactClipNameRe.Match(name ?? "");
        if (!m.Success) return false;
        return int.TryParse(m.Groups[1].Value, out scene) &&
               int.TryParse(m.Groups[2].Value, out clip);
    }

    /// <summary>
    /// Clips for scene remux: only exact <c>scene_SS_clip_CC.mp4</c> names (≥1KB).
    /// When Stage 2 blueprint lists <c>veo_clips</c>, only those clip numbers are included
    /// (drops orphans like an old clip_04 and never picks .native sidecars).
    /// </summary>
    private List<string> ListSceneClipFiles(string projectId, string videoDir, int sceneNum)
    {
        if (!Directory.Exists(videoDir)) return new();

        var byClip = new SortedDictionary<int, string>();
        foreach (var fi in new DirectoryInfo(videoDir).EnumerateFiles($"scene_{sceneNum:D2}_clip_*.mp4"))
        {
            var name = fi.Name;
            var m = ExactClipNameRe.Match(name);
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, out var sn) || sn != sceneNum) continue;
            if (!int.TryParse(m.Groups[2].Value, out var cn) || cn <= 0) continue;
            try
            {
                if (fi.Length < 1024) continue;
            }
            catch { continue; }

            byClip[cn] = fi.FullName;
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
            // True-sync path (no GetAwaiter): resolve blueprint under project dir.
            var dir = _projects.GetProjectDir(projectId);
            string? bpPath = null;
            var configPath = Path.Combine(dir, "pipeline_config.json");
            var preferred = "blueprint.clips.grok.json";
            if (File.Exists(configPath))
            {
                try
                {
                    using var cfg = JsonDocument.Parse(File.ReadAllText(configPath));
                    if (cfg.RootElement.TryGetProperty("blueprint_file", out var bf) &&
                        bf.GetString() is { Length: > 0 } n)
                        preferred = n;
                }
                catch { /* ignore */ }
            }

            foreach (var candidate in new[]
                     {
                         preferred,
                         "blueprint.clips.grok.json",
                     })
            {
                var full = Path.Combine(dir, candidate);
                if (File.Exists(full))
                {
                    bpPath = full;
                    break;
                }
            }

            if (bpPath is null) return null;
            using var bp = JsonDocument.Parse(File.ReadAllBytes(bpPath));
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

    private static async Task<Dictionary<string, object?>> LoadConfigAsync(
        string projectDir,
        CancellationToken ct = default)
    {
        var path = Path.Combine(projectDir, "pipeline_config.json");
        if (!File.Exists(path)) return new();
        try
        {
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return GrokChatClient.ParseJsonObject(text);
        }
        catch { return new(); }
    }

    /// <summary>
    /// Sum per-file durations via <c>ffmpeg -i</c> Duration lines (no ffprobe required).
    /// </summary>
    private async Task<double?> EstimateTotalDurationAsync(
        List<string> files,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        if (files.Count == 0) return null;
        double total = 0;
        var got = 0;
        // Cap probe cost for huge films (CA1859: List avoids interface dispatch)
        var toProbeCount = Math.Min(files.Count, 40);
        for (var i = 0; i < toProbeCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var d = await ProbeDurationSecondsAsync(files[i], ct);
            if (d is > 0)
            {
                total += d.Value;
                got++;
            }
            if (i == 0 || (i + 1) % 5 == 0 || i + 1 == toProbeCount)
                onProgress?.Invoke($"  probe {i + 1}/{toProbeCount}…");
        }
        if (got == 0) return null;
        if (toProbeCount < files.Count && got > 0)
        {
            // Scale average for unprobed tail
            var avg = total / got;
            total += avg * (files.Count - toProbeCount);
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

        var sw = Stopwatch.StartNew();
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

        try
        {
            // Prefer output path after -y ... last token that looks like a media file
            string? output = null;
            var parts = arguments.Split('"')
                .SelectMany((s, i) => i % 2 == 1 ? new[] { s } : s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .ToList();
            for (var i = parts.Count - 1; i >= 0; i--)
            {
                var p = parts[i];
                if (p.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    output = p;
                    break;
                }
            }

            var rec = ProjectTelemetryService.CondenseFfmpegOp(
                op: label,
                args: fullArgs.Length > 4000 ? fullArgs[..4000] + "…" : fullArgs,
                inputs: null,
                output: output,
                exitCode: proc.ExitCode,
                timedOut: false,
                wallMs: sw.ElapsedMilliseconds,
                rawLog: fullLog,
                ffmpegExe: exe,
                scene: _lastRemuxScene,
                includedCount: _lastIncludedCount,
                excludedCount: _lastExcludedCount);
            _telemetry.LogFfmpeg(rec);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "ffmpeg telemetry skip");
        }

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
