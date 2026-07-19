using System.Text;
using FilmStudio.Core.Models;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace FilmStudio.Engine;

/// <summary>Admin-gated: propose prompt/rule text from recent fail events (chat).</summary>
public sealed class LearningProposalService
{
    private readonly ReviewEventStore _learning;
    private readonly IGrokChatClient _chat;
    private readonly ILogger<LearningProposalService> _log;

    public LearningProposalService(
        ReviewEventStore learning,
        IGrokChatClient chat,
        ILogger<LearningProposalService> log)
    {
        _learning = learning;
        _chat = chat;
        _log = log;
    }

    public async Task<ProposeLearningRulesResult> ProposeAsync(
        ProposeLearningRulesRequest req,
        CancellationToken ct = default)
    {
        var n = Math.Clamp(req.LastNFails <= 0 ? 50 : req.LastNFails, 5, 200);
        // Scan full log then filter fails — Query(take:N) of mixed events can bury fails under passes
        var fails = _learning.ReadAll()
            .Where(e =>
                string.IsNullOrWhiteSpace(req.ProjectId) ||
                string.Equals(e.ProjectId, req.ProjectId, StringComparison.OrdinalIgnoreCase))
            .Where(e =>
                string.Equals(e.Type, "clip_fail", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(e.Type, "auto_review", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(e.Suggestion, "fail", StringComparison.OrdinalIgnoreCase)))
            .Where(e => string.IsNullOrWhiteSpace(req.Category) ||
                        string.Equals(e.Category, req.Category, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Ts)
            .Take(n)
            .ToList();

        if (fails.Count == 0)
        {
            return new ProposeLearningRulesResult
            {
                Ok = false,
                Error = "No fail events found for the filters.",
                FailEventsUsed = 0,
            };
        }

        var cats = fails
            .Select(f => string.IsNullOrWhiteSpace(f.Category) ? "other" : f.Category!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Recent film QC fails (newest first):");
        var i = 0;
        foreach (var f in fails)
        {
            i++;
            sb.AppendLine(
                $"{i}. [{f.Type}] project={f.ProjectId} S{f.Scene:D2}C{f.Clip:D2} " +
                $"cat={f.Category ?? "?"} note={Trim(f.Note, 200)}");
            if (!string.IsNullOrWhiteSpace(f.Before) || !string.IsNullOrWhiteSpace(f.After))
                sb.AppendLine($"   before/after present: beforeLen={f.Before?.Length ?? 0} afterLen={f.After?.Length ?? 0}");
        }

        var system =
            "You help improve a film generation pipeline. From QC fail notes, propose 3–7 concise " +
            "house rules for video prompt construction (and auto-review checks). " +
            "Output plain text bullet list only. No markdown fences. Each bullet one sentence. " +
            "Do not invent book-specific plot; keep rules general and actionable.";

        if (!_chat.IsConfigured)
        {
            // Deterministic offline proposal for tests / no key
            var offline = string.Join("\n", cats.Select(c =>
                $"- Strengthen checks and gen guidance for category '{c}' based on {fails.Count} recent fails."));
            return new ProposeLearningRulesResult
            {
                Ok = true,
                Proposal = offline + "\n- Prefer continuity from previous clip tail; flag jumps as fail when clear.",
                FailEventsUsed = fails.Count,
                Categories = cats,
            };
        }

        try
        {
            var proposal = await _chat.CompleteAsync(system, sb.ToString(), model: "grok-4.5", temperature: 0.3, ct)
                .ConfigureAwait(false);
            return new ProposeLearningRulesResult
            {
                Ok = true,
                Proposal = proposal.Trim(),
                FailEventsUsed = fails.Count,
                Categories = cats,
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Propose learning rules failed");
            return new ProposeLearningRulesResult
            {
                Ok = false,
                Error = ex.Message,
                FailEventsUsed = fails.Count,
                Categories = cats,
            };
        }
    }

    private static string Trim(string? s, int n)
    {
        s ??= "";
        return s.Length <= n ? s : s[..n];
    }
}
