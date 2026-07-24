namespace FilmStudio.Core.Models;

public sealed class ProjectInfo
{
    public string Id { get; set; } = "";
    public string? Label { get; set; }
    public string? Title { get; set; }
    public string Path { get; set; } = "";
}

public sealed class WorkspaceState
{
    public string? ActiveProject { get; set; }
}

public sealed class JobSnapshot
{
    /// <summary>Multi-job id. Empty when idle / not yet assigned.</summary>
    public string? JobId { get; set; }
    public string Status { get; set; } = "idle"; // idle|running|done|error|cancelled
    public string? Kind { get; set; }
    public string? Message { get; set; }
    public string? ProjectId { get; set; }
    public string? UserId { get; set; }
    public string? CharKey { get; set; }
    public int? Scene { get; set; }
    public int? Clip { get; set; }
    public int Index { get; set; }
    public int Total { get; set; }
    public List<string> Log { get; set; } = new();
    public string? Error { get; set; }
    public DateTimeOffset? QueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>Helpers for multi-job lists (Phase F).</summary>
public static class JobListHelpers
{
    /// <summary>Prefer a running job; else most recently finished/queued.</summary>
    public static JobSnapshot? PickPrimary(IReadOnlyList<JobSnapshot>? jobs)
    {
        if (jobs is null || jobs.Count == 0) return null;
        // Prefer active work over finished: running → queued → newest finished.
        // Without "queued", a second generate briefly surfaces the previous done job
        // (Index==Total) and the progress bar looks stuck full.
        var running = jobs
            .Where(j => string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.StartedAt ?? j.QueuedAt)
            .FirstOrDefault();
        if (running is not null) return running;
        var queued = jobs
            .Where(j => string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.QueuedAt)
            .FirstOrDefault();
        if (queued is not null) return queued;
        return jobs
            .OrderByDescending(j => j.FinishedAt ?? j.StartedAt ?? j.QueuedAt)
            .FirstOrDefault();
    }
}

/// <summary>Persisted multi-job record (same fields as snapshot + queue metadata).</summary>
public sealed class JobRecord
{
    public string JobId { get; set; } = "";
    public string Status { get; set; } = "queued"; // queued|running|done|error|cancelled
    public string? Kind { get; set; }
    public string? Message { get; set; }
    public string? ProjectId { get; set; }
    public string? UserId { get; set; }
    public string? CharKey { get; set; }
    public int? Scene { get; set; }
    public int? Clip { get; set; }
    public int Index { get; set; }
    public int Total { get; set; }
    public List<string> Log { get; set; } = new();
    public string? Error { get; set; }
    public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    public JobSnapshot ToSnapshot() => new()
    {
        JobId = JobId,
        Status = Status,
        Kind = Kind,
        Message = Message,
        ProjectId = ProjectId,
        UserId = UserId,
        CharKey = CharKey,
        Scene = Scene,
        Clip = Clip,
        Index = Index,
        Total = Total,
        Log = Log.ToList(),
        Error = Error,
        QueuedAt = QueuedAt,
        StartedAt = StartedAt,
        FinishedAt = FinishedAt,
    };
}

public sealed class StartSceneGenRequest
{
    public string ProjectId { get; set; } = "";
    public int Scene { get; set; }
    /// <summary>When set, only generate this clip within the scene.</summary>
    public int? Clip { get; set; }
    public bool OnlyMissing { get; set; } = true;
    /// <summary>Video resolution for this gen (e.g. 480p / 720p). Empty → project Configuration.</summary>
    public string? Resolution { get; set; }
    /// <summary>
    /// Block video gen until every cast member has an approved voice profile and
    /// (for on-screen roles) a locked ref image. Default true — prevents wasted API spend.
    /// </summary>
    public bool RequireLockedCharacters { get; set; } = true;
    /// <summary>
    /// When true, return 409 immediately if the scene lock is held by another user.
    /// Default false: accept as queued and wait for the lock (Phase 2).
    /// </summary>
    public bool FailIfLocked { get; set; }
}

public sealed class StartBatchGenRequest
{
    public string ProjectId { get; set; } = "";
    public List<int> Scenes { get; set; } = new();
    /// <summary>
    /// Explicit (scene, clip) targets — e.g. a multi-select of specific clips across one or more
    /// scenes. When set and non-empty, this replaces the <see cref="Scenes"/>-derived work list
    /// (every listed clip is force-regenerated, ignoring <see cref="OnlyMissing"/>, matching the
    /// single-clip regen behavior). <see cref="Scenes"/> may be left empty in this mode.
    /// </summary>
    public List<ClipTarget>? Clips { get; set; }
    public bool OnlyMissing { get; set; } = true;
    /// <summary>Video resolution for this gen (e.g. 480p / 720p). Empty → project Configuration.</summary>
    public string? Resolution { get; set; }
    /// <summary>
    /// Block video gen until every cast member has an approved voice profile and
    /// (for on-screen roles) a locked ref image. Default true — prevents wasted API spend.
    /// </summary>
    public bool RequireLockedCharacters { get; set; } = true;
    /// <summary>When true, 409 if any scene lock is held by another user (default wait).</summary>
    public bool FailIfLocked { get; set; }
}

/// <summary>One explicit clip target for a multi-select clip regen batch.</summary>
public sealed class ClipTarget
{
    public int Scene { get; set; }
    public int Clip { get; set; }
}

public sealed class CharacterImageRef
{
    public string FileName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string? Url { get; set; }
    public int? Index { get; set; }
    public bool Exists { get; set; }
}

public sealed class CharacterSummary
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string VisualLock { get; set; } = "";
    public string VoiceProfile { get; set; } = "";
    public string VoiceLabel { get; set; } = "";
    public bool VoiceOnly { get; set; }
    public bool Locked { get; set; }
    public string? RefFileName { get; set; }
    public string? RefUrl { get; set; }
    /// <summary>Locked ref or variant 1 when present — default primary Grok seed.</summary>
    public bool HasPreferred { get; set; }
    public string? PreferredLabel { get; set; }
    public string? PreferredUrl { get; set; }
    public List<string> WardrobeAlways { get; set; } = new();
    public List<string> DesignReferenceImages { get; set; } = new();
    public List<CharacterImageRef> BookRefs { get; set; } = new();
    public List<CharacterImageRef> Variants { get; set; } = new();
    public string? AgeBand { get; set; }
}

