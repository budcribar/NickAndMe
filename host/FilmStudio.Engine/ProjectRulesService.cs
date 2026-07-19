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

    /// <summary>Active rules as text block for prompt injection.</summary>
    public string GetActiveRulesBlock(string projectId)
    {
        var doc = Load(projectId);
        if (doc.Active.Count == 0) return "";
        var lines = doc.Active.Select(r => $"- [{r.Category}] {r.Text}");
        return "PROJECT HOUSE RULES (approved):\n" + string.Join("\n", lines);
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
            .Select(g => new { Category = g.Key, Count = g.Count(), Notes = g.Select(x => x.Note).Where(n => n.Length > 0).Take(5).ToList() })
            .Where(x => x.Count >= minFails)
            .ToList();

        var activeTexts = new HashSet<string>(
            doc.Active.Select(a => a.Text.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var pendingTexts = new HashSet<string>(
            doc.Pending.Select(p => p.Text.Trim()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var g in byCat)
        {
            var text = BuildRuleText(g.Category, g.Notes);
            if (activeTexts.Contains(text) || pendingTexts.Contains(text))
                continue;
            // Also skip if same category already has pending
            if (doc.Pending.Any(p => string.Equals(p.Category, g.Category, StringComparison.OrdinalIgnoreCase)))
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
        var doc = Load(projectId);
        var sug = doc.Pending.FirstOrDefault(p =>
            string.Equals(p.Id, suggestionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown suggestion: {suggestionId}");

        var text = !string.IsNullOrWhiteSpace(textOverride) ? textOverride.Trim() : sug.Text.Trim();
        doc.Pending.RemoveAll(p => string.Equals(p.Id, suggestionId, StringComparison.OrdinalIgnoreCase));
        doc.Active.Add(new ProjectRule
        {
            Id = Guid.NewGuid().ToString("N")[..10],
            Text = text,
            Category = sug.Category,
            ApprovedAt = DateTimeOffset.UtcNow,
            ApprovedBy = approvedBy,
            SourceFailCount = sug.FailCount,
        });
        Save(projectId, doc);
        return doc;
    }

    public ProjectRulesDocument Reject(string projectId, string suggestionId)
    {
        var doc = Load(projectId);
        doc.Pending.RemoveAll(p => string.Equals(p.Id, suggestionId, StringComparison.OrdinalIgnoreCase));
        Save(projectId, doc);
        return doc;
    }

    private static string BuildRuleText(string category, List<string> notes)
    {
        var sample = notes.FirstOrDefault(n => n.Length > 8);
        return category switch
        {
            "wrong_voice" => "Keep each character's voice consistent with their voice_profile (gender, pitch, age).",
            "wrong_look" => "Match locked character appearance and visual_lock on every clip; no identity drift.",
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
