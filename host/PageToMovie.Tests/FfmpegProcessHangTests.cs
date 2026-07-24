using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Regression: redirected ffmpeg pipes must be drained or WaitForExit deadlocks
/// (silence-trim re-encode / auto-review frame sample hung the pilot for minutes).
/// </summary>
public class FfmpegProcessHangTests
{
    private static string? FindFfmpeg()
    {
        foreach (var c in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "Resources", "ffmpeg.exe"),
                     Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                 })
        {
            if (File.Exists(c)) return c;
        }

        // Engine build output (common when tests copy Resources)
        var eng = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "PageToMovie.Engine", "bin", "Debug", "net10.0", "Resources", "ffmpeg.exe"));
        if (File.Exists(eng)) return eng;

        var api = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "PageToMovie.Api", "bin", "Debug", "net10.0", "Resources", "ffmpeg.exe"));
        return File.Exists(api) ? api : null;
    }

    private static string? FindFixtureMp4()
    {
        foreach (var c in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "Fixtures", "clip_merge_10s.mp4"),
                     Path.GetFullPath(Path.Combine(
                         AppContext.BaseDirectory, "..", "..", "..", "..",
                         "PageToMovie.Fakes", "Fixtures", "clip_merge_10s.mp4")),
                     Path.GetFullPath(Path.Combine(
                         AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                         "projects", "PoePilot3", "assets", "video", "scene_01_clip_01.mp4")),
                 })
        {
            if (File.Exists(c) && new FileInfo(c).Length > 100_000)
                return c;
        }
        return null;
    }

    [Fact]
    public async Task SilenceTrim_on_fixture_completes_under_30s()
    {
        var ffmpeg = FindFfmpeg();
        var mp4 = FindFixtureMp4();
        if (ffmpeg is null || mp4 is null)
        {
            // Environment without fixtures — skip rather than fail CI boxes without media
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "fs_sil_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, "clip.mp4");
        try
        {
            File.Copy(mp4, copy);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await ClipSilenceTrimmer.TrimTrailingSilenceAsync(
                ffmpeg, copy, ct: cts.Token);
            // Must not hang; any skip/trim outcome is fine
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
            Assert.True(File.Exists(copy));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public async Task FfmpegProcess_reencode_drains_pipes_without_deadlock()
    {
        var ffmpeg = FindFfmpeg();
        var mp4 = FindFixtureMp4();
        if (ffmpeg is null || mp4 is null)
            return;

        var outPath = Path.Combine(Path.GetTempPath(), "fs_enc_" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            // Verbose progress would fill the pipe if we didn't drain — this is the hang repro
            var args =
                $"-hide_banner -y -i \"{mp4}\" -t 2 -c:v libx264 -preset ultrafast -crf 28 " +
                $"-c:a aac -b:a 64k -movflags +faststart \"{outPath}\"";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var r = await FfmpegProcess.RunAsync(ffmpeg, args, cts.Token, timeoutMs: 40_000);
            Assert.False(r.TimedOut);
            Assert.True(r.Success, r.StdErr);
            Assert.True(File.Exists(outPath));
            Assert.True(new FileInfo(outPath).Length > 1024);
        }
        finally
        {
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { /* */ }
        }
    }
}