/// <summary>
/// Flexible seed policy for portrait generation.
/// <list type="bullet">
/// <item><c>auto</c> — preferred (if any) + up to MaxBookHints book plates (default).</item>
/// <item><c>preferred_only</c> — only preferred lock/best variant.</item>
/// <item><c>book_hints</c> — preferred + all attached book plates (capped by MaxRefs).</item>
/// <item><c>explicit</c> — only paths/indices supplied by the client.</item>
/// <item><c>none</c> — text-only (description / visual_lock).</item>
/// </list>
/// </summary>
public sealed class StartCharacterVariantsRequest
{
    public string ProjectId { get; set; } = "";
    public string CharKey { get; set; } = "";
    /// <summary>0 = auto (1 if locked, else 3).</summary>
    public int Count { get; set; }
    /// <summary>auto | preferred_only | book_hints | explicit | none</summary>
    public string SeedMode { get; set; } = "auto";
    /// <summary>
    /// Cap on image refs sent to the image API (Grok ≤ 3, Gemini ≤ 14).
    /// 0 = use provider default for the active image backend.
    /// </summary>
    public int MaxRefs { get; set; }
    /// <summary>In auto/book_hints, how many book plates after preferred (default 2).</summary>
    public int MaxBookHints { get; set; } = 2;
    /// <summary>Include preferred lock/best as first seed (default true for non-explicit modes).</summary>
    public bool IncludePreferred { get; set; } = true;
    /// <summary>When SeedMode is explicit (or to force-include): book ref indices from CharacterSummary.BookRefs.</summary>
    public List<int> BookRefIndices { get; set; } = new();
    /// <summary>When SeedMode is explicit: existing variant indices 1..3 to use as seeds.</summary>
    public List<int> VariantIndices { get; set; } = new();
    /// <summary>When SeedMode is explicit: also include locked ref if present.</summary>
    public bool IncludeLockedRef { get; set; } = true;
    /// <summary>
    /// Optional full selection order: "pref", "v1".."v3", "b0".."bN".
    /// When non-empty in explicit mode, overrides separate indices and preserves rank.
    /// </summary>
    public List<string> SeedOrderKeys { get; set; } = new();
    /// <summary>Optional description override for this generate (also used when PersistDescription).</summary>
    public string? DescriptionOverride { get; set; }
    /// <summary>Optional visual_lock override for this generate.</summary>
    public string? VisualLockOverride { get; set; }
    /// <summary>Write DescriptionOverride / VisualLockOverride into scenes.json seeds before generate.</summary>
    public bool PersistDescription { get; set; } = true;
}

public sealed class ExtractCastRequest
{
    public string ProjectId { get; set; } = "";
    /// <summary>Rebuild cast even if cast_seeds.json already exists.</summary>
    public bool Force { get; set; } = true;
    public string Model { get; set; } = "grok-4.5";
}

