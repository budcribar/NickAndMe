using System.Collections.Concurrent;
using System.Text.Json;
using PageToMovie.Core.Models;

namespace PageToMovie.Engine;

/// <summary>
/// Hot-path read caches for multi-user browse: project list, blueprint file bytes, asset dir indexes.
/// Entries are mtime/size validated (or short TTL for the project list) and explicitly invalidated on writes.
/// Async-only hot-path caches (no sync-over-async wrappers).
/// </summary>
public sealed class ProjectReadCache
{
    private static readonly TimeSpan ProjectsListTtl = TimeSpan.FromSeconds(10);

    private readonly object _projectsGate = new();
    private readonly SemaphoreSlim _projectsBuild = new(1, 1);
    private IReadOnlyList<ProjectInfo>? _projects;
    private DateTimeOffset _projectsAt;

    private readonly ConcurrentDictionary<string, BlueprintEntry> _blueprints =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string?> _blueprintPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DirEntry> _dirs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _buildLocks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When false, every call is a full rebuild (A/B soaks).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cached project list with short TTL (new folders appear within ~10s).</summary>
    public async Task<IReadOnlyList<ProjectInfo>> GetOrBuildProjectsAsync(
        Func<CancellationToken, Task<IReadOnlyList<ProjectInfo>>> build,
        CancellationToken ct = default)
    {
        if (!Enabled)
            return await build(ct).ConfigureAwait(false) ?? Array.Empty<ProjectInfo>();

        lock (_projectsGate)
        {
            if (_projects is not null && DateTimeOffset.UtcNow - _projectsAt <= ProjectsListTtl)
                return CloneProjects(_projects);
        }

        await _projectsBuild.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_projectsGate)
            {
                if (_projects is not null && DateTimeOffset.UtcNow - _projectsAt <= ProjectsListTtl)
                    return CloneProjects(_projects);
            }

