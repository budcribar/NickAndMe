using System.Collections.Concurrent;
using FilmStudio.Core.Models;

namespace FilmStudio.Engine;

/// <summary>
/// Single-flight cache for <see cref="ProjectStore.ListScenes"/> results.
/// Keyed by project + light/full (probeDurations). Short TTL + explicit invalidation.
/// </summary>
public sealed class SceneListCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _buildLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _ttl;

    public SceneListCache(TimeSpan? ttl = null) =>
        // 10s is safe with explicit invalidation after gen/remux/blueprint writes
        _ttl = ttl ?? TimeSpan.FromSeconds(10);

    public IReadOnlyList<SceneSummary> GetOrBuild(
        string projectId,
        bool probeDurations,
        Func<IReadOnlyList<SceneSummary>> build) =>
        GetOrBuildAsync(projectId, probeDurations, _ => Task.FromResult(build() ?? Array.Empty<SceneSummary>()))
            .GetAwaiter().GetResult();

    public async Task<IReadOnlyList<SceneSummary>> GetOrBuildAsync(
        string projectId,
        bool probeDurations,
        Func<CancellationToken, Task<IReadOnlyList<SceneSummary>>> build,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return await build(ct).ConfigureAwait(false) ?? Array.Empty<SceneSummary>();

        var key = MakeKey(projectId, probeDurations);
        if (TryGetFresh(key, out var hit))
            return CloneList(hit);

        var gate = _buildLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryGetFresh(key, out hit))
                return CloneList(hit);

            var built = await build(ct).ConfigureAwait(false) ?? Array.Empty<SceneSummary>();
            var list = built is List<SceneSummary> l ? l : built.ToList();
            var stored = CloneList(list);
            _entries[key] = new CacheEntry
            {
                BuiltAt = DateTimeOffset.UtcNow,
                Scenes = stored,
            };
            return CloneList(stored);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Drop list cache for a project (both light and full).</summary>
    public void Invalidate(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return;
        _entries.TryRemove(MakeKey(projectId, probeDurations: true), out _);
        _entries.TryRemove(MakeKey(projectId, probeDurations: false), out _);
    }

    public void InvalidateAll() => _entries.Clear();

    private bool TryGetFresh(string key, out IReadOnlyList<SceneSummary> scenes)
    {
        scenes = Array.Empty<SceneSummary>();
        if (!_entries.TryGetValue(key, out var entry))
            return false;
        if (DateTimeOffset.UtcNow - entry.BuiltAt > _ttl)
        {
            _entries.TryRemove(key, out _);
            return false;
        }
        scenes = entry.Scenes;
        return true;
    }

    private static string MakeKey(string projectId, bool probeDurations) =>
        projectId.Trim() + (probeDurations ? "|full" : "|light");

    private static List<SceneSummary> CloneList(IReadOnlyList<SceneSummary> src)
    {
        var list = new List<SceneSummary>(src.Count);
        foreach (var s in src)
            list.Add(CloneSummary(s));
        return list;
    }

    private static SceneSummary CloneSummary(SceneSummary s) => new()
    {
        SceneNumber = s.SceneNumber,
        Setting = s.Setting,
        ClipCount = s.ClipCount,
        ClipsOnDisk = s.ClipsOnDisk,
        ClipsComplete = s.ClipsComplete,
        PlannedDurationSeconds = s.PlannedDurationSeconds,
        ActualDurationSeconds = s.ActualDurationSeconds,
        DurationSeconds = s.DurationSeconds,
        CompositeExists = s.CompositeExists,
        CharactersOnScreen = s.CharactersOnScreen.ToList(),
        LocationIds = s.LocationIds.ToList(),
        PrimaryLocationId = s.PrimaryLocationId,
        Status = s.Status,
        // Locks applied per-request — leave empty in cache
        LockOwnerUserId = null,
        LockedByOther = false,
        LockReason = null,
    };

    private sealed class CacheEntry
    {
        public DateTimeOffset BuiltAt { get; set; }
        public List<SceneSummary> Scenes { get; set; } = new();
    }
}
