using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace FilmStudio.Engine;

/// <summary>
/// Post-gen: trim trailing silence / dead air so the next video-extend
/// starts from the last real speech or action, not empty hold frames.
/// </summary>
public static class ClipSilenceTrimmer
{
    private static readonly Regex SilenceEndRe = new(
        @"silence_end:\s*([0-9]+(?:\.[0-9]+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SilenceStartRe = new(
        @"silence_start:\s*([0-9]+(?:\.[0-9]+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DurationRe = new(
        @"Duration:\s*(\d{1,2}):(\d{2}):(\d{2}(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// If trailing silence is found, rewrite <paramref name="videoPath"/> in place
    /// (via temp file). Returns true when a trim was applied.
    /// </summary>
    /// <param name="keepTailSeconds">Seconds to keep after last non-silence (speech landing).</param>
    /// <param name="minTrimSavings">Only trim if we remove at least this many seconds.</param>
    public static async Task<TrimResult> TrimTrailingSilenceAsync(
        string ffmpegPath,
        string videoPath,
        double keepTailSeconds = 0.35,
        double minTrimSavings = 0.4,
        double noiseDb = -35,
        double minSilenceSec = 0.25,
        ILogger? log = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(videoPath))
            return TrimResult.Skipped("missing file or ffmpeg");

        var total = await ProbeDurationAsync(ffmpegPath, videoPath, ct).ConfigureAwait(false);
        if (total is null or < 1.5)
            return TrimResult.Skipped("duration unknown or too short");

        var silenceLog = await RunSilenceDetectAsync(
            ffmpegPath, videoPath, noiseDb, minSilenceSec, ct).ConfigureAwait(false);

        var cutAt = ComputeCutPoint(silenceLog, total.Value, keepTailSeconds);
        if (cutAt is null)
            return TrimResult.Skipped("no trailing silence");

        var savings = total.Value - cutAt.Value;
        if (savings < minTrimSavings)
            return TrimResult.Skipped($"tail only {savings:F2}s");

        // Never cut shorter than floor
        if (cutAt.Value < ClipDurationEstimator.MinSeconds - 0.25)
            return TrimResult.Skipped("would cut below minimum length");

        var dir = Path.GetDirectoryName(videoPath)!;
        var tmp = Path.Combine(dir, $"_trim_{Path.GetFileName(videoPath)}.tmp.mp4");
        try
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            // -nostats -loglevel error: less pipe noise; still drain both streams in FfmpegProcess
            var ok = await RunFfmpegAsync(
                ffmpegPath,
                $"-hide_banner -nostats -loglevel error -y -i \"{videoPath}\" " +
                $"-t {cutAt.Value.ToString("0.###", CultureInfo.InvariantCulture)} " +
                $"-c:v libx264 -preset veryfast -crf 18 -c:a aac -b:a 128k -movflags +faststart \"{tmp}\"",
                ct).ConfigureAwait(false);
            if (!ok || !File.Exists(tmp) || new FileInfo(tmp).Length < 1024)
                return TrimResult.Skipped("ffmpeg trim failed");

            File.Copy(tmp, videoPath, overwrite: true);
            try { File.Delete(tmp); } catch { /* ignore */ }

            // Refresh duration sidecar if present
            try
            {
                await MediaDurationProbe.WriteDurationSidecarAsync(videoPath, cutAt.Value, ct)
                    .ConfigureAwait(false);
            }
            catch { /* optional */ }

            log?.LogInformation(
                "Silence-trimmed {File}: {Before:F2}s → {After:F2}s (−{Saved:F2}s)",
                Path.GetFileName(videoPath), total.Value, cutAt.Value, savings);

            return new TrimResult(true, total.Value, cutAt.Value, $"trimmed −{savings:F2}s");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            return TrimResult.Skipped(ex.Message);
        }
    }

    /// <summary>
    /// Cut after last real audio when the file ends in silence.
    /// Mid-file pauses alone do not trigger a cut.
    /// </summary>
    public static double? ComputeCutPoint(
        string silenceDetectLog,
        double totalDuration,
        double keepTailSeconds)
    {
        if (string.IsNullOrWhiteSpace(silenceDetectLog) || totalDuration < 1.0)
            return null;
        if (double.IsNaN(totalDuration) || double.IsInfinity(totalDuration))
            return null;

        var starts = SilenceStartRe.Matches(silenceDetectLog)
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .OrderBy(x => x)
            .ToList();
        var ends = SilenceEndRe.Matches(silenceDetectLog)
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .OrderBy(x => x)
            .ToList();

        if (starts.Count == 0)
            return null;

        // Trailing silence = a silence_start with no silence_end after it
        // (ffmpeg often omits silence_end when silence runs to EOF).
        double? trailStart = null;
        foreach (var s in starts)
        {
            // Prefer the last silence_start with no silence_end after it (runs to EOF)
            if (!ends.Any(e => e > s + 0.05))
                trailStart = s;
        }

        // Also: last silence_end very near EOF ⇒ trailing region started at matching start
        if (trailStart is null && ends.Count > 0 && starts.Count > 0)
        {
            var lastEnd = ends[^1];
            if (totalDuration - lastEnd < 0.35)
            {
                // find start paired with this end (last start before lastEnd)
                for (var i = starts.Count - 1; i >= 0; i--)
                {
                    if (starts[i] < lastEnd)
                    {
                        trailStart = starts[i];
                        break;
                    }
                }
            }
        }

        if (trailStart is null)
            return null;

        var silenceTail = totalDuration - trailStart.Value;
        if (silenceTail < 0.35)
            return null;

        var cut = trailStart.Value + keepTailSeconds;
        cut = Math.Min(cut, totalDuration - 0.05);
        if (cut >= totalDuration - 0.2)
            return null;
        // Align with TrimTrailingSilenceAsync / product floor
        if (cut < ClipDurationEstimator.MinSeconds - 0.25)
            return null;
        return cut;
    }

    private static async Task<string> RunSilenceDetectAsync(
        string ffmpegPath,
        string videoPath,
        double noiseDb,
        double minSilenceSec,
        CancellationToken ct)
    {
        // silencedetect writes to stderr; drain both pipes (avoid deadlock)
        var args =
            $"-hide_banner -nostats -i \"{videoPath}\" -af silencedetect=noise={noiseDb.ToString(CultureInfo.InvariantCulture)}dB:" +
            $"d={minSilenceSec.ToString(CultureInfo.InvariantCulture)} -f null -";
        var r = await FfmpegProcess.RunAsync(ffmpegPath, args, ct, timeoutMs: 60_000)
            .ConfigureAwait(false);
        return r.StdErr;
    }

    private static async Task<double?> ProbeDurationAsync(
        string ffmpegPath,
        string videoPath,
        CancellationToken ct)
    {
        try
        {
            var r = await FfmpegProcess.RunAsync(
                    ffmpegPath,
                    $"-hide_banner -i \"{videoPath}\"",
                    ct,
                    timeoutMs: 30_000)
                .ConfigureAwait(false);
            var err = r.StdErr;
            var m = DurationRe.Match(err);
            if (!m.Success) return null;
            var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var sec = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            return h * 3600 + min * 60 + sec;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> RunFfmpegAsync(string ffmpegPath, string args, CancellationToken ct)
    {
        // Re-encode can take a while on long clips; 3 min cap
        var r = await FfmpegProcess.RunAsync(ffmpegPath, args, ct, timeoutMs: 180_000)
            .ConfigureAwait(false);
        return r.Success;
    }

    public readonly record struct TrimResult(bool Trimmed, double? BeforeSec, double? AfterSec, string Message)
    {
        public static TrimResult Skipped(string reason) => new(false, null, null, reason);
    }
}