public sealed class AttachCharacterPlatesRequest
{
    public string ProjectId { get; set; } = "";
    /// <summary>
    /// Re-sort even if pipeline_state.character_plates.sorted_by_character is true.
    /// Default false so import/UI can skip a second sort.
    /// </summary>
    public bool Force { get; set; }
    /// <summary>Copy into assets/characters/*_bookref_*.</summary>
    public bool CopyIntoAssets { get; set; } = true;
    /// <summary>Optional single character; empty = all on-screen cast.</summary>
    public string? CharKey { get; set; }
    /// <summary>Use Grok vision to assign pages to cast (default true for job).</summary>
    public bool UseGrok { get; set; } = true;
    /// <summary>Max book images to send to Grok (cost/latency cap).</summary>
    public int MaxImages { get; set; } = 32;
    public string VisionModel { get; set; } = "grok-4.5";
}

public sealed class AttachCharacterPlatesResult
{
    public bool Ok { get; set; }
    public string? Reason { get; set; }
    public int CharactersUpdated { get; set; }
    public int CharactersSkipped { get; set; }
    /// <summary>pipeline_state says plates were already sorted; Attach was a no-op.</summary>
    public bool AlreadySorted { get; set; }
    /// <summary>After this call, character plates are sorted in scenes.json.</summary>
    public bool SortedByCharacter { get; set; }
    public string? SortedAt { get; set; }
    /// <summary>grok_vision | heuristic | heuristic_after_grok_empty | none</summary>
    public string? Method { get; set; }
    public int ImagesClassified { get; set; }
    public int ImagesSkippedText { get; set; }
    public Dictionary<string, List<string>> AttachedByCharacter { get; set; } = new();
}

/// <summary>
/// Snapshot of pipeline_state.character_plates — whether book images were
/// sorted onto character seeds (scenes.json design_reference_images).
/// </summary>
public sealed class CharacterPlatesState
{
    public bool SortedByCharacter { get; set; }
    public string? SortedAt { get; set; }
    public string Source { get; set; } = "scenes.json#character_seed_tokens.design_reference_images";
    public int CharactersUpdated { get; set; }
    /// <summary>grok_vision | heuristic | …</summary>
    public string? Method { get; set; }
}

/// <summary>Active image backend seed limits for Characters UI.</summary>
public sealed class ImageSeedLimits
{
    /// <summary>grok | gemini</summary>
    public string Provider { get; set; } = "grok";
    public string? ImageModel { get; set; }
    /// <summary>Max reference images the active image API accepts per edit.</summary>
    public int MaxReferenceImages { get; set; } = 3;
}

public sealed class StartBookPrepareRequest
{
    public string ProjectId { get; set; } = "";
    public bool ForceExtract { get; set; } = true;
    public bool ForceVision { get; set; }
    public bool AutoVision { get; set; } = true;
    public string VisionModel { get; set; } = "grok-4.5";
}

/// <summary>
/// Import pipeline job: optional book prepare (PDF/OCR) + book→Fountain adapt.
/// Prefer this over sync from-book for long novels.
/// </summary>
public sealed class StartBookImportRequest
{
    public string ProjectId { get; set; } = "";
    /// <summary>When true, skip PDF/OCR and only run book→Fountain (book_full.txt must exist).</summary>
    public bool SkipPrepare { get; set; }
    public bool ForceExtract { get; set; } = true;
    public bool ForceVision { get; set; }
    public bool AutoVision { get; set; } = true;
    public string VisionModel { get; set; } = "grok-4.5";
    public string Model { get; set; } = "grok-4.5";
}

/// <summary>POST /api/projects — create a new project folder.</summary>
public sealed class CreateProjectRequest
{
    /// <summary>Display name or folder id.</summary>
    public string? Name { get; set; }
    public string? Id { get; set; }
    public string? Title { get; set; }
}

public sealed class LockCharacterRequest
{
    public string ProjectId { get; set; } = "";
    public string CharKey { get; set; } = "";
    /// <summary>bookref | variant</summary>
    public string Source { get; set; } = "variant";
    public int Index { get; set; } = 1;
}

/// <summary>Update voice_label / voice_profile on character seeds (scenes.json + blueprint).</summary>
public sealed class UpdateCharacterVoiceRequest
{
    public string ProjectId { get; set; } = "";
    public string CharKey { get; set; } = "";
    public string? VoiceLabel { get; set; }
    public string? VoiceProfile { get; set; }
}

/// <summary>
/// Film-pipeline voice sample: short Grok video with VOICE LOCK, audio extracted to MP3.
/// </summary>
public sealed class StartVoicePreviewRequest
{
    public string ProjectId { get; set; } = "";
    public string CharKey { get; set; } = "";
    public string? VoiceLabel { get; set; }
    public string? VoiceProfile { get; set; }
    public string? DisplayName { get; set; }
    /// <summary>Optional dialogue line; default is a short name intro.</summary>
    public string? SampleText { get; set; }
    /// <summary>Ignore cache and regenerate (e.g. after editing profile).</summary>
    public bool Force { get; set; }
}

