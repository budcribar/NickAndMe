using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Models;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Persist admin review of AI-proposed house rules (check-off list).
/// Path: <c>{WorkspaceRoot}/_learning/proposal_checklist.json</c>.
/// Propose merges by exact text or theme similarity and keeps reviewed items.
/// </summary>
public sealed class ProposalChecklistService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Jaccard token overlap threshold for soft text match (same theme wording drift).</summary>
    public const double ThemeMatchMinScore = 0.32;

    private readonly ProjectStore _projects;
    private readonly ILogger<ProposalChecklistService> _log;
    private readonly object _gate = new();

    public ProposalChecklistService(ProjectStore projects, ILogger<ProposalChecklistService> log)
    {
        _projects = projects;
        _log = log;
    }

    public string ChecklistPath =>
        Path.Combine(_projects.WorkspaceRoot, "_learning", "proposal_checklist.json");

    public ProposalChecklistDocument Load()
    {
        lock (_gate)
        {
            var path = ChecklistPath;
            if (!File.Exists(path))
                return SaveUnlocked(SeedDefaultSessionReview());
            try
            {
                var doc = JsonSerializer.Deserialize<ProposalChecklistDocument>(
                    File.ReadAllText(path), JsonOpts) ?? new ProposalChecklistDocument();
                if (doc.Items.Count == 0)
                    return SaveUnlocked(SeedDefaultSessionReview());
                return doc;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed loading proposal checklist");
                return SaveUnlocked(SeedDefaultSessionReview());
            }
        }
    }

    public ProposalChecklistDocument Save(ProposalChecklistDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        lock (_gate)
            return SaveUnlocked(doc);
    }

    private ProposalChecklistDocument SaveUnlocked(ProposalChecklistDocument doc)
    {
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        var path = ChecklistPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOpts) + "\n");
        return doc;
    }

    /// <summary>
    /// After Propose-from-fails: merge new bullets into the checklist.
    /// Preserves reviewed/disposition via exact text or theme match; keeps prior reviewed
    /// items that the new propose did not restate; only replaces unmatched pending items.
    /// </summary>
    public ProposalChecklistDocument IngestProposal(string proposal, string? sourceLabel = null)
    {
        var bullets = ParseBullets(proposal);
        if (bullets.Count == 0 && !string.IsNullOrWhiteSpace(proposal))
            bullets.Add(proposal.Trim());

        lock (_gate)
        {
            var prev = LoadUnlockedNoSeedWrite();
            var available = prev.Items.ToList();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<ProposalChecklistItem>();

            foreach (var b in bullets)
            {
                var match = FindBestMatch(b, available, used);
                if (match is not null)
                {
                    used.Add(match.Id);
                    // Keep review state; refresh wording only when still pending (no disposition).
                    var keepText = IsDoneOrReviewed(match) ? match.Text : b;
                    items.Add(CloneWith(
                        match,
                        text: keepText,
                        // If previously reviewed, stay reviewed even if status string was stale
                        reviewed: match.Reviewed || !string.IsNullOrWhiteSpace(match.Disposition),
                        status: ResolveStatus(match)));
                }
                else
                {
                    items.Add(new ProposalChecklistItem
                    {
                        Id = ShortId(b),
                        Text = b,
                        Reviewed = false,
                        Status = "pending",
                    });
                }
            }

            // Retain prior reviewed / dispositioned items not restated in this propose
            foreach (var old in available)
            {
                if (used.Contains(old.Id)) continue;
                if (!IsDoneOrReviewed(old)) continue; // drop unmatched pending (replaced by new propose)
                items.Add(old);
            }

            var doc = new ProposalChecklistDocument
            {
                SourceLabel = sourceLabel ?? "propose_from_fails",
                RawProposal = proposal,
                Items = DedupById(items),
            };
            return SaveUnlocked(doc);
        }
    }

    /// <summary>
    /// When project rules are approved, mark best-matching checklist items as accepted/done
    /// so the admin list stays in sync with what actually shipped into project_rules.
    /// </summary>
    public ProposalChecklistDocument MarkAcceptedFromRuleTexts(
        IEnumerable<string> ruleTexts,
        string disposition = "accepted",
        string? note = null)
    {
        var texts = (ruleTexts ?? Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToList();
        if (texts.Count == 0)
            return Load();

        lock (_gate)
        {
            var doc = LoadUnlockedNoSeedWrite();
            if (doc.Items.Count == 0)
                doc = SeedDefaultSessionReview();

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var now = DateTimeOffset.UtcNow;
            var matched = 0;

            foreach (var text in texts)
            {
                var match = FindBestMatch(text, doc.Items, used, minScore: ThemeMatchMinScore);
                if (match is null) continue;
                used.Add(match.Id);
                match.Reviewed = true;
                match.Status = "reviewed";
                match.Disposition = string.IsNullOrWhiteSpace(disposition) ? "accepted" : disposition.Trim();
                match.Note = string.IsNullOrWhiteSpace(note)
                    ? "Synced from approved project rule"
                    : note.Trim();
                match.ReviewedAt = now;
                matched++;
            }

            if (matched > 0)
                _log.LogInformation("Checklist: marked {N} item(s) from approved project rules", matched);

            return SaveUnlocked(doc);
        }
    }

    public ProposalChecklistDocument Upsert(ProposalChecklistUpsertRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (!string.IsNullOrWhiteSpace(req.RawProposal) && (req.Items is null || req.Items.Count == 0))
            return IngestProposal(req.RawProposal!, req.SourceLabel);

        var doc = Load();
        if (!string.IsNullOrWhiteSpace(req.SourceLabel))
            doc.SourceLabel = req.SourceLabel;
        if (!string.IsNullOrWhiteSpace(req.RawProposal))
            doc.RawProposal = req.RawProposal;
        if (req.Items is { Count: > 0 })
            doc.Items = req.Items;
        return Save(doc);
    }

    public ProposalChecklistDocument Toggle(ProposalChecklistToggleRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (string.IsNullOrWhiteSpace(req.Id))
            throw new ArgumentException("Id required", nameof(req));

        lock (_gate)
        {
            var doc = LoadUnlockedNoSeedWrite();
            if (doc.Items.Count == 0)
                doc = SeedDefaultSessionReview();

            var item = doc.Items.FirstOrDefault(i =>
                string.Equals(i.Id, req.Id, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Unknown checklist item: {req.Id}");

            item.Reviewed = req.Reviewed;
            item.Status = req.Reviewed ? "reviewed" : "pending";
            if (req.Disposition is not null)
                item.Disposition = string.IsNullOrWhiteSpace(req.Disposition) ? null : req.Disposition.Trim();
            if (req.Note is not null)
                item.Note = req.Note;
            item.ReviewedAt = req.Reviewed ? DateTimeOffset.UtcNow : null;
            return SaveUnlocked(doc);
        }
    }

    public static List<string> ParseBullets(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 8) continue;
            if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("• "))
            {
                list.Add(line[2..].Trim());
                continue;
            }
            var m = Regex.Match(line, @"^\d+[\.\)]\s+(.+)$");
            if (m.Success)
                list.Add(m.Groups[1].Value.Trim());
        }
        return list;
    }

    /// <summary>
    /// Seed checklist with the Tell-Tale / pilot Admin proposals we walked through in session.
    /// </summary>
    public static ProposalChecklistDocument SeedDefaultSessionReview()
    {
        var texts = new[]
        {
            "Require every visual prompt to fully describe the speaking or featured character by stable name and appearance so Narrator is never swapped with another role such as Old Man.",
            "Reject truncated visual prompts; each must be complete sentences covering subject, expression, gaze, wardrobe, and era before generation.",
            "Enforce audio-visual parity: if the clip is silent or has no shout, the character must not be shown mid-shout, open-mouthed, or with clenched-fist yelling pose.",
            "Specify explicit gaze and eye state (e.g. eyes open and facing camera) whenever the shot should address the viewer; flag shut or downcast eyes as fail.",
            "Mandate photoreal period-correct human look and fabrics (e.g. nervous 1840s man in wool coat); ban smooth vampiric CGI or stylized skin.",
            "Include a short negative constraint in every prompt against wrong identity, wrong era, CGI sheen, and mismatched expression.",
            "Auto-review must check character identity consistency, prompt length/completeness, silence-vs-expression match, eye contact, and photoreal period wardrobe before accept.",
        };
        return new ProposalChecklistDocument
        {
            SourceLabel = "session_admin_proposals",
            RawProposal = string.Join("\n", texts.Select(t => "- " + t)),
            Items = texts.Select(t => new ProposalChecklistItem
            {
                Id = ShortId(t),
                Text = t,
                Reviewed = false,
                Status = "pending",
            }).ToList(),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    // ---- matching ----

    /// <summary>Best unused prior item for <paramref name="text"/>, or null.</summary>
    public static ProposalChecklistItem? FindBestMatch(
        string text,
        IReadOnlyList<ProposalChecklistItem> candidates,
        HashSet<string> usedIds,
        double minScore = ThemeMatchMinScore)
    {
        ProposalChecklistItem? best = null;
        var bestScore = 0.0;
        var key = NormalizeKey(text);
        var themes = InferThemes(text);
        var tokens = SignificantTokens(text);

        foreach (var c in candidates)
        {
            if (usedIds.Contains(c.Id)) continue;
            var cKey = NormalizeKey(c.Text);
            if (string.Equals(key, cKey, StringComparison.Ordinal))
                return c; // exact wins immediately

            var score = ScoreMatch(tokens, themes, c.Text);
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        return bestScore >= minScore ? best : null;
    }

    /// <summary>Public for tests: similarity score in 0..1+.</summary>
    public static double ScoreMatch(string a, string b) =>
        ScoreMatch(SignificantTokens(a), InferThemes(a), b);

    private static double ScoreMatch(
        HashSet<string> tokensA,
        HashSet<string> themesA,
        string textB)
    {
        var tokensB = SignificantTokens(textB);
        var themesB = InferThemes(textB);
        var jaccard = Jaccard(tokensA, tokensB);
        var themeOverlap = themesA.Count == 0 || themesB.Count == 0
            ? 0
            : (double)themesA.Intersect(themesB, StringComparer.OrdinalIgnoreCase).Count()
              / Math.Max(1, Math.Min(themesA.Count, themesB.Count));
        // Theme agreement boosts soft wording drift (photoreal vs live-action period, etc.)
        return jaccard + 0.45 * themeOverlap;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
        var union = a.Union(b, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0 : (double)inter / union;
    }

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "to", "of", "for", "in", "on", "at", "is", "are", "be",
        "with", "from", "that", "this", "any", "all", "each", "every", "must", "not", "no",
        "so", "as", "by", "into", "than", "then", "when", "if", "do", "does", "did", "can",
        "should", "will", "their", "its", "it", "they", "we", "you", "your", "our",
    };

    internal static HashSet<string> SignificantTokens(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(text ?? "", @"[a-zA-Z]{3,}"))
        {
            var w = m.Value.ToLowerInvariant();
            if (Stop.Contains(w)) continue;
            set.Add(w);
        }
        return set;
    }

    /// <summary>Coarse theme tags for merge (photoreal, cast count, identity, …).</summary>
    internal static HashSet<string> InferThemes(string text)
    {
        var t = (text ?? "").ToLowerInvariant();
        var themes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Regex.IsMatch(t, @"\b(photoreal|live-?action|period|illustrated|anime|cartoon|painted|cgi|medium|style lock|render)\b"))
            themes.Add("style");
        if (Regex.IsMatch(t, @"\b(cast count|extras|background|crowd|unlisted|named identit)\b"))
            themes.Add("cast_count");
        if (Regex.IsMatch(t, @"\b(identity|face|wardrobe|grooming|visual_?lock|character|narrator|swap|drift)\b"))
            themes.Add("identity");
        if (Regex.IsMatch(t, @"\b(silent|no-dialogue|closed mouth|shout|speaking|agitation|audio|parity)\b"))
            themes.Add("silent_audio");
        if (Regex.IsMatch(t, @"\b(eye|gaze|eye-line|downcast|expression|skin|vampiric)\b"))
            themes.Add("face_expression");
        if (Regex.IsMatch(t, @"\b(prompt|truncat|complete|ungarbled|who acts|who speaks)\b"))
            themes.Add("prompt_quality");
        if (Regex.IsMatch(t, @"\b(auto-?review|fail any clip|verifier)\b"))
            themes.Add("auto_review");
        if (Regex.IsMatch(t, @"\b(negative|ban|forbid|constraint)\b") && themes.Contains("style"))
            themes.Add("style_negative");

        return themes;
    }

    private static bool IsDoneOrReviewed(ProposalChecklistItem i) =>
        i.Reviewed
        || !string.IsNullOrWhiteSpace(i.Disposition)
        || string.Equals(i.Status, "reviewed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(i.Status, "deferred", StringComparison.OrdinalIgnoreCase);

    private static string ResolveStatus(ProposalChecklistItem match)
    {
        if (!string.IsNullOrWhiteSpace(match.Disposition))
            return match.Status is "pending" or "" ? "reviewed" : match.Status;
        if (match.Reviewed)
            return match.Status is "pending" or "" ? "reviewed" : match.Status;
        return "pending";
    }

    private static ProposalChecklistItem CloneWith(
        ProposalChecklistItem src,
        string text,
        bool reviewed,
        string status) =>
        new()
        {
            Id = src.Id,
            Text = text,
            Reviewed = reviewed,
            Status = status,
            Disposition = src.Disposition,
            Note = src.Note,
            ReviewedAt = src.ReviewedAt,
        };

    private static List<ProposalChecklistItem> DedupById(List<ProposalChecklistItem> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ProposalChecklistItem>();
        foreach (var i in items)
        {
            if (!seen.Add(i.Id)) continue;
            list.Add(i);
        }
        return list;
    }

    private ProposalChecklistDocument LoadUnlockedNoSeedWrite()
    {
        var path = ChecklistPath;
        if (!File.Exists(path))
            return new ProposalChecklistDocument();
        try
        {
            return JsonSerializer.Deserialize<ProposalChecklistDocument>(
                File.ReadAllText(path), JsonOpts) ?? new ProposalChecklistDocument();
        }
        catch
        {
            return new ProposalChecklistDocument();
        }
    }

    private static string NormalizeKey(string text) =>
        Regex.Replace((text ?? "").Trim().ToLowerInvariant(), @"\s+", " ");

    private static string ShortId(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeKey(text)));
        return Convert.ToHexString(hash)[..10].ToLowerInvariant();
    }
}
