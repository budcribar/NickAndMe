using System.Collections.Concurrent;
using FilmStudio.Core.Options;
using FilmStudio.Engine;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Fakes;

/// <summary>
/// Fake video client: delay + copy fixture MP4 (merge-realistic size when available).
/// </summary>
public sealed class FakeGrokVideoClient : IGrokVideoClient
{
    private readonly FilmStudioOptions _opts;
    private readonly ILogger<FakeGrokVideoClient> _log;
    private readonly ConcurrentDictionary<string, string> _pending = new();
    private int _submitCount;

    public FakeGrokVideoClient(IOptions<FilmStudioOptions> opts, ILogger<FakeGrokVideoClient> log)
    {
        _opts = opts.Value;
        _log = log;
    }

    public bool IsConfigured => true;

    public async Task<string> SubmitGenerationAsync(
        string prompt,
        int durationSeconds,
        string resolution,
        string model,
        CancellationToken ct,
        IReadOnlyList<string>? referenceImagePaths = null,
        string? startFrameImagePath = null,
        string? continueFromVideoPath = null)
    {
        var n = Interlocked.Increment(ref _submitCount);
        var fakes = _opts.Fakes ?? new FakesOptions();
        if (fakes.RateLimitEveryN > 0 && n % fakes.RateLimitEveryN == 0)
            throw new InvalidOperationException("Fake 429: rate limit (RateLimitEveryN)");

        await Task.Delay(Math.Max(0, fakes.VideoDelayMs), ct);

        if (fakes.FailRate > 0 && Random.Shared.NextDouble() < fakes.FailRate)
            throw new InvalidOperationException("Fake video generation failed (FailRate)");

        var id = "fake-" + Guid.NewGuid().ToString("N")[..12];
        var fixture = ResolveFixturePath(fakes.VideoMode, durationSeconds);
        // Mark extensions so Download can optionally chain (caller trims new portion)
        if (!string.IsNullOrWhiteSpace(continueFromVideoPath) && File.Exists(continueFromVideoPath))
            _pending[id] = "extend:" + continueFromVideoPath + "|" + fixture;
        else
            _pending[id] = fixture;
        _log.LogInformation(
            "Fake video submit {Id} duration={Dur}s fixture={Fixture} refs={Refs} startFrame={Start} continue={Cont} promptLen={Len}",
            id, durationSeconds, Path.GetFileName(fixture),
            referenceImagePaths?.Count ?? 0,
            startFrameImagePath is null ? "-" : Path.GetFileName(startFrameImagePath),
            continueFromVideoPath is null ? "-" : Path.GetFileName(continueFromVideoPath),
            prompt?.Length ?? 0);
        return id;
    }

    public async Task<string> PollForVideoUrlAsync(
        string requestId,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        onProgress?.Invoke("status=pending (fake)");
        await Task.Delay(50, ct);
        onProgress?.Invoke("status=done (fake)");
        if (!_pending.TryGetValue(requestId, out var fixture))
            throw new InvalidOperationException($"Unknown fake request_id {requestId}");
        // Use file:// so Download can detect local fixture
        return "fake-fixture:" + fixture;
    }

    public async Task DownloadToFileAsync(string url, string destPath, CancellationToken ct)
    {
        await Task.Yield();
        string fixture;
        if (url.StartsWith("fake-fixture:", StringComparison.OrdinalIgnoreCase))
            fixture = url["fake-fixture:".Length..];
        else if (_pending.Values.FirstOrDefault() is { } f)
            fixture = f;
        else
            fixture = ResolveFixturePath(_opts.Fakes?.VideoMode, 10);

        // extend:prevPath|fixturePath → just use fixture as the "new portion"
        if (fixture.StartsWith("extend:", StringComparison.OrdinalIgnoreCase))
        {
            var pipe = fixture.IndexOf('|');
            fixture = pipe > 0 ? fixture[(pipe + 1)..] : fixture["extend:".Length..];
        }

        if (!File.Exists(fixture))
            throw new FileNotFoundException(
                "Fake video fixture missing. Run scripts/generate-fake-fixtures.ps1 or copy an MP4 to FilmStudio.Fakes/Fixtures/clip_merge_10s.mp4",
                fixture);

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        File.Copy(fixture, destPath, overwrite: true);

        // Sidecar for MediaDurationProbe without re-probing
        try
        {
            var mode = _opts.Fakes?.VideoMode ?? "MergeRealistic";
            var seconds = mode.Equals("LoadLight", StringComparison.OrdinalIgnoreCase) ? 1.0 : 10.0;
            await MediaDurationProbe.WriteDurationSidecarAsync(destPath, seconds, ct);
        }
        catch { /* ignore */ }

        _log.LogInformation("Fake download {Bytes} bytes → {Path}", new FileInfo(destPath).Length, destPath);
    }

    public static string ResolveFixturePath(string? videoMode, int durationSeconds)
    {
        var baseDir = AppContext.BaseDirectory;
        var fixtures = Path.Combine(baseDir, "Fixtures");
        var light = Path.Combine(fixtures, "clip_tiny_1s.mp4");
        var merge = Path.Combine(fixtures, "clip_merge_10s.mp4");

        if (string.Equals(videoMode, "LoadLight", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(light)) return light;
            if (File.Exists(merge)) return merge;
        }
        else
        {
            if (File.Exists(merge)) return merge;
            if (File.Exists(light)) return light;
        }

        // Fallback: any mp4 under Fixtures or a sample from workspace Buster
        if (Directory.Exists(fixtures))
        {
            var any = Directory.GetFiles(fixtures, "*.mp4").FirstOrDefault();
            if (any is not null) return any;
        }

        // Dev convenience: use a real short Buster clip if present
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "projects", "Buster", "assets", "video", "scene_04_clip_03.mp4")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "projects", "Buster", "assets", "video", "scene_04_clip_03.mp4")),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        return merge; // path for error message
    }
}