/// <summary>Whether a cached film voice sample exists and matches current profile text.</summary>
public sealed class VoicePreviewStatusDto
{
    public bool Ok { get; set; } = true;
    public bool Exists { get; set; }
    public bool Matches { get; set; }
    public string? Fingerprint { get; set; }
    public DateTimeOffset? GeneratedAt { get; set; }
    public string? AudioUrl { get; set; }
}

/// <summary>Start AI auto-review of one clip (this clip + previous tail for continuity).</summary>
public sealed class StartClipAutoReviewRequest
{
    public string ProjectId { get; set; } = "";
    public int Scene { get; set; }
    public int Clip { get; set; }
}

/// <summary>Batch AI auto-review for all (or missing) on-disk clips in a project/scene.</summary>
public sealed class StartClipAutoReviewBatchRequest
{
    public string ProjectId { get; set; } = "";
    /// <summary>When set, only that scene; otherwise all scenes with clips on disk.</summary>
    public int? Scene { get; set; }
    /// <summary>When true (default), skip clips that already have an auto-review draft.</summary>
    public bool OnlyMissing { get; set; } = true;
}

/// <summary>Project-wide review index (assets/review/index.json) — one row per on-disk clip.</summary>
public sealed class ReviewIndexDocument
{
    public string ProjectId { get; set; } = "";
    public DateTimeOffset BuiltAtUtc { get; set; }
    public string SchemaVersion { get; set; } = "1";
    public List<ReviewIndexClipRow> Clips { get; set; } = new();
}

/// <summary>One clip’s QC / assembly / frame paths for V4 audit.</summary>
public sealed class ReviewIndexClipRow
{
    public string Key { get; set; } = "";
    public int Scene { get; set; }
    public int Clip { get; set; }
    public string VideoPath { get; set; } = "";
    public bool VideoExists { get; set; }
    public string? AutoSuggestion { get; set; }
    public string? AutoCategory { get; set; }
    public string? AutoNote { get; set; }
    public DateTimeOffset? AutoReviewedAt { get; set; }
    public string? HumanStatus { get; set; }
    public string? HumanNote { get; set; }
    public bool AssemblyEligible { get; set; }
    public string? AssemblyBlockReason { get; set; }
    public string? DraftPath { get; set; }
    public bool HasDraft { get; set; }
    public List<string> FramePaths { get; set; } = new();
}

/// <summary>One suggested edit from auto-review (user may accept/edit before apply).</summary>
public sealed class ClipAutoReviewSuggestion
{
    /// <summary>clip | character | scene</summary>
    public string Layer { get; set; } = "clip";
    /// <summary>Field id: visual_prompt | voice_profile | description | visual_lock | note</summary>
    public string Field { get; set; } = "";
    /// <summary>Character key when layer=character.</summary>
    public string? CharKey { get; set; }
    public string Label { get; set; } = "";
    public string CurrentValue { get; set; } = "";
    public string SuggestedValue { get; set; } = "";
    /// <summary>Default checked when opening Apply panel (high confidence).</summary>
    public bool IncludeByDefault { get; set; } = true;
    public string? Rationale { get; set; }
}

/// <summary>AI draft for one clip — Review → Apply suggestions → Regen.</summary>
public sealed class ClipAutoReviewDraft
{
    public bool Ok { get; set; } = true;
    public string ProjectId { get; set; } = "";
    public int Scene { get; set; }
    public int Clip { get; set; }
    /// <summary>pass | fail | unclear</summary>
    public string Suggestion { get; set; } = "unclear";
    public string Category { get; set; } = "other";
    public string Confidence { get; set; } = "medium";
    public string Note { get; set; } = "";
    public string Continuity { get; set; } = "unclear";
    public bool IncludedPreviousTail { get; set; }
    public DateTimeOffset? GeneratedAt { get; set; }
    public List<ClipAutoReviewSuggestion> Suggestions { get; set; } = new();
    public string? RawSummary { get; set; }
    public string? Error { get; set; }
}

/// <summary>Apply selected suggestion rows (writes seeds / blueprint), optional then regen via gen job.</summary>
public sealed class ApplyClipAutoReviewRequest
{
    public string ProjectId { get; set; } = "";
    public int Scene { get; set; }
    public int Clip { get; set; }
    public List<ClipAutoReviewApplyItem> Items { get; set; } = new();
}

public sealed class ClipAutoReviewApplyItem
{
    public string Layer { get; set; } = "clip";
    public string Field { get; set; } = "";
    public string? CharKey { get; set; }
    /// <summary>Final text to write (user may have edited the suggestion).</summary>
    public string Value { get; set; } = "";
}

