using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Append-only project telemetry under <c>projects/{id}/telemetry/</c>:
/// <list type="bullet">
/// <item><c>api_calls.jsonl</c> — live model/API calls (full prompts)</item>
/// <item><c>ffmpeg.jsonl</c> — condensed remux/trim/sample ops</item>
/// </list>
/// Project id from <see cref="UseProject"/> scope, else <see cref="ProjectStore.ActiveProjectId"/>.
/// </summary>
public sealed class ProjectTelemetryService
{
    private static readonly AsyncLocal<string?> ScopedProjectId = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static readonly Regex ProgressFluff = new(
        @"^(frame=|fps=|bitrate=|total_size=|out_time|dup=|drop=|speed=|progress=)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ProjectStore _projects;
    private readonly ILogger<ProjectTelemetryService> _log;
    private readonly ConcurrentDictionary<string, object> _fileLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public ProjectTelemetryService(ProjectStore projects, ILogger<ProjectTelemetryService> log)
    {
        _projects = projects;
        _log = log;
    }

    /// <summary>Bind telemetry writes to a project for the current async flow.</summary>
    public IDisposable UseProject(string projectId)
    {
        var prev = ScopedProjectId.Value;
        ScopedProjectId.Value = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();
        return new ScopePop(() => ScopedProjectId.Value = prev);
    }

    public string? CurrentProjectId =>
        !string.IsNullOrWhiteSpace(ScopedProjectId.Value)
            ? ScopedProjectId.Value
            : string.IsNullOrWhiteSpace(_projects.ActiveProjectId)
                ? null
                : _projects.ActiveProjectId;

    public string TelemetryDir(string projectId) =>
        Path.Combine(_projects.GetProjectDir(projectId), "telemetry");

    public string ApiCallsPath(string projectId) =>
        Path.Combine(TelemetryDir(projectId), "api_calls.jsonl");

    public string FfmpegPath(string projectId) =>
        Path.Combine(TelemetryDir(projectId), "ffmpeg.jsonl");

    public void LogApiCall(ApiCallTelemetry rec)
    {
        var projectId = rec.ProjectId ?? CurrentProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            _log.LogDebug("api_calls skip — no project id (kind={Kind})", rec.Kind);
            return;
        }

        rec.ProjectId = projectId;
        if (rec.Ts is null)
            rec.Ts = DateTimeOffset.UtcNow;

        try
        {
            AppendJsonl(ApiCallsPath(projectId), rec);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "api_calls append failed for {ProjectId}", projectId);
        }
    }

    public void LogFfmpeg(FfmpegOpTelemetry rec)
    {
        var projectId = rec.ProjectId ?? CurrentProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            _log.LogDebug("ffmpeg.jsonl skip — no project id (op={Op})", rec.Op);
            return;
        }

        rec.ProjectId = projectId;
        if (rec.Ts is null)
            rec.Ts = DateTimeOffset.UtcNow;

        try
        {
            AppendJsonl(FfmpegPath(projectId), rec);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ffmpeg.jsonl append failed for {ProjectId}", projectId);
        }
    }

    /// <summary>
    /// Build condensed ffmpeg telemetry from raw stderr/stdout + args.
    /// Drops frame/fps spam; keeps interesting lines + sparse progress samples.
    /// </summary>
    public static FfmpegOpTelemetry CondenseFfmpegOp(
        string op,
        string args,
        IReadOnlyList<string>? inputs,
        string? output,
        int exitCode,
        bool timedOut,
        long wallMs,
        string? rawLog,
        string? ffmpegExe = null,
        int? scene = null,
        int? includedCount = null,
        int? excludedCount = null,
        string? fallback = null,
        string? projectId = null)
    {
        var interesting = new List<string>();
        var progressSamples = new List<string>();
        string? lastTime = null;
        string? lastSpeed = null;

        if (!string.IsNullOrEmpty(rawLog))
        {
            foreach (var line in rawLog.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;

                if (t.StartsWith("out_time=", StringComparison.OrdinalIgnoreCase) ||
                    t.Contains("time=", StringComparison.OrdinalIgnoreCase))
                {
                    lastTime = t.Length > 120 ? t[..120] : t;
                    // Sample sparsely: keep first, then every ~8th-ish by counting
                    if (progressSamples.Count == 0 ||
                        progressSamples.Count < 8 && progressSamples.Count % 2 == 0)
                    {
                        if (progressSamples.Count == 0 ||
                            !string.Equals(progressSamples[^1], lastTime, StringComparison.Ordinal))
                            progressSamples.Add(lastTime);
                    }
                    continue;
                }

                if (t.StartsWith("speed=", StringComparison.OrdinalIgnoreCase))
                {
                    lastSpeed = t;
                    continue;
                }

                if (ProgressFluff.IsMatch(t) && !IsInterestingLogLine(t))
                    continue;

                if (IsInterestingLogLine(t))
                {
                    interesting.Add(t.Length > 300 ? t[..300] : t);
                    if (interesting.Count >= 40) break;
                }
            }
        }

        // Cap progress samples
        if (progressSamples.Count > 8)
        {
            progressSamples = new List<string>
            {
                progressSamples[0],
                progressSamples[progressSamples.Count / 4],
                progressSamples[progressSamples.Count / 2],
                progressSamples[progressSamples.Count * 3 / 4],
                progressSamples[^1],
            };
        }

        if (lastTime is not null &&
            (progressSamples.Count == 0 || progressSamples[^1] != lastTime))
            progressSamples.Add(lastTime);

        return new FfmpegOpTelemetry
        {
            ProjectId = projectId,
            Op = op,
            Args = args,
            Inputs = inputs?.ToList(),
            Output = output,
            ExitCode = exitCode,
            TimedOut = timedOut,
            WallMs = wallMs,
            FfmpegPath = ffmpegExe,
            Scene = scene,
            IncludedCount = includedCount,
            ExcludedCount = excludedCount,
            Fallback = fallback,
            Progress = progressSamples.Count > 0 ? progressSamples : null,
            StderrInteresting = interesting.Count > 0 ? interesting : null,
            Stats = lastTime is not null || lastSpeed is not null
                ? new Dictionary<string, object?>
                {
                    ["lastTime"] = lastTime,
                    ["lastSpeed"] = lastSpeed,
                }
                : null,
        };
    }

    public static bool IsInterestingLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("warning", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("failed", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("Invalid", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("No such", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("Conversion failed", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("not found", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("Error opening", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void AppendJsonl(string path, object rec)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var line = JsonSerializer.Serialize(rec, JsonOpts) + "\n";
        var gate = _fileLocks.GetOrAdd(path, _ => new object());
        lock (gate)
        {
            File.AppendAllText(path, line);
        }
    }

    private sealed class ScopePop : IDisposable
    {
        private readonly Action _onDispose;
        private int _done;
        public ScopePop(Action onDispose) => _onDispose = onDispose;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                _onDispose();
        }
    }
}

/// <summary>One live API call (full prompts on disk for project review).</summary>
public sealed class ApiCallTelemetry
{
    public DateTimeOffset? Ts { get; set; }
    public string? ProjectId { get; set; }
    /// <summary>video | video_extend | video_poll | image | image_edit | vision | chat | tts | …</summary>
    public string Kind { get; set; } = "";
    public string? Endpoint { get; set; }
    public string? Model { get; set; }
    public int? HttpStatus { get; set; }
    public string? RequestId { get; set; }
    public string? Error { get; set; }
    public long? DurationMs { get; set; }
    public int? Scene { get; set; }
    public int? Clip { get; set; }
    public string? CharKey { get; set; }
    /// <summary>
    /// Call purpose tag. Chat: <c>book_to_fountain</c>, <c>cast_from_screenplay</c>, …
    /// (see <c>ChatCallModes</c>). Video: <c>fresh</c> | <c>video-extend</c> | <c>reseed</c> | …
    /// </summary>
    public string? Mode { get; set; }
    public string? Prompt { get; set; }
    public string? SystemPrompt { get; set; }
    public string? UserPrompt { get; set; }
    public string? ResponsePreview { get; set; }
    public List<string>? ReferenceImagePaths { get; set; }
    public bool? RefsAttached { get; set; }
    public string? Resolution { get; set; }
    public double? DurationSec { get; set; }
    public int? Attempt { get; set; }
    public string? JobId { get; set; }
    public bool Fakes { get; set; }
    public int? ImageCount { get; set; }
    public int? PromptChars { get; set; }
    public int? ResponseChars { get; set; }
    public bool Ok { get; set; } = true;
}

/// <summary>One condensed ffmpeg operation.</summary>
public sealed class FfmpegOpTelemetry
{
    public DateTimeOffset? Ts { get; set; }
    public string? ProjectId { get; set; }
    public string Op { get; set; } = "";
    public string? Args { get; set; }
    public List<string>? Inputs { get; set; }
    public string? Output { get; set; }
    public int ExitCode { get; set; }
    public bool TimedOut { get; set; }
    public long WallMs { get; set; }
    public string? FfmpegPath { get; set; }
    public int? Scene { get; set; }
    public int? IncludedCount { get; set; }
    public int? ExcludedCount { get; set; }
    public string? Fallback { get; set; }
    public List<string>? Progress { get; set; }
    public List<string>? StderrInteresting { get; set; }
    public Dictionary<string, object?>? Stats { get; set; }
    public bool Ok => !TimedOut && ExitCode == 0;
}
