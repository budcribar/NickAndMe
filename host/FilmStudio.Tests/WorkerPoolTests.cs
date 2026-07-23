using FilmStudio.Core.Options;
using FilmStudio.Engine;
using Microsoft.Extensions.Options;
using Xunit;

namespace FilmStudio.Tests;

public class WorkerPoolTests
{
    [Fact]
    public async Task ApiWorkerPool_respects_global_cap()
    {
        var opts = Options.Create(new FilmStudioOptions
        {
            Capacity = new CapacityOptions
            {
                MaxVideoInFlight = 2,
                MaxVideoInFlightPerUser = 2,
            },
        });
        var pool = new ApiWorkerPool(opts);
        var running = 0;
        var peak = 0;
        var gate = new object();

        async Task Work()
        {
            lock (gate)
            {
                running++;
                if (running > peak) peak = running;
            }
            await Task.Delay(80);
            lock (gate) running--;
        }

        var tasks = Enumerable.Range(0, 6)
            .Select(i => pool.RunAsync($"u{i % 3}", _ => Work(), CancellationToken.None))
            .ToArray();
        await Task.WhenAll(tasks);
        Assert.True(peak <= 2, $"peak concurrency {peak} exceeded cap 2");
    }

    [Fact]
    public async Task ApiWorkerPool_per_user_cap()
    {
        var opts = Options.Create(new FilmStudioOptions
        {
            Capacity = new CapacityOptions
            {
                MaxVideoInFlight = 8,
                MaxVideoInFlightPerUser = 1,
            },
        });
        var pool = new ApiWorkerPool(opts);
        var userRunning = 0;
        var peak = 0;
        var gate = new object();

        async Task Work()
        {
            lock (gate)
            {
                userRunning++;
                if (userRunning > peak) peak = userRunning;
            }
            await Task.Delay(60);
            lock (gate) userRunning--;
        }

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => pool.RunAsync("same-user", _ => Work(), CancellationToken.None))
            .ToArray();
        await Task.WhenAll(tasks);
        Assert.True(peak <= 1, $"per-user peak {peak} exceeded 1");
    }

    [Fact]
    public async Task ApiWorkerPool_resize_under_load_does_not_fault_waiters()
    {
        // Live options object so EnsureCaps sees updated MaxVideoInFlight mid-flight.
        var cap = new CapacityOptions { MaxVideoInFlight = 2, MaxVideoInFlightPerUser = 4 };
        var opts = Options.Create(new FilmStudioOptions { Capacity = cap });
        var pool = new ApiWorkerPool(opts);
        var started = 0;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runners = Enumerable.Range(0, 4)
            .Select(i => pool.RunAsync($"u{i}", async _ =>
            {
                Interlocked.Increment(ref started);
                await hold.Task;
            }, CancellationToken.None))
            .ToArray();

        // Wait until some work is in flight, then shrink capacity (old bug disposed live sem).
        while (Volatile.Read(ref started) < 2)
            await Task.Delay(10);

        cap.MaxVideoInFlight = 1;
        // Trigger EnsureCaps via MaxGlobal while work still holds slots
        _ = pool.MaxGlobal;
        _ = pool.InFlight;

        hold.TrySetResult();
        await Task.WhenAll(runners); // must not throw ObjectDisposedException
        Assert.Equal(4, started);
    }

    [Fact]
    public async Task LocalWorkerPool_resize_under_load_does_not_fault_waiters()
    {
        var cap = new CapacityOptions { MaxFfmpegInFlight = 2 };
        var opts = Options.Create(new FilmStudioOptions { Capacity = cap });
        var pool = new LocalWorkerPool(opts);
        var started = 0;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runners = Enumerable.Range(0, 3)
            .Select(_ => pool.RunAsync(async ct =>
            {
                Interlocked.Increment(ref started);
                await hold.Task;
            }, CancellationToken.None))
            .ToArray();

        while (Volatile.Read(ref started) < 2)
            await Task.Delay(10);

        cap.MaxFfmpegInFlight = 1;
        _ = pool.InFlight; // EnsureCaps

        hold.TrySetResult();
        await Task.WhenAll(runners);
        Assert.Equal(3, started);
    }
}