/// <summary>Update description / visual_lock on character seeds (cast_seeds + blueprint).</summary>
public sealed class UpdateCharacterLookRequest
{
    public string ProjectId { get; set; } = "";
    public string CharKey { get; set; } = "";
    public string? Description { get; set; }
    public string? VisualLock { get; set; }
    /// <summary>
    /// Run AI visual scrub (literal + base-look, not later wardrobe) before save. Default true.
    /// </summary>
    public bool ScrubWithAi { get; set; } = true;
    public string Model { get; set; } = "grok-4.5";
}

/// <summary>Response from look save (optional AI scrub applied).</summary>
public sealed class UpdateCharacterLookResult
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public string? CharKey { get; set; }
    public bool ScrubbedWithAi { get; set; }
    public string? Description { get; set; }
    public string? VisualLock { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}

/// <summary>Delete preferred, variant, or book-ref picture for a character.</summary>
public sealed class DeleteCharacterImageRequest
{
    public string Kind { get; set; } = ""; // preferred | variant | bookref
    public int Index { get; set; }
}

public sealed class SceneSummary
{
    public int SceneNumber { get; set; }
    public string Setting { get; set; } = "";
    public int ClipCount { get; set; }
    public int ClipsOnDisk { get; set; }
    public bool ClipsComplete { get; set; }
    /// <summary>Stage 2 plan total (sum of planned clip targets). Not measured media.</summary>
    public double? PlannedDurationSeconds { get; set; }
    /// <summary>Measured from composite, or sum of on-disk clips. Null if no media.</summary>
    public double? ActualDurationSeconds { get; set; }
    /// <summary>Preferred display: actual if known, else planned.</summary>
    public double? DurationSeconds { get; set; }
    public bool CompositeExists { get; set; }
    public List<string> CharactersOnScreen { get; set; } = new();
    public List<string> LocationIds { get; set; } = new();
    public string? PrimaryLocationId { get; set; }
    public string Status { get; set; } = "empty"; // empty | partial | complete

    /// <summary>User holding scene lock (Phase D), if any.</summary>
    public string? LockOwnerUserId { get; set; }
    /// <summary>True when locked by a different user than the caller.</summary>
    public bool LockedByOther { get; set; }
    public string? LockReason { get; set; }
}

public sealed class ClipSummary
{
    public int ClipNumber { get; set; }
    public string Timestamp { get; set; } = "";
    /// <summary>Stage 2 planned duration for this clip.</summary>
    public int DurationSeconds { get; set; }
    /// <summary>Measured from the MP4 when on disk.</summary>
    public double? ActualDurationSeconds { get; set; }
    public string Continuation { get; set; } = "none";
    public string PrimarySubject { get; set; } = "";
    public string VisualPrompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
    public string Dialogue { get; set; } = "";
    public string? Speaker { get; set; }
    public string? Delivery { get; set; }
    public List<string> CharactersOnScreen { get; set; } = new();
    public string? ColorPalette { get; set; }
    public string? FilmStock { get; set; }
    public bool OnDisk { get; set; }
    public long SizeBytes { get; set; }
    public string? VideoUrl { get; set; }
    public string? FileName { get; set; }
}

/// <summary>Create or fully edit a clip's shot-plan fields (Scenes clip editor).</summary>
public sealed class ClipEditRequest
{
    public string ProjectId { get; set; } = "";
    public int Scene { get; set; }
    public int Clip { get; set; }
    public string VisualPrompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
    public string Dialogue { get; set; } = "";
    public string? Speaker { get; set; }
    public string? Delivery { get; set; }
    public string PrimarySubject { get; set; } = "";
    public List<string> CharactersOnScreen { get; set; } = new();
    public string? ColorPalette { get; set; }
    public string? FilmStock { get; set; }
    public int DurationSeconds { get; set; }
}

public sealed class SceneDetail
{
    public int SceneNumber { get; set; }
    public string Setting { get; set; } = "";
    /// <summary>Stage 2 plan total.</summary>
    public double? PlannedDurationSeconds { get; set; }
    /// <summary>Measured composite or sum of clips.</summary>
    public double? ActualDurationSeconds { get; set; }
    /// <summary>Preferred display: actual if known, else planned.</summary>
    public double? DurationSeconds { get; set; }
    public int ClipCount { get; set; }
    public int ClipsOnDisk { get; set; }
    public bool CompositeExists { get; set; }
    public string? CompositeUrl { get; set; }
    public List<string> CharactersOnScreen { get; set; } = new();
    public List<string> LocationIds { get; set; } = new();
    public string? PrimaryLocationId { get; set; }
    public List<ClipSummary> Clips { get; set; } = new();
}

