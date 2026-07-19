using System.Text.Json;
using FilmStudio.Core.Models;
using Microsoft.Extensions.Logging;

namespace FilmStudio.Engine;

/// <summary>
/// Host-level append-only review/learning events (<c>{WorkspaceRoot}/_learning/review_events.jsonl</c>).
/// Project edit log remains the per-project audit; this store powers admin insights across films.
/// </summary>
public sealed class ReviewEventStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ProjectStore _projects;
    private readonly ILogger<ReviewEventStore> _log;
    private readonly object _writeLock = new();

    public ReviewEventStore(ProjectStore projects, ILogger<ReviewEventStore> log)
    {
        _projects = projects;
        _log = log;
    }

    public string LearningDir =>
        Path.Combine(_projects.WorkspaceRoot, "_learning");

    public string EventsPath =>
        Path.Combine(LearningDir, "review_events.jsonl");

    public ReviewLearningEvent Append(ReviewLearningEvent ev)
    {
        if (string.IsNullOrWhiteSpace(ev.Id))
            ev.Id = Guid.NewGuid().ToString("N")[..12];
        if (ev.Ts == default)
            ev.Ts = DateTimeOffset.UtcNow;

        try
        {
            Directory.CreateDirectory(LearningDir);
            var line = JsonSerializer.Serialize(ev, JsonOpts);
            lock (_writeLock)
            {
                File.AppendAllText(EventsPath, line + "\n");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to append learning event {Type} for {Project}",
                ev.Type, ev.ProjectId);
        }

        return ev;
    }

    public ReviewLearningEvent AppendFromEditLog(
        string projectId,
        EditLogEntry entry,
        string? userId = null,
        string? category = null,
        string? suggestion = null,
        string? confidence = null,
        string? continuity = null,
        int? suggestionCount = null,
        string? field = null,
        string? jobId = null,
        string? outcome = null)
    {
        DateTimeOffset ts = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(entry.Ts) &&
            DateTimeOffset.TryParse(entry.Ts, out var parsed))
            ts = parsed.ToUniversalTime();

        return Append(new ReviewLearningEvent
        {
            Id = entry.Id,
            Ts = ts,
            ProjectId = projectId,
            UserId = userId,
            Type = entry.Type,
            Scene = entry.Scene,
            Clip = entry.Clip,
            Character = entry.Character,
            Note = entry.UserNote,
            ActionTaken = entry.ActionTaken,
            Before = entry.Before,
            After = entry.After,
            LearningLayer = entry.LearningLayer,
            Category = category,
            Suggestion = suggestion,
            Confidence = confidence,
            Continuity = continuity,
            SuggestionCount = suggestionCount,
            Field = field,
            JobId = jobId,
            Outcome = outcome,
        });
    }

    /// <summary>Read recent events (newest first). Optional filters.</summary>
    public IReadOnlyList<ReviewLearningEvent> Query(
        string? projectId = null,
        string? type = null,
        string? category = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int take = 200)
    {
        take = Math.Clamp(take, 1, 5000);
        var all = ReadAll();
        IEnumerable<ReviewLearningEvent> q = all;
        if (!string.IsNullOrWhiteSpace(projectId))
            q = q.Where(e => string.Equals(e.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(type))
            q = q.Where(e => string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(category))
            q = q.Where(e => string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase));
        if (from is { } f)
            q = q.Where(e => e.Ts >= f);
        if (to is { } t)
            q = q.Where(e => e.Ts <= t);
        return q.OrderByDescending(e => e.Ts).Take(take).ToList();
    }

    public LearningInsightsDto BuildInsights(
        string? projectId = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int recentTake = 40)
    {
        var events = Query(projectId, from: from, to: to, take: 5000);
        var dto = new LearningInsightsDto
        {
            EventCount = events.Count,
            From = from ?? events.LastOrDefault()?.Ts,
            To = to ?? events.FirstOrDefault()?.Ts,
            Recent = events.Take(Math.Clamp(recentTake, 5, 200)).ToList(),
        };

        foreach (var e in events)
        {
            Bump(dto.ByType, e.Type);
            if (!string.IsNullOrWhiteSpace(e.Category))
                Bump(dto.ByCategory, e.Category!);

            if (string.Equals(e.Type, "clip_fail", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(e.Type, "auto_review", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(e.Suggestion, "fail", StringComparison.OrdinalIgnoreCase)))
            {
                dto.HumanFail += string.Equals(e.Type, "clip_fail", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                var cat = string.IsNullOrWhiteSpace(e.Category) ? "other" : e.Category!;
                if (string.Equals(e.Type, "clip_fail", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.Suggestion, "fail", StringComparison.OrdinalIgnoreCase))
                    Bump(dto.FailByCategory, cat);
            }

            if (string.Equals(e.Type, "clip_pass", StringComparison.OrdinalIgnoreCase))
                dto.HumanPass++;
            if (string.Equals(e.Type, "auto_review", StringComparison.OrdinalIgnoreCase))
                dto.AutoReview++;
            if (string.Equals(e.Type, "auto_review_apply", StringComparison.OrdinalIgnoreCase))
                dto.ApplyCount++;
            if (string.Equals(e.Type, "regen_after_review", StringComparison.OrdinalIgnoreCase))
                dto.RegenCount++;
        }

        // auto_review fails counted above only when suggestion=fail; ensure human fail tally correct
        dto.HumanFail = events.Count(e =>
            string.Equals(e.Type, "clip_fail", StringComparison.OrdinalIgnoreCase));

        return dto;
    }

    public IReadOnlyList<ReviewLearningEvent> ReadAll()
    {
        var path = EventsPath;
        if (!File.Exists(path))
            return Array.Empty<ReviewLearningEvent>();

        var list = new List<ReviewLearningEvent>();
        try
        {
            string[] lines;
            lock (_writeLock)
                lines = File.ReadAllLines(path);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var ev = JsonSerializer.Deserialize<ReviewLearningEvent>(line, JsonOpts);
                    if (ev is not null) list.Add(ev);
                }
                catch
                {
                    /* skip bad line */
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed reading learning events");
        }

        return list;
    }

    private static void Bump(Dictionary<string, int> map, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) key = "unknown";
        map.TryGetValue(key, out var n);
        map[key] = n + 1;
    }
}
