using System.Text.Json;
using FilmStudio.Core.Models;
using Microsoft.Extensions.Logging;

namespace FilmStudio.Engine;

/// <summary>
/// Versioned prompt packs under <c>{WorkspaceRoot}/prompts/packs/</c> + active manifest.
/// Host defaults ship in-repo; workspace can override active selection.
/// </summary>
public sealed class PromptPackService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public const string KindGen = "gen";
    public const string KindAutoReview = "auto_review";

    private readonly ProjectStore _projects;
    private readonly ILogger<PromptPackService> _log;
    private readonly object _lock = new();

    public PromptPackService(ProjectStore projects, ILogger<PromptPackService> log)
    {
        _projects = projects;
        _log = log;
    }

    public string PacksDir => Path.Combine(_projects.WorkspaceRoot, "prompts", "packs");
    public string ManifestPath => Path.Combine(PacksDir, "manifest.json");

    /// <summary>Ensure default packs exist and return manifest.</summary>
    public PromptPackManifest EnsureDefaults()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(PacksDir);
            var manifest = LoadManifestUnlocked();
            EnsurePackFile(
                manifest,
                id: "gen-v1",
                kind: KindGen,
                fileName: "gen-v1.txt",
                notes: "Default video gen addendum (house rules + continuity).",
                body: DefaultGenPackBody);
            EnsurePackFile(
                manifest,
                id: "auto_review-v1",
                kind: KindAutoReview,
                fileName: "auto_review-v1.txt",
                notes: "Default auto-review QC instructions addendum.",
                body: DefaultAutoReviewPackBody);

            if (string.IsNullOrWhiteSpace(manifest.ActiveGenPackId))
                manifest.ActiveGenPackId = "gen-v1";
            if (string.IsNullOrWhiteSpace(manifest.ActiveAutoReviewPackId))
                manifest.ActiveAutoReviewPackId = "auto_review-v1";

            SaveManifestUnlocked(manifest);
            return CloneManifest(manifest);
        }
    }

    public PromptPackManifest GetManifest() => EnsureDefaults();

    public IReadOnlyList<PromptPackInfo> ListPacks() => EnsureDefaults().Packs;

    public string? LoadActivePackText(string kind)
    {
        var m = EnsureDefaults();
        var id = string.Equals(kind, KindAutoReview, StringComparison.OrdinalIgnoreCase)
            ? m.ActiveAutoReviewPackId
            : m.ActiveGenPackId;
        return LoadPackText(id);
    }

    public string? LoadPackText(string packId)
    {
        var m = EnsureDefaults();
        var pack = m.Packs.FirstOrDefault(p =>
            string.Equals(p.Id, packId, StringComparison.OrdinalIgnoreCase));
        if (pack is null) return null;
        var path = Path.Combine(_projects.WorkspaceRoot, pack.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return null;
        return File.ReadAllText(path);
    }

    public PromptPackManifest Activate(string packId)
    {
        lock (_lock)
        {
            var m = LoadManifestUnlocked();
            var pack = m.Packs.FirstOrDefault(p =>
                string.Equals(p.Id, packId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Unknown pack: {packId}");

            if (string.Equals(pack.Kind, KindAutoReview, StringComparison.OrdinalIgnoreCase))
                m.ActiveAutoReviewPackId = pack.Id;
            else
                m.ActiveGenPackId = pack.Id;

            foreach (var p in m.Packs)
            {
                p.Active = string.Equals(p.Id, m.ActiveGenPackId, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(p.Id, m.ActiveAutoReviewPackId, StringComparison.OrdinalIgnoreCase);
            }

            SaveManifestUnlocked(m);
            _log.LogInformation("Activated prompt pack {Id} kind={Kind}", pack.Id, pack.Kind);
            return CloneManifest(m);
        }
    }

    /// <summary>Create a new pack version from body text (admin).</summary>
    public PromptPackInfo CreateVersion(string kind, string versionLabel, string body, string? notes = null)
    {
        kind = kind.Trim().ToLowerInvariant();
        if (kind is not (KindGen or KindAutoReview))
            throw new InvalidOperationException("kind must be gen or auto_review");
        var safeVer = string.Join("", (versionLabel ?? "next").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        if (safeVer.Length == 0) safeVer = DateTime.UtcNow.ToString("yyyyMMddHHmm");
        var id = $"{kind}-{safeVer}";
        var fileName = $"{id}.txt";

        lock (_lock)
        {
            var m = LoadManifestUnlocked();
            if (m.Packs.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Pack already exists: {id}");

            Directory.CreateDirectory(PacksDir);
            var rel = Path.Combine("prompts", "packs", fileName).Replace('\\', '/');
            var full = Path.Combine(PacksDir, fileName);
            File.WriteAllText(full, body ?? "");
            var info = new PromptPackInfo
            {
                Id = id,
                Kind = kind,
                Version = safeVer,
                RelativePath = rel,
                Active = false,
                UpdatedAt = DateTimeOffset.UtcNow,
                Notes = notes,
            };
            m.Packs.Add(info);
            SaveManifestUnlocked(m);
            return info;
        }
    }

    private void EnsurePackFile(
        PromptPackManifest manifest,
        string id,
        string kind,
        string fileName,
        string notes,
        string body)
    {
        var rel = Path.Combine("prompts", "packs", fileName).Replace('\\', '/');
        var full = Path.Combine(PacksDir, fileName);
        if (!File.Exists(full))
            File.WriteAllText(full, body);

        var existing = manifest.Packs.FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            manifest.Packs.Add(new PromptPackInfo
            {
                Id = id,
                Kind = kind,
                Version = "1",
                RelativePath = rel,
                Notes = notes,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        foreach (var p in manifest.Packs)
        {
            p.Active = string.Equals(p.Id, manifest.ActiveGenPackId, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(p.Id, manifest.ActiveAutoReviewPackId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private PromptPackManifest LoadManifestUnlocked()
    {
        if (!File.Exists(ManifestPath))
            return new PromptPackManifest();
        try
        {
            var json = File.ReadAllText(ManifestPath);
            return JsonSerializer.Deserialize<PromptPackManifest>(json, JsonOpts)
                   ?? new PromptPackManifest();
        }
        catch
        {
            return new PromptPackManifest();
        }
    }

    private void SaveManifestUnlocked(PromptPackManifest m)
    {
        Directory.CreateDirectory(PacksDir);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(m, JsonOpts) + "\n");
    }

    private static PromptPackManifest CloneManifest(PromptPackManifest m) =>
        JsonSerializer.Deserialize<PromptPackManifest>(JsonSerializer.Serialize(m, JsonOpts), JsonOpts)
        ?? new PromptPackManifest();

    private const string DefaultGenPackBody =
        """
        # Film Studio gen pack (active addendum)

        Apply these house rules when building clip video prompts:
        - Dialogue clips must have audible speech and visible lip motion for the speaker; never silent mouths on dialogue.
        - Continuity: when extending from previous clip, match wardrobe, place, and facing from the last frames.
        - Respect VOICE LOCK and character visual locks exactly.
        - Prefer tight action after speech; avoid long empty holds.
        """;

    private const string DefaultAutoReviewPackBody =
        """
        # Film Studio auto-review pack (QC addendum)

        When reviewing clips:
        - Always judge start of current clip against previous clip tail when provided.
        - Flag silent dialogue, identity drift, and hard continuity jumps as fail when confidence is high.
        - Prefer clip visual_prompt rewrites over character rewrites unless look/voice is clearly wrong for the whole cast member.
        - Keep Character_* keys intact in suggestions.
        """;
}