/// <summary>Book + Fountain screenplay + Stage 1 + Stage 2 readiness for the Adaptation page.</summary>
public sealed class AdaptationStatus
{
    public string ProjectId { get; set; } = "";
    public BookSourceStatus Book { get; set; } = new();
    /// <summary>Editable Fountain draft + sign-off state (source of truth for the screenplay).</summary>
    public ScreenplayStatus Screenplay { get; set; } = new();
    public Stage1Status Stage1 { get; set; } = new();
    public Stage2PlanStatus Stage2 { get; set; } = new();
    /// <summary>Cast pin progress (look + voice) for next-step gating.</summary>
    public CastStatus Cast { get; set; } = new();
    public bool XaiConfigured { get; set; }
    /// <summary>Configured planning model (Configuration page) — Stage 1 / book→Fountain / cast scrub.</summary>
    public string PlanningModel { get; set; } = "grok-4.5";
    public string NextStep { get; set; } = "";
}

/// <summary>
/// Whether every cast member is approved for video: voice profile for all,
/// locked ref image for on-screen roles (variant drafts alone are not enough).
/// </summary>
public sealed class CastStatus
{
    public int Total { get; set; }
    public int Ready { get; set; }
    /// <summary>True when every cast member has approved voice + locked look (if needed).</summary>
    public bool ReadyForShots { get; set; }
    /// <summary>Character keys still missing voice and/or locked image (for UI messaging).</summary>
    public List<string> Missing { get; set; } = new();
}

/// <summary>
/// Fountain draft under source/screenplay.fountain.
/// Signed = user approved; cast seeds materialised on sign-off.
/// </summary>
public sealed class ScreenplayStatus
{
    public bool DraftExists { get; set; }
    public long DraftBytes { get; set; }
    public string? DraftHash { get; set; }
    public string? DraftMtime { get; set; }
    public int SceneHeadingCount { get; set; }
    public string? Title { get; set; }
    /// <summary>True when draft hash matches last sign-off hash.</summary>
    public bool Signed { get; set; }
    public string? SignedHash { get; set; }
    public string? SignedAt { get; set; }
    /// <summary>Draft exists and differs from signed hash (or never signed).</summary>
    public bool Dirty { get; set; }
    /// <summary>Characters/Shots may proceed (signed Fountain with scene headings).</summary>
    public bool ReadyForShots { get; set; }
}

public sealed class BookSourceStatus
{
    public bool PdfExists { get; set; }
    public string? PdfName { get; set; }
    public bool BookTextExists { get; set; }
    public string? BookTextPath { get; set; }
    public long BookTextBytes { get; set; }
    public string? TextQuality { get; set; }
    public double GarbageScore { get; set; }
    public string? BookKind { get; set; }
    public string? TextEngine { get; set; }
    public int? TextWords { get; set; }
    public int? SuggestedTotalMinutes { get; set; }
    public int? SuggestedChunkPages { get; set; }
    public int PageImageCount { get; set; }
    public bool ReadyForStage1 { get; set; }
    public string? Preview { get; set; }
    public List<string> Notes { get; set; } = new();
}

public sealed class Stage1Status
{
    public bool Present { get; set; }
    public string? ScenesFile { get; set; }
    public string? MovieTitle { get; set; }
    public string? SourceBookTitle { get; set; }
    public int SceneCount { get; set; }
    public int BeatCount { get; set; }
    public int CharacterCount { get; set; }
    public int LocationCount { get; set; }
    public double? RuntimeSeconds { get; set; }
    public string? Mtime { get; set; }
    public List<string> CastNames { get; set; } = new();
    public List<Stage1SceneRow> Scenes { get; set; } = new();
}

public sealed class Stage1SceneRow
{
    public int SceneNumber { get; set; }
    public string Setting { get; set; } = "";
    public int BeatCount { get; set; }
    public double? DurationSeconds { get; set; }
}

public sealed class Stage2PlanStatus
{
    public bool Stage1Exists { get; set; }
    public int Stage1Scenes { get; set; }
    public bool BlueprintExists { get; set; }
    public string? BlueprintPath { get; set; }
    public string? BlueprintFileName { get; set; }
    public int Stage2Scenes { get; set; }
    public int Stage2Clips { get; set; }
    public bool Stage2Ready { get; set; }
    public bool Stage2Stale { get; set; }
    public string? LastCompletedAt { get; set; }
    public string? LastRunMessage { get; set; }
    public int ValidationIssueCount { get; set; }
}

public sealed class StartStage1Request
{
    public string ProjectId { get; set; } = "";
    public int ChunkPages { get; set; } = 10;
    public int? TotalMinutes { get; set; }
    public string Model { get; set; } = "grok-4.5";
    public bool Resume { get; set; }
    public int MaxChunks { get; set; }
}

