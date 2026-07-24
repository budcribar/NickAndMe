namespace PageToMovie.Core.Models;

/// <summary>
/// Host-level learning event (append-only). Mirrors project edit log + richer fields for admin insights.
/// </summary>
public sealed class ReviewLearningEvent
{
    public string Id { get; set; } = "";
    public DateTimeOffset Ts { get; set; } = DateTimeOffset.UtcNow;
    public string ProjectId { get; set; } = "";
    public string? UserId { get; set; }
    /// <summary>clip_pass | clip_fail | auto_review | auto_review_apply | regen_after_review | scene_approve | …</summary>
    public string Type { get; set; } = "";
    public int? Scene { get; set; }
    public int? Clip { get; set; }
    public string? Character { get; set; }
    public string Note { get; set; } = "";
    public string ActionTaken { get; set; } = "";
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public string? Category { get; set; }
    public string? Suggestion { get; set; }
    public string? Confidence { get; set; }
    public string? Continuity { get; set; }
    public string? Outcome { get; set; }
    public string LearningLayer { get; set; } = "clip";
    public string? JobId { get; set; }
    public string? Field { get; set; }
    public int? SuggestionCount { get; set; }
}

public sealed class LearningInsightsDto
{
    public bool Ok { get; set; } = true;
    public int EventCount { get; set; }
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    public Dictionary<string, int> ByType { get; set; } = new();
    public Dictionary<string, int> ByCategory { get; set; } = new();
    public Dictionary<string, int> FailByCategory { get; set; } = new();
    public List<ReviewLearningEvent> Recent { get; set; } = new();
    public int HumanFail { get; set; }
    public int HumanPass { get; set; }
    public int AutoReview { get; set; }
    public int ApplyCount { get; set; }
    public int RegenCount { get; set; }
}

public sealed class PromptPackInfo
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = ""; // gen | auto_review | shared
    public string Version { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public bool Active { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? Notes { get; set; }
}

public sealed class PromptPackManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string ActiveGenPackId { get; set; } = "gen-v1";
    public string ActiveAutoReviewPackId { get; set; } = "auto_review-v1";
    public List<PromptPackInfo> Packs { get; set; } = new();
}

public sealed class ActivatePromptPackRequest
{
    public string PackId { get; set; } = "";
}

public sealed class CreatePromptPackBody
{
    public string? Kind { get; set; }
    public string? Version { get; set; }
    public string? Body { get; set; }
    public string? Notes { get; set; }
}

public sealed class ProposeLearningRulesRequest
{
    public int LastNFails { get; set; } = 50;
    public string? ProjectId { get; set; }
    public string? Category { get; set; }
}

public sealed class ProposeLearningRulesResult
{
    public bool Ok { get; set; } = true;
    public string Proposal { get; set; } = "";
    public int FailEventsUsed { get; set; }
    public List<string> Categories { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>
/// Admin checklist: proposed AI rules with reviewed check-off (host <c>_learning/proposal_checklist.json</c>).
/// </summary>
public sealed class ProposalChecklistDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string? SourceLabel { get; set; }
    public string? RawProposal { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ProposalChecklistItem> Items { get; set; } = new();
}

public sealed class ProposalChecklistItem
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>pending | reviewed | deferred</summary>
    public string Status { get; set; } = "pending";
    public bool Reviewed { get; set; }
    /// <summary>Optional: accepted | rejected | partial | n/a</summary>
    public string? Disposition { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
}

public sealed class ProposalChecklistUpsertRequest
{
    /// <summary>Replace raw proposal text and re-parse bullets (optional).</summary>
    public string? RawProposal { get; set; }
    public string? SourceLabel { get; set; }
    /// <summary>When set, merge/replace checklist items.</summary>
    public List<ProposalChecklistItem>? Items { get; set; }
}

public sealed class ProposalChecklistToggleRequest
{
    public string Id { get; set; } = "";
    public bool Reviewed { get; set; }
    public string? Disposition { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// Mark checklist items accepted when project rules are approved (theme/text match).
/// </summary>
public sealed class ProposalChecklistAcceptMatchingRequest
{
    public List<string> Texts { get; set; } = new();
    public string Disposition { get; set; } = "accepted";
    public string? Note { get; set; }
}

/// <summary>Project-scoped house rules from repeated review fails.</summary>
public sealed class ProjectRulesDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<ProjectRule> Active { get; set; } = new();
    public List<ProjectRuleSuggestion> Pending { get; set; } = new();
}

public sealed class ProjectRule
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string Category { get; set; } = "other";
    public DateTimeOffset ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public int SourceFailCount { get; set; }
}

public sealed class ProjectRuleSuggestion
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string Category { get; set; } = "other";
    public int FailCount { get; set; }
    public DateTimeOffset SuggestedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Rationale { get; set; } = "";
}

public sealed class ApproveProjectRuleRequest
{
    public string SuggestionId { get; set; } = "";
    /// <summary>Optional edit before approve.</summary>
    public string? Text { get; set; }
}

public sealed class RejectProjectRuleRequest
{
    public string SuggestionId { get; set; } = "";
}