            var built = await build(ct).ConfigureAwait(false) ?? Array.Empty<ProjectInfo>();
            var snap = CloneProjects(built);
            lock (_projectsGate)
            {
                _projects = snap;
                _projectsAt = DateTimeOffset.UtcNow;
                return CloneProjects(snap);
            }
        }
        finally
        {
            _projectsBuild.Release();
        }
    }

    public void InvalidateProjectsList()
    {
        lock (_projectsGate)
        {
            _projects = null;
            _projectsAt = default;
        }
    }

    public async Task<string?> GetOrFindBlueprintPathAsync(
        string projectId,
        Func<CancellationToken, Task<string?>> find,
        CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(projectId))
            return await find(ct).ConfigureAwait(false);

        var key = projectId.Trim();
        // Only cache positive hits — a miss may become a hit after Stage 2 writes the blueprint
        if (_blueprintPaths.TryGetValue(key, out var hit) && !string.IsNullOrWhiteSpace(hit))
            return hit;

        var path = await find(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(path))
            _blueprintPaths[key] = path;
        else
            _blueprintPaths.TryRemove(key, out _);
        return path;
    }

    /// <summary>
    /// Shared parsed blueprint — <b>do not dispose</b>. Reloaded when file mtime/size changes.
    /// </summary>
    public async Task<JsonDocument?> GetOrLoadBlueprintDocumentAsync(
        string? absolutePath,
        CancellationToken ct = default)
    {
        var entry = await GetOrLoadBlueprintEntryAsync(absolutePath, ct).ConfigureAwait(false);
        return entry?.Doc;
    }

    public async Task<byte[]?> GetOrLoadBlueprintUtf8Async(
        string? absolutePath,
        CancellationToken ct = default)
    {
        var entry = await GetOrLoadBlueprintEntryAsync(absolutePath, ct).ConfigureAwait(false);
        return entry?.Utf8;
    }

    public static JsonDocument? CloneBlueprintDocument(JsonDocument? shared)
    {
        if (shared is null) return null;
        return JsonDocument.Parse(shared.RootElement.GetRawText());
    }

    private async Task<BlueprintEntry?> GetOrLoadBlueprintEntryAsync(
        string? absolutePath,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath))
            return null;

        if (!Enabled)
            return null;

        FileInfo fi;
        try { fi = new FileInfo(absolutePath); }
        catch { return null; }

        var key = fi.FullName;
        if (_blueprints.TryGetValue(key, out var hit) &&
            hit.Ticks == fi.LastWriteTimeUtc.Ticks &&
            hit.Length == fi.Length)
            return hit;

        var gate = _buildLocks.GetOrAdd("bp:" + key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try { fi.Refresh(); }
            catch { return null; }

            if (_blueprints.TryGetValue(key, out hit) &&
                hit.Ticks == fi.LastWriteTimeUtc.Ticks &&
                hit.Length == fi.Length)
                return hit;

            var utf8 = await File.ReadAllBytesAsync(absolutePath, ct).ConfigureAwait(false);
            var doc = JsonDocument.Parse(utf8);
            var entry = new BlueprintEntry
            {
                Ticks = fi.LastWriteTimeUtc.Ticks,
                Length = fi.Length,
                Utf8 = utf8,
                Doc = doc,
            };

            if (_blueprints.TryRemove(key, out var old))
            {
                try { old.Doc.Dispose(); } catch { /* ignore */ }
            }

            _blueprints[key] = entry;
            return entry;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// File name → length map. Directory enumeration stays sync (cheap metadata);
    /// single-flight uses async wait so request threads are not blocked on the gate.
    /// </summary>
    public async Task<Dictionary<string, long>> GetOrIndexDirAsync(
        string dir,
        Func<string, CancellationToken, Task<Dictionary<string, long>>> index,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dir))
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        if (!Enabled)
            return await index(dir, ct).ConfigureAwait(false)
                   ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        long dirTicks = 0;
        var exists = Directory.Exists(dir);
        if (exists)
        {
            try { dirTicks = Directory.GetLastWriteTimeUtc(dir).Ticks; }
            catch { /* ignore */ }
        }

        var key = Path.GetFullPath(dir);
        if (_dirs.TryGetValue(key, out var hit) && hit.Exists == exists && hit.DirTicks == dirTicks)
            return CloneDir(hit.Files);

        var gate = _buildLocks.GetOrAdd("dir:" + key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            exists = Directory.Exists(dir);
            if (exists)
            {
                try { dirTicks = Directory.GetLastWriteTimeUtc(dir).Ticks; }
                catch { dirTicks = 0; }
            }
            else
            {
                dirTicks = 0;
            }

            if (_dirs.TryGetValue(key, out hit) && hit.Exists == exists && hit.DirTicks == dirTicks)
                return CloneDir(hit.Files);

            var map = await index(dir, ct).ConfigureAwait(false)
                      ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var stored = CloneDir(map);
            _dirs[key] = new DirEntry
            {
                Exists = exists,
                DirTicks = dirTicks,
                Files = stored,
            };
            return CloneDir(stored);
        }
        finally
        {
            gate.Release();
        }
    }

    public void InvalidateProject(string? projectId, string? projectDir = null)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            InvalidateAll();
            return;
        }

        _blueprintPaths.TryRemove(projectId.Trim(), out _);

        if (!string.IsNullOrWhiteSpace(projectDir))
        {
            try
            {
                var root = Path.GetFullPath(projectDir);
                foreach (var key in _blueprints.Keys.ToArray())
                {
                    if (key.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                        _blueprints.TryRemove(key, out var old))
                    {
                        try { old.Doc.Dispose(); } catch { /* ignore */ }
                    }
                }

                foreach (var key in _dirs.Keys.ToArray())
                {
                    if (key.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        _dirs.TryRemove(key, out _);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }

    public void InvalidateAll()
    {
        InvalidateProjectsList();
        _blueprintPaths.Clear();
        foreach (var key in _blueprints.Keys.ToArray())
        {
            if (_blueprints.TryRemove(key, out var old))
            {
                try { old.Doc.Dispose(); } catch { /* ignore */ }
            }
        }
        _dirs.Clear();
    }

    private static List<ProjectInfo> CloneProjects(IReadOnlyList<ProjectInfo> src)
    {
        var list = new List<ProjectInfo>(src.Count);
        foreach (var p in src)
        {
            list.Add(new ProjectInfo
            {
                Id = p.Id,
                Title = p.Title,
                Label = p.Label,
                Path = p.Path,
            });
        }
        return list;
    }

    private static Dictionary<string, long> CloneDir(Dictionary<string, long> src) =>
        new(src, StringComparer.OrdinalIgnoreCase);

    private sealed class BlueprintEntry
    {
        public long Ticks { get; init; }
        public long Length { get; init; }
        public byte[] Utf8 { get; init; } = Array.Empty<byte>();
        public JsonDocument Doc { get; init; } = null!;
    }

    private sealed class DirEntry
    {
        public bool Exists { get; init; }
        public long DirTicks { get; init; }
        public Dictionary<string, long> Files { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