public sealed class StartStage2Request
{
    public string ProjectId { get; set; } = "";
    /// <summary>Optional; empty uses project Configuration resolution (prompt tag only).</summary>
    public string? Resolution { get; set; }
    public string Scenes { get; set; } = "all";
}

public sealed class StartRemuxRequest
{
    public string ProjectId { get; set; } = "";
    public int? Scene { get; set; }
    public bool RebuildWip { get; set; } = true;
    /// <summary>
    /// When true with <see cref="RebuildWip"/>, remux only <b>stale</b> scene composites
    /// (clips newer than composite, or composite missing), then stitch WIP.
    /// </summary>
    public bool RefreshStaleScenes { get; set; }
    /// <summary>When true, 409 if remux locks held by another user (default wait).</summary>
    public bool FailIfLocked { get; set; }
    /// <summary>
    /// Emergency only: include human-fail / unresolved auto-fail clips in remux/WIP.
    /// Requires <see cref="IgnoreAssemblyGateReason"/> (≥12 chars).
    /// </summary>
    public bool IgnoreAssemblyGate { get; set; }
    /// <summary>Required when <see cref="IgnoreAssemblyGate"/> is true.</summary>
    public string? IgnoreAssemblyGateReason { get; set; }
}

/// <summary>
/// Build a temporary multi-scene preview (assets/movie_preview.mp4) by stitching an explicit,
/// caller-ordered scene list — can skip scenes (e.g. 1,2,4) so a user can audition whether
/// cutting a scene improves pacing before deleting it for real.
/// </summary>
public sealed class StartPreviewRequest
{
    public string ProjectId { get; set; } = "";
    /// <summary>Scene numbers in playback order; duplicates/order preserved as given.</summary>
    public List<int> Scenes { get; set; } = new();
    /// <summary>When true, remux any selected scene whose composite is missing/stale before stitching.</summary>
    public bool RefreshStaleScenes { get; set; } = true;
    public bool IgnoreAssemblyGate { get; set; }
    public string? IgnoreAssemblyGateReason { get; set; }
}

