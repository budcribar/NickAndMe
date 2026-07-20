using System.Text.Json;
using FilmStudio.Core.Models;
using Microsoft.Extensions.Logging;

namespace FilmStudio.Engine;

/// <summary>
/// Per-project house rules (<c>project_rules.json</c>) + pending suggestions from repeated fail categories.
/// </summary>
public sealed class ProjectRulesService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Minimum fails in one category before auto-suggest.</summary>
    public const int DefaultMinFailsForSuggest = 3;

    private readonly ProjectStore _projects;
    private readonly ReviewEventStore _learning;
    private readonly ILogger<ProjectRulesService> _log;

    public ProjectRulesService(
        ProjectStore projects,
        ReviewEventStore learning,
        ILogger<ProjectRulesService> log)
    {
        _projects = projects;
        _learning = learning;
        _log = log;
    }

    public string RulesPath(string projectId) =>
        Path.Combine(_projects.GetProjectDir(projectId), "project_rules.json");

    public ProjectRulesDocument Load(string projectId)
    {
        var path = RulesPath(projectId);
        if (!File.Exists(path))
            return new ProjectRulesDocument();
        try
        {
            return JsonSerializer.Deserialize<ProjectRulesDocument>(File.ReadAllText(path), JsonOpts)
                   ?? new ProjectRulesDocument();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed loading project rules for {Project}", projectId);
            return new ProjectRulesDocument();
        }
    }

    public void Save(string projectId, ProjectRulesDocument doc)
    {
        var path = RulesPath(projectId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOpts) + "\n");
    }

    /// <summary>Stable id for auto style rule written from cast extract / render_style_lock.</summary>
    public const string StyleRuleId = "style_from_cast";
    /// <summary>Stable id for auto performance/address rule from cast extract.</summary>
    public const string PerformanceRuleId = "performance_from_cast";

    /// <summary>Active rules as text block for prompt injection.</summary>
    public string GetActiveRulesBlock(string projectId)
    {
        var doc = Load(projectId);
        var lines = doc.Active
            .Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .Select(r => $"- [{(string.IsNullOrWhiteSpace(r.Category) ? "other" : r.Category!.Trim())}] {r.Text!.Trim()}")
            .ToList();

        // Fallback: cast_seeds locks if no matching rules yet (gen/auto-review still see them)
        if (!doc.Active.Any(r =>
                string.Equals(r.Category, "style", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.Id, StyleRuleId, StringComparison.OrdinalIgnoreCase)))
        {
            var fromCast = TryReadCastField(projectId, "render_style_lock");
            if (!string.IsNullOrWhiteSpace(fromCast))
                lines.Add($"- [style] {NormalizeStyleRuleText(fromCast)}");
        }

        if (!doc.Active.Any(r =>
                string.Equals(r.Category, "performance", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.Id, PerformanceRuleId, StringComparison.OrdinalIgnoreCase)))
        {
            var perf = TryReadCastField(projectId, "performance_lock");
            if (!string.IsNullOrWhiteSpace(perf))
                lines.Add($"- [performance] {NormalizePerformanceRuleText(perf)}");
        }

        if (lines.Count == 0) return "";
        return "PROJECT HOUSE RULES (approved):\n" + string.Join("\n", lines);
    }

    /// <summary>
    /// Upsert an active style rule from cast extract <c>render_style_lock</c> (book/screenplay medium).
    /// Does not overwrite a user-approved style rule (different id / non-system approver).
    /// </summary>
    public bool EnsureStyleRuleFromRenderLock(
        string projectId,
        string? renderStyleLock,
        string approvedBy = "cast_extract")
    {
        var text = NormalizeStyleRuleText(renderStyleLock);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var doc = Load(projectId);
        var systemOwned = doc.Active.FirstOrDefault(r =>
            string.Equals(r.Id, StyleRuleId, StringComparison.OrdinalIgnoreCase));
        if (systemOwned is not null)
        {
            if (string.Equals(systemOwned.Text?.Trim(), text, StringComparison.OrdinalIgnoreCase))
                return false;
            systemOwned.Text = text;
            systemOwned.Category = "style";
            systemOwned.ApprovedAt = DateTimeOffset.UtcNow;
            systemOwned.ApprovedBy = approvedBy;
            Save(projectId, doc);
            return true;
        }

        // User already has an active style rule they approved — leave it
        var userStyle = doc.Active.FirstOrDefault(r =>
            string.Equals(r.Category, "style", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(r.Id, StyleRuleId, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(r.ApprovedBy, "cast_extract", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(r.ApprovedBy, "system", StringComparison.OrdinalIgnoreCase));
        if (userStyle is not null)
            return false;

        // Remove any other auto style duplicates, then add
        doc.Active.RemoveAll(r =>
            string.Equals(r.Category, "style", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(r.ApprovedBy, "cast_extract", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(r.ApprovedBy, "system", StringComparison.OrdinalIgnoreCase)));

        doc.Active.Add(new ProjectRule
        {
            Id = StyleRuleId,
            Text = text,
            Category = "style",
            ApprovedAt = DateTimeOffset.UtcNow,
            ApprovedBy = approvedBy,
            SourceFailCount = 0,
        });
        Save(projectId, doc);
        return true;
    }

    public static string NormalizeStyleRuleText(string? renderStyleLock)
    {
        var t = (renderStyleLock ?? "").Trim();
        if (t.Length == 0) return "";
        // Ensure readable house-rule form
        if (!t.Contains("STYLE", StringComparison.OrdinalIgnoreCase) &&
            !t.Contains("picture", StringComparison.OrdinalIgnoreCase) &&
            !t.Contains("photoreal", StringComparison.OrdinalIgnoreCase) &&
            !t.Contains("live-action", StringComparison.OrdinalIgnoreCase) &&
            !t.Contains("CGI", StringComparison.OrdinalIgnoreCase) &&
            !t.Contains("animated", StringComparison.OrdinalIgnoreCase))
        {
            t = "Hold this film’s render medium consistently: " + t;
        }
        if (t.Length > 600)
            t = t[..597].TrimEnd() + "…";
        return t;
    }

    /// <summary>
    /// Upsert performance/address convention from cast extract (book-inferred, not a fixed eye recipe).
    /// </summary>
    public bool EnsurePerformanceRuleFromLock(
        string projectId,
        string? performanceLock,
        string approvedBy = "cast_extract")
    {
        var text = NormalizePerformanceRuleText(performanceLock);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var doc = Load(projectId);
        var systemOwned = doc.Active.FirstOrDefault(r =>
            string.Equals(r.Id, PerformanceRuleId, StringComparison.OrdinalIgnoreCase));
        if (systemOwned is not null)
        {
            if (string.Equals(systemOwned.Text?.Trim(), text, StringComparison.OrdinalIgnoreCase))
                return false;
            systemOwned.Text = text;
            systemOwned.Category = "performance";
            systemOwned.ApprovedAt = DateTimeOffset.UtcNow;
            systemOwned.ApprovedBy = approvedBy;
            Save(projectId, doc);
            return true;
        }

        var userOwned = doc.Active.FirstOrDefault(r =>
            string.Equals(r.Category, "performance", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(r.Id, PerformanceRuleId, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(r.ApprovedBy, "cast_extract", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(r.ApprovedBy, "system", StringComparison.OrdinalIgnoreCase));
        if (userOwned is not null)
            return false;

        doc.Active.RemoveAll(r =>
            string.Equals(r.Category, "performance", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(r.ApprovedBy, "cast_extract", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(r.ApprovedBy, "system", StringComparison.OrdinalIgnoreCase)));

        doc.Active.Add(new ProjectRule
        {
            Id = PerformanceRuleId,
            Text = text,
            Category = "performance",
            ApprovedAt = DateTimeOffset.UtcNow,
            ApprovedBy = approvedBy,
            SourceFailCount = 0,
        });
        Save(projectId, doc);
        return true;
    }

    public static string NormalizePerformanceRuleText(string? performanceLock)
    {
        var t = (performanceLock ?? "").Trim();
        if (t.Length == 0) return "";
        if (!t.Contains("PERFORMANCE", StringComparison.OrdinalIgnoreCase) &&
            !t.Contains("address", StringComparison.OrdinalIgnoreCase) &&
            !t.Contains("viewer", StringComparison.OrdinalIgnoreCase) &&
            !t.Contains("camera", StringComparison.OrdinalIgnoreCase) &&
            !t.Contains("confessional", StringComparison.OrdinalIgnoreCase) &&
            !t.Contains("observ", StringComparison.OrdinalIgnoreCase))
        {
            t = "PERFORMANCE LOCK: " + t;
        }
        if (t.Length > 700)
            t = t[..697].TrimEnd() + "…";
        return t;
    }

    private string? TryReadCastField(string projectId, string propertyName)
    {
        try
        {
            var path = ScreenplayService.GetCastSeedsPath(_projects, projectId);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty(propertyName, out var el) &&
                el.ValueKind == JsonValueKind.String)
                return el.GetString();
        }
        catch
        {
            /* ignore */
        }
        return null;
    }

    /// <summary>
    /// Scan host learning events for this project; add pending suggestions for hot fail categories.
    /// Does not auto-activate.
    /// </summary>
    public ProjectRulesDocument SuggestFromFails(
        string projectId,
        int minFails = DefaultMinFailsForSuggest)
    {
        minFails = Math.Max(2, minFails);
        var doc = Load(projectId);
        var fails = _learning.Query(projectId: projectId, take: 2000)
            .Where(e =>
                string.Equals(e.Type, "clip_fail", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(e.Type, "auto_review", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(e.Suggestion, "fail", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var byCat = fails
            .GroupBy(e => string.IsNullOrWhiteSpace(e.Category) ? "other" : e.Category!.Trim().ToLowerInvariant())
            .Select(g => new
            {
                Category = g.Key,
                Count = g.Count(),
                Notes = g.Select(x => x.Note)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Take(5)
                    .ToList(),
            })
            .Where(x => x.Count >= minFails)
            .ToList();

        var activeTexts = new HashSet<string>(
            doc.Active.Select(a => (a.Text ?? "").Trim()).Where(t => t.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var pendingTexts = new HashSet<string>(
            doc.Pending.Select(p => (p.Text ?? "").Trim()).Where(t => t.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var activeCategories = new HashSet<string>(
            doc.Active.Select(a => (a.Category ?? "other").Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var g in byCat)
        {
            var text = BuildRuleText(g.Category, g.Notes);
            if (activeTexts.Contains(text) || pendingTexts.Contains(text))
                continue;
            // Skip if this category already has an active or pending rule
            if (activeCategories.Contains(g.Category) ||
                doc.Pending.Any(p => string.Equals(p.Category, g.Category, StringComparison.OrdinalIgnoreCase)))
                continue;

            doc.Pending.Add(new ProjectRuleSuggestion
            {
                Id = Guid.NewGuid().ToString("N")[..10],
                Category = g.Category,
                FailCount = g.Count,
                Text = text,
                Rationale = $"Seen {g.Count} fails tagged {g.Category}.",
                SuggestedAt = DateTimeOffset.UtcNow,
            });
            pendingTexts.Add(text);
        }

        Save(projectId, doc);
        return doc;
    }

    public ProjectRulesDocument Approve(
        string projectId,
        string suggestionId,
        string? textOverride,
        string? approvedBy)
    {
        if (string.IsNullOrWhiteSpace(suggestionId))
            throw new ArgumentException("suggestionId required", nameof(suggestionId));
        var doc = Load(projectId);
        var sug = doc.Pending.FirstOrDefault(p =>
            string.Equals(p.Id, suggestionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown suggestion: {suggestionId}");

        var text = !string.IsNullOrWhiteSpace(textOverride)
            ? textOverride.Trim()
            : (sug.Text ?? "").Trim();
        if (text.Length == 0)
            throw new InvalidOperationException("Rule text cannot be empty.");
        doc.Pending.RemoveAll(p => string.Equals(p.Id, suggestionId, StringComparison.OrdinalIgnoreCase));
        doc.Active.Add(new ProjectRule
        {
            Id = Guid.NewGuid().ToString("N")[..10],
            Text = text,
            Category = string.IsNullOrWhiteSpace(sug.Category) ? "other" : sug.Category.Trim(),
            ApprovedAt = DateTimeOffset.UtcNow,
            ApprovedBy = approvedBy,
            SourceFailCount = sug.FailCount,
        });
        Save(projectId, doc);
        return doc;
    }

    public ProjectRulesDocument Reject(string projectId, string suggestionId)
    {
        if (string.IsNullOrWhiteSpace(suggestionId))
            throw new ArgumentException("suggestionId required", nameof(suggestionId));
        var doc = Load(projectId);
        doc.Pending.RemoveAll(p => string.Equals(p.Id, suggestionId, StringComparison.OrdinalIgnoreCase));
        Save(projectId, doc);
        return doc;
    }

    private static string BuildRuleText(string category, List<string> notes)
    {
        var sample = notes.FirstOrDefault(n => n is { Length: > 8 });
        return category switch
        {
            "wrong_voice" => "Keep each character's voice consistent with their voice_profile (gender, pitch, age).",
            "wrong_look" => "Match locked character appearance and visual_lock on every clip; no identity drift.",
            "wrong_style" or "style" =>
                "Hold the project render medium on every clip (picture-book CG vs photoreal, etc.); no medium drift mid-film.",
            "continuity" => "When continuing from previous clip, match wardrobe, place, and pose from the last frames.",
            "silent" => "Dialogue clips must have clear audible speech and lip sync for the speaker.",
            "framing" => "Follow planned framing/action in visual_prompt; avoid empty holds and wrong shots.",
            _ => string.IsNullOrWhiteSpace(sample)
                ? $"Address repeated review fails in category '{category}'."
                : $"Address '{category}' issues (e.g. {Trim(sample, 120)}).",
        };
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];
}
