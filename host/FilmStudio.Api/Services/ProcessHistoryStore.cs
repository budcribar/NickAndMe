using System.Diagnostics;

namespace FilmStudio.Api.Services;

/// <summary>Rolling process memory samples for admin charts.</summary>
public sealed class ProcessHistoryStore
{
    private readonly object _gate = new();
    private readonly List<ProcessSampleDto> _samples = new();
    private const int MaxSamples = 180; // ~6 min at 2s

    public void Sample()
    {
        try
        {
            var p = Process.GetCurrentProcess();
            p.Refresh();
            var dto = new ProcessSampleDto
            {
                At = DateTimeOffset.UtcNow,
                WorkingSetMb = Math.Round(p.WorkingSet64 / (1024.0 * 1024.0), 1),
                GcHeapMb = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 1),
                ThreadCount = p.Threads.Count,
            };
            lock (_gate)
            {
                _samples.Add(dto);
                while (_samples.Count > MaxSamples)
                    _samples.RemoveAt(0);
            }
        }
        catch
        {
            // ignore
        }
    }

    public IReadOnlyList<ProcessSampleDto> GetHistory()
    {
        lock (_gate)
            return _samples.Select(s => s with { }).ToList();
    }
}

public sealed record ProcessSampleDto
{
    public DateTimeOffset At { get; init; }
    public double WorkingSetMb { get; init; }
    public double GcHeapMb { get; init; }
    public int ThreadCount { get; init; }
}