public sealed class StartYouTubeUploadRequest
{
    public string ProjectId { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    /// <summary>private | unlisted | public. Default unlisted (link-shareable, not publicly listed).</summary>
    public string PrivacyStatus { get; set; } = "unlisted";
}

/// <summary>Last successful YouTube upload for a project (sidecar assets/youtube_upload.json).</summary>
public sealed class YouTubeUploadInfo
{
    public string VideoId { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string PrivacyStatus { get; set; } = "";
    public DateTimeOffset UploadedAt { get; set; }
}

/// <summary>WIP movie freshness for Play-WIP-one-step UX.</summary>
public sealed class WipFreshness
{
    public bool Exists { get; set; }
    public bool Stale { get; set; }
    public bool CanBuild { get; set; }
    public string Reason { get; set; } = "";
    public string? Path { get; set; }
    public long Bytes { get; set; }
    public string? UpdatedAt { get; set; }
    /// <summary>Scenes whose composites need rebuild (clips newer / missing composite).</summary>
    public List<int> StaleScenes { get; set; } = new();
    /// <summary>All scenes that should be remuxed before WIP (Stage 2 order, with clips).</summary>
    public List<int> ScenesToRemux { get; set; } = new();
}

public sealed class ClipReviewRequest
{
    public string ProjectId { get; set; } = "";
    public int Scene { get; set; }
    public int Clip { get; set; }
    /// <summary>pass | fail | pending</summary>
    public string Status { get; set; } = "pass";
    public string Note { get; set; } = "";
}

public sealed class SceneApproveRequest
{
    public string ProjectId { get; set; } = "";
    public int Scene { get; set; }
    public string Note { get; set; } = "";
    public bool Remux { get; set; }
    public bool RebuildWip { get; set; }
}

public sealed class EditLogEntry
{
    public string Id { get; set; } = "";
    public string Ts { get; set; } = "";
    public string Type { get; set; } = "";
    public string LearningLayer { get; set; } = "clip";
    public int? Scene { get; set; }
    public int? Clip { get; set; }
    public string? Character { get; set; }
    public string UserNote { get; set; } = "";
    public string ActionTaken { get; set; } = "";
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public string SuggestedRule { get; set; } = "";
}

public sealed class EditLogDocument
{
    public int Version { get; set; } = 1;
    public List<EditLogEntry> Entries { get; set; } = new();
}

/// <summary>SignalR event payloads.</summary>
public static class JobHubEvents
{
    public const string JobUpdated = "JobUpdated";
    public const string JobLog = "JobLog";
    /// <summary>Admin ops group snapshot push (Phase C).</summary>
    public const string AdminState = "AdminState";
}

// ---- Cost / ledger ----

public sealed class CostEvent
{
    public string? Id { get; set; }
    public string? Ts { get; set; }
    public string Kind { get; set; } = "video"; // video | image | other
    public int? Scene { get; set; }
    public int? Clip { get; set; }
    public string? Model { get; set; }
    public string? Resolution { get; set; }
    public double? DurationSec { get; set; }
    public double Usd { get; set; }
    public string Currency { get; set; } = "USD";
    public string? Source { get; set; } // list_rate | backfill
    public string? Character { get; set; }
    public double? OutputRatePerSec { get; set; }
    public bool? HasRefImage { get; set; }
    public bool? IsExtend { get; set; }
}

public sealed class CostLedgerSummary
{
    public double ActualUsd { get; set; }
    public int EventCount { get; set; }
    public int VideoJobs { get; set; }
    public int ImageJobs { get; set; }
    public double VideoSec { get; set; }
    public Dictionary<string, double> ByKind { get; set; } = new();
    public Dictionary<string, double> ByScene { get; set; } = new();
    public Dictionary<string, double> ByModel { get; set; } = new();
    public string Currency { get; set; } = "USD";
    public string Notes { get; set; } =
        "Tracked actuals = list-rate pricing at generation time (cost_ledger). Not xAI invoices.";
}

public sealed class CostSceneRow
{
    public int Scene { get; set; }
    public string Setting { get; set; } = "";
    public int ClipsTotal { get; set; }
    public int ClipsOnDisk { get; set; }
    public int ClipsMissing { get; set; }
    public bool IsHero { get; set; }
    public string? HeroResolution { get; set; }
    public List<string> CharactersOnScreen { get; set; } = new();
    public List<string> LocationIds { get; set; } = new();
    public string? PrimaryLocationId { get; set; }
    public double SpentUsd { get; set; }
    public double ActualUsd { get; set; }
    public double RemainingDraftUsd { get; set; }
    public double HeroUpgradeUsd { get; set; }
    public double AllDraftUsd { get; set; }
    public double AllHeroUsd { get; set; }
    public double DurationOnDiskSec { get; set; }
    public double DurationMissingSec { get; set; }
}

public sealed class CostReportSummary
{
    public int ClipsTotal { get; set; }
    public int ClipsOnDisk { get; set; }
    public int ClipsMissing { get; set; }
    public double SecOnDisk { get; set; }
    public double SecMissing { get; set; }
    public double SpentUsd { get; set; }
    public double ActualUsd { get; set; }
    public int ActualEvents { get; set; }
    public int ActualVideoJobs { get; set; }
    public double ActualVideoSec { get; set; }
    public double RemainingFirstPassUsd { get; set; }
    public double RemainingHeroUpgradeUsd { get; set; }
    public double FinishDraftUsd { get; set; }
    public double FinishDraftPlusHeroUsd { get; set; }
    public double FinishFromActualUsd { get; set; }
    public double FullFilmAllDraftUsd { get; set; }
    public double FullFilmAllHeroUsd { get; set; }
    public int ScenesWithMedia { get; set; }
    public int ScenesHero { get; set; }
    public int ScenesTotal { get; set; }
}

public sealed class CostScenarioRow
{
    public string Label { get; set; } = "";
    public string Resolution { get; set; } = "480p";
    public string? ModelName { get; set; }
    public double RatePerSec { get; set; }
    public double FullFilmUsd { get; set; }
    public double RemainingMissingUsd { get; set; }
    public double RegenOnDiskUsd { get; set; }
    public double AssumeAvgRetries { get; set; }
}

public sealed class CostReport
{
    public string ProjectId { get; set; } = "";
    public string DraftResolution { get; set; } = "480p";
    public string HeroResolution { get; set; } = "720p";
    public string? ModelName { get; set; }
    public string? VideoProvider { get; set; }
    public double OutputRateDraft { get; set; }
    public double OutputRateHero { get; set; }
    public double AssumeAvgRetries { get; set; }
    public CostReportSummary Summary { get; set; } = new();
    public CostLedgerSummary Actual { get; set; } = new();
    public List<CostSceneRow> Scenes { get; set; } = new();
    public List<CostScenarioRow> Scenarios { get; set; } = new();
    public List<CostEvent> RecentEvents { get; set; } = new();
    public string Notes { get; set; } =
        "Estimates = planning (current rates × scope). Actual = cost_ledger list rates (not xAI invoice).";
    public string Currency { get; set; } = "USD";
}

public sealed class CostBackfillResult
{
    public int Added { get; set; }
    public int Skipped { get; set; }
    public int LedgerEvents { get; set; }
    public double ActualUsd { get; set; }
}
