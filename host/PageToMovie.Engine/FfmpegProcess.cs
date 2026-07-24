using System.Diagnostics;

namespace PageToMovie.Engine;

/// <summary>
/// Safe ffmpeg process launch: always drain stdout/stderr while waiting so the
/// OS pipe cannot fill and deadlock the child (classic WaitForExit hang).
/// </summary>
public static class FfmpegProcess
{
    public const int DefaultTimeoutMs = 120_000;

    public sealed record Result(int ExitCode, string StdOut, string StdErr, bool TimedOut)
    {
        public bool Success => !TimedOut && ExitCode == 0;
        public string CombinedLog =>
            string.IsNullOrEmpty(StdOut) ? StdErr
            : string.IsNullOrEmpty(StdErr) ? StdOut
            : StdOut + "\n" + StdErr;
    }

    /// <summary>
    /// Run ffmpeg with redirected streams drained in parallel.
    /// Kills the process tree on timeout or cancellation.
    /// </summary>
    public static async Task<Result> RunAsync(
        string ffmpegPath,
        string arguments,
        CancellationToken ct = default,
        int timeoutMs = DefaultTimeoutMs,
        string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
            return new Result(-1, "", "ffmpeg path empty", TimedOut: false);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        using var proc = Process.Start(psi);
        if (proc is null)
            return new Result(-1, "", "Could not start ffmpeg", TimedOut: false);

        // Drain both pipes concurrently BEFORE / while waiting for exit.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs > 0)
            timeoutCts.CancelAfter(timeoutMs);

        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(proc);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw;
        }

        string stdout;
        string stderr;
        try
        {
            // After kill/exit, finish draining (use CancellationToken.None so we get partial buffers)
            var drain = Task.WhenAll(stdoutTask, stderrTask);
            await Task.WhenAny(drain, Task.Delay(5_000)).ConfigureAwait(false);
            stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "";
            stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";
        }
        catch
        {
            stdout = "";
            stderr = "";
        }

        if (timedOut)
            return new Result(-1, stdout, stderr + "\n[ffmpeg timed out]", TimedOut: true);

        try
        {
            return new Result(proc.ExitCode, stdout, stderr, TimedOut: false);
        }
        catch
        {
            return new Result(-1, stdout, stderr, TimedOut: false);
        }
    }

    /// <summary>Convenience: success bool only.</summary>
    public static async Task<bool> RunOkAsync(
        string ffmpegPath,
        string arguments,
        CancellationToken ct = default,
        int timeoutMs = DefaultTimeoutMs)
    {
        var r = await RunAsync(ffmpegPath, arguments, ct, timeoutMs).ConfigureAwait(false);
        return r.Success;
    }

    /// <summary>Convenience: stderr (ffmpeg logs there) on success or failure.</summary>
    public static async Task<string> RunCaptureStderrAsync(
        string ffmpegPath,
        string arguments,
        CancellationToken ct = default,
        int timeoutMs = DefaultTimeoutMs)
    {
        var r = await RunAsync(ffmpegPath, arguments, ct, timeoutMs).ConfigureAwait(false);
        return r.StdErr;
    }

    private static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch
        {
            /* ignore */
        }
    }
}
