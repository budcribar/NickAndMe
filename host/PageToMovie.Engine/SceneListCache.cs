using System.Collections.Concurrent;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;

namespace PageToMovie.Engine;

/// <summary>
/// Single-flight cache for <see cref="ProjectStore.ListScenes"/> results.
/// Keyed by project + light/full (probeDurations). Short TTL + explicit invalidation.
/// </summary>
public sealed class SceneListCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly KeyedAsyncLock<string> _buildLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _ttl;

    public SceneListCache(TimeSpan? ttl = null) =>
        // 10s is safe with explicit invalidation after gen/remux/blueprint writes
        _ttl = ttl ?? TimeSpan.FromSeconds(10);

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

        using (await _buildLocks.LockAsync(key, ct).ConfigureAwait(false))
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

    // JSON round-trip rather than a field-by-field copy: a manual copy silently drops any field the
    // author forgets to list, and stays silent forever — SceneSummary.IsCredits, IsUserOverride,
    // IsApproved, and HasBackgroundMusic were all missing here and got reset to their default
    // (false) on every cache read, e.g. an end-credits scene reporting IsCredits=false and never
    // being routed to client-side rendering. This can't drop a field again when SceneSummary grows
    // new ones later.
    private static SceneSummary CloneSummary(SceneSummary s)
    {
        var clone = JsonSerializer.Deserialize<SceneSummary>(JsonSerializer.Serialize(s))!;
        // Locks applied per-request — leave empty in cache regardless of what was serialized.
        clone.LockOwnerUserId = null;
        clone.LockedByOther = false;
        clone.LockReason = null;
        return clone;
    }

    private sealed class CacheEntry
    {
        public DateTimeOffset BuiltAt { get; set; }
        public List<SceneSummary> Scenes { get; set; } = new();
    }
}
