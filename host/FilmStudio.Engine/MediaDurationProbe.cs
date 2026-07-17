using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FilmStudio.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

/// <summary>
/// Probe media duration via ffmpeg -i (cached by path + mtime + size).
/// Also reads totalDurationSeconds from *.sources.json when present.
/// </summary>
public sealed class MediaDurationProbe
{
    private static readonly Regex DurationRe = new(
        @"Duration:\s*(\d{1,2}):(\d{2}):(\d{2}(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, (long Ticks, long Length, double Sec)> _cache = new();
    private readonly FilmStudioOptions _opts;
    private readonly ILogger<MediaDurationProbe> _log;
    private string? _ffmpeg;
    private readonly object _ffmpegLock = new();

    public MediaDurationProbe(IOptions<FilmStudioOptions> opts, ILogger<MediaDurationProbe> log)
    {
        _opts = opts.Value;
        _log = log;
    }

    /// <summary>Duration in seconds, or null if unknown / missing file.</summary>
    public double? GetDurationSeconds(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
            return null;

        try
        {
            var fi = new FileInfo(mediaPath);
            if (fi.Length < 1024) return null;

            var key = fi.FullName;
            if (_cache.TryGetValue(key, out var hit) &&
                hit.Ticks == fi.LastWriteTimeUtc.Ticks &&
                hit.Length == fi.Length)
                return hit.Sec;

            // Prefer duration written at remux time
            var fromManifest = TryReadManifestDuration(mediaPath);
            if (fromManifest is > 0)
            {
                _cache[key] = (fi.LastWriteTimeUtc.Ticks, fi.Length, fromManifest.Value);
                return fromManifest;
            }

            var probed = ProbeWithFfmpeg(fi.FullName);
            if (probed is > 0)
            {
                _cache[key] = (fi.LastWriteTimeUtc.Ticks, fi.Length, probed.Value);
                return probed;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Duration probe failed for {Path}", mediaPath);
        }

        return null;
    }

    /// <summary>
    /// Actual scene length: composite if present, else sum of exact on-disk clips for that scene.
    /// </summary>
    public double? GetSceneActualDurationSeconds(
        string? compositePath,
        IEnumerable<string> exactClipPaths)
    {
        if (!string.IsNullOrWhiteSpace(compositePath) && File.Exists(compositePath))
        {
            var d = GetDurationSeconds(compositePath);
            if (d is > 0) return d;
        }

        double sum = 0;
        var any = false;
        foreach (var clip in exactClipPaths)
        {
            var d = GetDurationSeconds(clip);
            if (d is > 0)
            {
                sum += d.Value;
                any = true;
            }
        }

        return any ? sum : null;
    }

    public static void WriteDurationSidecar(string mediaPath, double durationSeconds)
    {
        try
        {
            if (durationSeconds <= 0 || string.IsNullOrWhiteSpace(mediaPath)) return;
            var path = mediaPath + ".duration.json";
            var doc = new Dictionary<string, object?>
            {
                ["seconds"] = Math.Round(durationSeconds, 3),
                ["updatedAtUtc"] = DateTime.UtcNow.ToString("o"),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(doc) + "\n");
        }
        catch { /* ignore */ }
    }

    private static double? TryReadManifestDuration(string mediaPath)
    {
        // scene_01.mp4.sources.json may include totalDurationSeconds
        foreach (var candidate in new[]
                 {
                     mediaPath + ".sources.json",
                     mediaPath + ".duration.json",
                 })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(candidate));
                var root = doc.RootElement;
                if (root.TryGetProperty("totalDurationSeconds", out var t) && t.TryGetDouble(out var td) && td > 0)
                    return td;
                if (root.TryGetProperty("seconds", out var s) && s.TryGetDouble(out var sd) && sd > 0)
                    return sd;
            }
            catch { /* ignore */ }
        }

        return null;
    }

    private double? ProbeWithFfmpeg(string fullPath)
    {
        var ffmpeg = ResolveFfmpeg();
        if (string.IsNullOrWhiteSpace(ffmpeg)) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-hide_banner -i \"{fullPath}\"",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var err = p.StandardError.ReadToEnd();
            _ = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(12_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;
            }

            var m = DurationRe.Match(err);
            if (!m.Success) return null;
            var h = int.Parse(m.Groups[1].Value);
            var min = int.Parse(m.Groups[2].Value);
            var sec = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            var total = h * 3600 + min * 60 + sec;
            return total > 0.05 ? total : null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "ffmpeg duration probe failed");
            return null;
        }
    }

    private string ResolveFfmpeg()
    {
        if (_ffmpeg is not null) return _ffmpeg;
        lock (_ffmpegLock)
        {
            if (_ffmpeg is not null) return _ffmpeg;

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(_opts.FfmpegPath) && File.Exists(_opts.FfmpegPath))
                candidates.Add(Path.GetFullPath(_opts.FfmpegPath!));

            foreach (var root in new[]
                     {
                         AppContext.BaseDirectory,
                         Path.GetDirectoryName(typeof(MediaDurationProbe).Assembly.Location) ?? "",
                     }.Where(r => r.Length > 0))
            {
                candidates.Add(Path.Combine(root, "Resources", "ffmpeg.exe"));
                candidates.Add(Path.Combine(root, "ffmpeg.exe"));
            }

            foreach (var c in candidates)
            {
                try
                {
                    if (File.Exists(c) && new FileInfo(c).Length > 100_000)
                    {
                        _ffmpeg = Path.GetFullPath(c);
                        return _ffmpeg;
                    }
                }
                catch { /* ignore */ }
            }

            _ffmpeg = "ffmpeg";
            return _ffmpeg;
        }
    }
}
