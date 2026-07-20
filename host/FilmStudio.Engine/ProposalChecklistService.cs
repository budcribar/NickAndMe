using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FilmStudio.Core.Models;
using Microsoft.Extensions.Logging;

namespace FilmStudio.Engine;

/// <summary>
/// Persist admin review of AI-proposed house rules (check-off list).
/// Path: <c>{WorkspaceRoot}/_learning/proposal_checklist.json</c>.
/// </summary>
public sealed class ProposalChecklistService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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
    /// After Propose-from-fails: parse bullets into checklist, keep prior Reviewed flags by text match.
    /// </summary>
    public ProposalChecklistDocument IngestProposal(string proposal, string? sourceLabel = null)
    {
        var bullets = ParseBullets(proposal);
        if (bullets.Count == 0 && !string.IsNullOrWhiteSpace(proposal))
        {
            // Whole block as one item if not bulleted
            bullets.Add(proposal.Trim());
        }

        var prev = Load();
        var byText = prev.Items
            .GroupBy(i => NormalizeKey(i.Text), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var items = new List<ProposalChecklistItem>();
        foreach (var b in bullets)
        {
            var key = NormalizeKey(b);
            if (byText.TryGetValue(key, out var old))
            {
                items.Add(new ProposalChecklistItem
                {
                    Id = old.Id,
                    Text = b,
                    Reviewed = old.Reviewed,
                    Status = old.Reviewed ? (old.Status is "pending" or "" ? "reviewed" : old.Status) : "pending",
                    Disposition = old.Disposition,
                    Note = old.Note,
                    ReviewedAt = old.ReviewedAt,
                });
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

        var doc = new ProposalChecklistDocument
        {
            SourceLabel = sourceLabel ?? "propose_from_fails",
            RawProposal = proposal,
            Items = items,
        };
        return Save(doc);
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

        var doc = Load();
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
        return Save(doc);
    }

    public static List<string> ParseBullets(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 8) continue;
            // "- rule" / "* rule" / "• rule" / "1. rule"
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

    private static string NormalizeKey(string text) =>
        Regex.Replace((text ?? "").Trim().ToLowerInvariant(), @"\s+", " ");

    private static string ShortId(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeKey(text)));
        return Convert.ToHexString(hash)[..10].ToLowerInvariant();
    }
}
