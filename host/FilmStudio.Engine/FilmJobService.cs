using System.Collections.Concurrent;
using System.Text.Json;
using FilmStudio.Core.Models;
using FilmStudio.Core.Options;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

public interface IJobProgressSink
{
    Task OnJobUpdatedAsync(JobSnapshot snapshot, CancellationToken ct = default);
    Task OnJobLogAsync(string message, CancellationToken ct = default);
}

/// <summary>
/// Native C# film job orchestrator (no Python): Stage 1/2, book prepare,
/// character design, multi-ref video, remux/WIP with SignalR progress.
/// Phase C: multi-job concurrency via ApiWorkerPool/LocalWorkerPool, scene locks, metrics.
/// </summary>
public sealed class FilmJobService
{
    private static readonly AsyncLocal<JobRunState?> CurrentRun = new();
    private static readonly TimeSpan DefaultLockTtl = TimeSpan.FromHours(2);

    private readonly ProjectStore _projects;
    private readonly IVideoClient _grok;
    private readonly CharacterDesignService _characters;
    private readonly CharacterBookPlateService _plates;
    private readonly BookPrepareService _books;
    private readonly IChatClient _chat;
    private readonly Stage1Service _stage1;
    private readonly Stage2PlannerService _stage2;
    private readonly IFfmpegRemux _remux;
    private readonly VoicePreviewService _voicePreview;
    private readonly ClipAutoReviewService _clipAutoReview;
    private readonly ReviewIndexService _reviewIndex;
    private readonly ProjectTelemetryService _telemetry;
    private readonly ProjectArtifactIndexService _artifactIndex;
    private readonly ReviewEventStore _learning;
    private readonly EditLogService _editLogs;
    private readonly PromptPackService _promptPacks;
    private readonly ProjectRulesService _projectRules;
    private readonly CostReportService _costs;
    private readonly IJobStore _jobs;
    private readonly ILockService _locks;
    private readonly ApiWorkerPool _apiPool;
    private readonly LocalWorkerPool _localPool;
    private readonly IServerMetricsService _metrics;
    private readonly FilmStudioOptions _opts;
    private readonly ILogger<FilmJobService> _log;
    private readonly ConcurrentQueue<string> _logLines = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobCts =
        new(StringComparer.OrdinalIgnoreCase);
    private IJobProgressSink? _sink;
    private readonly IUserContext _user;
    private readonly IUserApiKeyProvider _keys;

    public FilmJobService(
        ProjectStore projects,
        IVideoClient grok,
        CharacterDesignService characters,
        CharacterBookPlateService plates,
        BookPrepareService books,
        IChatClient chat,
        Stage1Service stage1,
        Stage2PlannerService stage2,
        IFfmpegRemux remux,
        VoicePreviewService voicePreview,
        ClipAutoReviewService clipAutoReview,
        ReviewIndexService reviewIndex,
        ProjectTelemetryService telemetry,
        ProjectArtifactIndexService artifactIndex,
        ReviewEventStore learning,
        EditLogService editLogs,
        PromptPackService promptPacks,
        ProjectRulesService projectRules,
        CostReportService costs,
        IJobStore jobs,
        ILockService locks,
        ApiWorkerPool apiPool,
        LocalWorkerPool localPool,
        IServerMetricsService metrics,
        IOptions<FilmStudioOptions> opts,
        ILogger<FilmJobService> log,
        IUserContext user,
        IUserApiKeyProvider keys)
    {
        _projects = projects;
        _grok = grok;
        _characters = characters;
        _plates = plates;
        _books = books;
        _chat = chat;
        _stage1 = stage1;
        _stage2 = stage2;
        _remux = remux;
        _voicePreview = voicePreview;
        _clipAutoReview = clipAutoReview;
        _reviewIndex = reviewIndex;
        _telemetry = telemetry;
        _artifactIndex = artifactIndex;
        _learning = learning;
        _editLogs = editLogs;
        _promptPacks = promptPacks;
        _projectRules = projectRules;
        _costs = costs;
        _jobs = jobs;
        _locks = locks;
        _apiPool = apiPool;
        _localPool = localPool;
        _metrics = metrics;
        _opts = opts.Value;
        _log = log;
        _user = user;
        _keys = keys;
    }

    public void SetProgressSink(IJobProgressSink sink) => _sink = sink;

    /// <summary>
    /// Primary job for the current caller (Phase F: no global singleton job).
    /// Prefers this user's running job, else their most recent, else idle.
    /// </summary>
    public JobSnapshot GetSnapshot()
    {
        var userId = string.IsNullOrWhiteSpace(_user.UserId) ? null : _user.UserId;
        var primary = _jobs.GetPrimary(userId);
        if (primary is not null)
            return primary.ToSnapshot();
        // Fallback: active AsyncLocal run (background worker thread)
        if (CurrentRun.Value?.Snapshot is { } live &&
            !string.Equals(live.Status, "idle", StringComparison.OrdinalIgnoreCase))
            return Clone(live);
        return new JobSnapshot { Status = "idle", UserId = userId };
    }

    public JobSnapshot? GetJob(string jobId) => _jobs.Get(jobId)?.ToSnapshot();

    public IReadOnlyList<JobSnapshot> ListJobs(string? userId = null, string? projectId = null, int take = 50) =>
        _jobs.List(userId, projectId, take).Select(j => j.ToSnapshot()).ToList();

    public bool IsRunning => _jobs.CountRunning() > 0;

    /// <summary>O(1) count of jobs currently running (hot path for /api/capacity).</summary>
    public int RunningCount => _jobs.CountRunning();

    public CapacityOptions Capacity => _opts.Capacity ?? new CapacityOptions();

    public ILockService Locks => _locks;

    public IServerMetricsService Metrics => _metrics;

    /// <summary>
    /// Cancel one job by id, or cancel active jobs in scope.
    /// </summary>
    /// <param name="jobId">When set, cancel only this job (ownership is enforced at the API).</param>
    /// <param name="userId">
    /// When canceling without <paramref name="jobId"/> and <paramref name="cancelAllUsers"/> is false,
    /// only cancel jobs owned by this user. Required for bulk cancel unless canceling all users.
    /// </param>
    /// <param name="cancelAllUsers">
    /// When true (admin only at API), cancel every active job regardless of owner.
    /// </param>
    /// <returns>Number of jobs that were marked cancelled / had CTS cancelled.</returns>
    public Task<int> CancelAsync(
        string? jobId = null,
        string? userId = null,
        bool cancelAllUsers = false)
    {
        if (!string.IsNullOrWhiteSpace(jobId))
        {
            var n = CancelOneJob(jobId!) ? 1 : 0;
            return Task.FromResult(n);
        }

        // Refuse unscoped bulk cancel — callers must pass userId or cancelAllUsers.
        if (!cancelAllUsers && string.IsNullOrWhiteSpace(userId))
            return Task.FromResult(0);

        var cancelled = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Prefer store records (have UserId) over bare CTS keys.
        var records = cancelAllUsers
            ? _jobs.List(userId: null, take: 200)
            : _jobs.List(userId: userId, take: 200);

        foreach (var rec in records)
        {
            if (!IsActiveJobStatus(rec.Status))
                continue;
            if (!seen.Add(rec.JobId))
                continue;
            if (CancelOneJob(rec.JobId))
                cancelled++;
        }

        // CTS entries that might lack a store row (edge case)
        foreach (var kv in _jobCts.ToArray())
        {
            if (!seen.Add(kv.Key))
                continue;
            var rec = _jobs.Get(kv.Key);
            if (!IsInBulkCancelScope(rec?.UserId, userId, cancelAllUsers))
                continue;
            if (CancelOneJob(kv.Key))
                cancelled++;
        }

        return Task.FromResult(cancelled);
    }

    /// <summary>Whether a job owner matches bulk-cancel scope (unit-tested).</summary>
    public static bool IsInBulkCancelScope(
        string? jobUserId,
        string? requestUserId,
        bool cancelAllUsers)
    {
        if (cancelAllUsers)
            return true;
        if (string.IsNullOrWhiteSpace(requestUserId))
            return false;
        return string.Equals(jobUserId, requestUserId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsActiveJobStatus(string? status) =>
        string.Equals(status, "running", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase);

    private bool CancelOneJob(string jobId)
    {
        var storeHit = _jobs.TryCancel(jobId);
        var ctsHit = false;
        if (_jobCts.TryGetValue(jobId, out var cts))
        {
            try
            {
                cts.Cancel();
                ctsHit = true;
            }
            catch
            {
                /* ignore */
            }
        }
        return storeHit || ctsHit;
    }

    private void EnsureCanStart(string? userId)
    {
        var cap = Capacity;
        // Soft gate: running at global max still allows queue until per-user max;
        // worker pool will wait for a slot. Reject only when user queue is full.
        if (!string.IsNullOrWhiteSpace(userId) &&
            _jobs.CountQueuedForUser(userId!) >= Math.Max(1, cap.MaxQueuePerUser))
        {
            _metrics.NoteCapacityReject();
            throw new CapacityRejectedException(
                $"User queue full: MaxQueuePerUser={cap.MaxQueuePerUser}.");
        }

        // Hard reject if global running already >> 2x cap (runaway protection)
        var running = _jobs.CountRunning();
        var max = Math.Max(1, cap.MaxVideoInFlight);
        if (running >= max + Math.Max(1, cap.MaxQueuePerUser))
        {
            _metrics.NoteCapacityReject();
            throw new CapacityRejectedException(
                $"At capacity: running={running}, MaxVideoInFlight={max}.");
        }
    }

    private JobSnapshot Snapshot
    {
        get => CurrentRun.Value?.Snapshot
               ?? throw new InvalidOperationException("No active job run context.");
        set
        {
            var run = CurrentRun.Value
                      ?? throw new InvalidOperationException("No active job run context.");
            run.Snapshot = value;
        }
    }

    private string? ActiveJobId
    {
        get => CurrentRun.Value?.ActiveJobId;
        set
        {
            if (CurrentRun.Value is not null)
                CurrentRun.Value.ActiveJobId = value;
        }
    }

    /// <summary>
    /// Promote pre-created queued job to running (or create if none). Publishes SignalR.
    /// </summary>
    private void RegisterActiveJob()
    {
        var run = CurrentRun.Value
                  ?? throw new InvalidOperationException("No active job run context.");
        if (string.IsNullOrWhiteSpace(Snapshot.UserId))
            Snapshot.UserId = run.UserId;
        Snapshot.QueuedAt ??= run.QueuedAt;
        Snapshot.StartedAt ??= DateTimeOffset.UtcNow;
        Snapshot.Status = "running";
        run.StartedAt = Snapshot.StartedAt;

        if (!string.IsNullOrWhiteSpace(run.ActiveJobId))
        {
            // Promote existing queued → running
            Snapshot.JobId = run.ActiveJobId;
            _jobs.Update(run.ActiveJobId, rec =>
            {
                rec.Status = "running";
                rec.Kind = Snapshot.Kind;
                rec.Message = Snapshot.Message;
                rec.ProjectId = Snapshot.ProjectId;
                rec.UserId = Snapshot.UserId;
                rec.CharKey = Snapshot.CharKey;
                rec.Scene = Snapshot.Scene;
                rec.Clip = Snapshot.Clip;
                rec.Index = Snapshot.Index;
                rec.Total = Snapshot.Total;
                rec.Log = Snapshot.Log.ToList();
                rec.StartedAt = Snapshot.StartedAt;
                rec.QueuedAt = Snapshot.QueuedAt ?? rec.QueuedAt;
            });
            foreach (var res in run.HeldLocks)
            {
                var existing = _locks.Get(res);
                if (existing is not null &&
                    string.Equals(existing.UserId, run.UserId, StringComparison.OrdinalIgnoreCase))
                {
                    _locks.TryAcquire(res, run.UserId, DefaultLockTtl, existing.Reason, run.ActiveJobId);
                }
            }
            _metrics.NoteJobStarted(Snapshot.Kind ?? "job", run.UserId, run.QueuedAt);
            _ = PublishAsync();
            return;
        }

        // Fallback: create running job when no pre-queued record
        var recNew = _jobs.Create(new JobRecord
        {
            Status = Snapshot.Status,
            Kind = Snapshot.Kind,
            ProjectId = Snapshot.ProjectId,
            UserId = Snapshot.UserId,
            CharKey = Snapshot.CharKey,
            Scene = Snapshot.Scene,
            Clip = Snapshot.Clip,
            Message = Snapshot.Message,
            Index = Snapshot.Index,
            Total = Snapshot.Total,
            QueuedAt = run.QueuedAt,
            StartedAt = Snapshot.StartedAt ?? DateTimeOffset.UtcNow,
            Log = Snapshot.Log.ToList(),
        });
        ActiveJobId = recNew.JobId;
        Snapshot.JobId = recNew.JobId;
        Snapshot.QueuedAt = recNew.QueuedAt;
        _jobCts[recNew.JobId] = run.Cts;
        foreach (var res in run.HeldLocks)
        {
            var existing = _locks.Get(res);
            if (existing is not null &&
                string.Equals(existing.UserId, run.UserId, StringComparison.OrdinalIgnoreCase))
            {
                _locks.TryAcquire(res, run.UserId, DefaultLockTtl, existing.Reason, recNew.JobId);
            }
        }
        _metrics.NoteJobStarted(Snapshot.Kind ?? "job", run.UserId, run.QueuedAt);
        _ = PublishAsync();
    }

    private sealed class JobEnqueueMeta
    {
        public string? Kind { get; set; }
        public string? ProjectId { get; set; }
        public int? Scene { get; set; }
        public int? Clip { get; set; }
        public string? CharKey { get; set; }
        public string Message { get; set; } = "Queued — waiting for worker…";
    }

    /// <summary>
    /// Phase 2: accept job as <c>queued</c> immediately, wait for locks + worker slot, then run.
    /// Hard 409 only when user queue is full, or <paramref name="failIfLocked"/> and lock held by other.
    /// </summary>
    private Task<JobSnapshot> StartBackgroundJobAsync(
        Func<CancellationToken, Task> work,
        JobEnqueueMeta meta,
        IReadOnlyList<string>? lockResources = null,
        string? lockReason = null,
        bool useLocalPool = false,
        bool failIfLocked = false)
    {
        var userId = string.IsNullOrWhiteSpace(_user.UserId) ? "local" : _user.UserId.Trim();
        EnsureCanStart(userId);

        var resources = (lockResources ?? Array.Empty<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Hard reject only when client asks FailIfLocked and lock is held by someone else
        if (failIfLocked)
        {
            foreach (var res in resources)
            {
                var held = _locks.Get(res);
                if (held is null) continue;
                if (string.Equals(held.UserId, userId, StringComparison.OrdinalIgnoreCase))
                    continue;
                _metrics.NoteLockConflict();
                throw new LockConflictException(res, held.UserId, held.ExpiresAt);
            }
        }

        var apiKey = !string.IsNullOrWhiteSpace(_user.RequestApiKey)
            ? _user.RequestApiKey
            : _keys.GetKey(userId);

        var queuedAt = DateTimeOffset.UtcNow;
        var cts = new CancellationTokenSource();
        var kind = meta.Kind ?? "job";
        var rec = _jobs.Create(new JobRecord
        {
            Status = "queued",
            Kind = kind,
            ProjectId = meta.ProjectId,
            UserId = userId,
            CharKey = meta.CharKey,
            Scene = meta.Scene,
            Clip = meta.Clip,
            Message = meta.Message,
            QueuedAt = queuedAt,
            Log = new List<string> { meta.Message },
        });

        var run = new JobRunState
        {
            UserId = userId,
            ApiKey = apiKey,
            QueuedAt = queuedAt,
            UseLocalPool = useLocalPool,
            Cts = cts,
            ActiveJobId = rec.JobId,
            HeldLocks = new List<string>(),
            Snapshot = rec.ToSnapshot(),
            PendingLockResources = resources,
            LockReason = lockReason,
        };
        _jobCts[rec.JobId] = cts;
        _metrics.NoteJobQueued(kind, userId);
        _ = PublishSnapshotAsync(run.Snapshot);

        _ = Task.Run(async () =>
        {
            CurrentRun.Value = run;
            using (ApiKeyScope.Push(run.ApiKey))
            {
                var startedAt = DateTimeOffset.UtcNow;
                var success = false;
                try
                {
                    // Wait for locks (queued stays visible via SignalR messages)
                    await WaitForLocksAsync(run, cts.Token);

                    await UpdateQueuedMessageAsync(run, "Waiting for worker slot…");

                    async Task RunWorkAsync(CancellationToken ct)
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Cts.Token, ct);
                        // Bind api_calls / ffmpeg.jsonl to this job's project for the async flow
                        using var tel = !string.IsNullOrWhiteSpace(meta.ProjectId)
                            ? _telemetry.UseProject(meta.ProjectId!)
                            : null;
                        await work(linked.Token);
                    }

                    if (useLocalPool)
                        await _localPool.RunAsync(RunWorkAsync, run.Cts.Token);
                    else
                        await _apiPool.RunAsync(userId, RunWorkAsync, run.Cts.Token);

                    var status = CurrentRun.Value?.Snapshot.Status;
                    success = string.Equals(status, "done", StringComparison.OrdinalIgnoreCase);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        if (CurrentRun.Value?.Snapshot is { } s &&
                            !string.Equals(s.Status, "cancelled", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(s.Status, "done", StringComparison.OrdinalIgnoreCase))
                        {
                            await FinishAsync("cancelled", "Cancelled by user");
                        }
                    }
                    catch { /* ignore */ }
                }
                catch (LockConflictException ex)
                {
                    _metrics.NoteLockConflict();
                    try
                    {
                        await FinishAsync("error", ex.Message, ex.Message);
                    }
                    catch { /* ignore */ }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Background job failed");
                    try
                    {
                        if (CurrentRun.Value?.Snapshot is { } s &&
                            (string.Equals(s.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(s.Status, "queued", StringComparison.OrdinalIgnoreCase)))
                        {
                            await FinishAsync("error", ex.Message, ex.Message);
                        }
                    }
                    catch { /* ignore */ }
                }
                finally
                {
                    var kindDone = CurrentRun.Value?.Snapshot.Kind ?? kind;
                    var q = run.QueuedAt;
                    var st = run.StartedAt ?? startedAt;
                    var snapStatus = CurrentRun.Value?.Snapshot.Status;
                    if (string.Equals(snapStatus, "done", StringComparison.OrdinalIgnoreCase))
                        success = true;
                    else if (string.Equals(snapStatus, "error", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(snapStatus, "cancelled", StringComparison.OrdinalIgnoreCase))
                        success = false;

                    _metrics.NoteJobFinished(kindDone, userId, success, q, st);

                    foreach (var res in run.HeldLocks)
                        _locks.Release(res, userId);

                    if (!string.IsNullOrWhiteSpace(run.ActiveJobId))
                    {
                        _jobCts.TryRemove(run.ActiveJobId, out _);
                        _locks.ReleaseAllForJob(run.ActiveJobId);
                    }

                    CurrentRun.Value = null;
                }
            }
        }, CancellationToken.None);

        return Task.FromResult(rec.ToSnapshot());
    }

    private async Task WaitForLocksAsync(JobRunState run, CancellationToken ct)
    {
        var resources = run.PendingLockResources;
        if (resources.Count == 0)
            return;

        await UpdateQueuedMessageAsync(run, "Waiting for resource lock…");

        while (!ct.IsCancellationRequested)
        {
            // Cancelled while queued?
            var job = !string.IsNullOrEmpty(run.ActiveJobId) ? _jobs.Get(run.ActiveJobId) : null;
            if (job is not null &&
                string.Equals(job.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                throw new OperationCanceledException("Job cancelled");
            }

            var acquired = new List<string>();
            string? blockedResource = null;
            string? blockedOwner = null;
            foreach (var res in resources)
            {
                if (_locks.TryAcquire(res, run.UserId, DefaultLockTtl, run.LockReason, run.ActiveJobId))
                {
                    acquired.Add(res);
                    continue;
                }

                var holder = _locks.Get(res);
                if (holder is not null &&
                    string.Equals(holder.UserId, run.UserId, StringComparison.OrdinalIgnoreCase))
                {
                    // Already ours
                    acquired.Add(res);
                    continue;
                }

                blockedResource = res;
                blockedOwner = holder?.UserId;
                break;
            }

            if (blockedResource is null)
            {
                run.HeldLocks = acquired;
                await UpdateQueuedMessageAsync(run, "Lock acquired — waiting for worker…");
                return;
            }

            foreach (var a in acquired)
                _locks.Release(a, run.UserId);

            var msg = string.IsNullOrEmpty(blockedOwner)
                ? $"Waiting for lock {blockedResource}…"
                : $"Waiting for lock (held by {blockedOwner})…";
            await UpdateQueuedMessageAsync(run, msg);
            await Task.Delay(300, ct);
        }

        throw new OperationCanceledException("Cancelled while waiting for lock");
    }

    private async Task UpdateQueuedMessageAsync(JobRunState run, string message)
    {
        if (string.IsNullOrEmpty(run.ActiveJobId)) return;
        run.Snapshot.Message = message;
        run.Snapshot.Status = "queued";
        if (run.Snapshot.Log.Count == 0 || run.Snapshot.Log[^1] != message)
        {
            run.Snapshot.Log.Add(message);
            if (run.Snapshot.Log.Count > 120)
                run.Snapshot.Log = run.Snapshot.Log.TakeLast(120).ToList();
        }
        _jobs.Update(run.ActiveJobId, rec =>
        {
            if (string.Equals(rec.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
                return;
            rec.Status = "queued";
            rec.Message = message;
            rec.Log = run.Snapshot.Log.ToList();
        });
        await PublishSnapshotAsync(run.Snapshot);
    }

    private async Task PublishSnapshotAsync(JobSnapshot snap)
    {
        if (_sink is not null)
            await _sink.OnJobUpdatedAsync(Clone(snap));
    }

    public Task<JobSnapshot> StartSceneGenAsync(StartSceneGenRequest req)
    {
        if (req.Scene <= 0)
            throw new InvalidOperationException("scene required");
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        return StartBackgroundJobAsync(
            ct => RunSceneGenAsync(req, ct),
            new JobEnqueueMeta
            {
                Kind = "scene",
                ProjectId = projectId,
                Scene = req.Scene,
                Clip = req.Clip,
                Message = $"Queued scene S{req.Scene:D2} gen…",
            },
            lockResources: new[] { LockKeys.Scene(projectId, req.Scene) },
            lockReason: $"scene gen S{req.Scene:D2}",
            failIfLocked: req.FailIfLocked);
    }

    public Task<JobSnapshot> StartBatchGenAsync(StartBatchGenRequest req)
    {
        var hasClips = req.Clips is { Count: > 0 };
        if ((req.Scenes is null || req.Scenes.Count == 0) && !hasClips)
            throw new InvalidOperationException("At least one scene or clip is required.");
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        var sceneNumbers = hasClips
            ? req.Clips!.Select(c => c.Scene)
            : req.Scenes ?? new List<int>();
        var locks = sceneNumbers
            .Where(s => s > 0)
            .Select(s => LockKeys.Scene(projectId, s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var queuedMsg = hasClips
            ? $"Queued batch gen ({req.Clips!.Count} clip(s))…"
            : $"Queued batch gen ({req.Scenes!.Count} scenes)…";
        return StartBackgroundJobAsync(
            ct => RunBatchGenAsync(req, ct),
            new JobEnqueueMeta
            {
                Kind = "batch",
                ProjectId = projectId,
                Message = queuedMsg,
            },
            lockResources: locks,
            lockReason: "batch scene gen",
            failIfLocked: req.FailIfLocked);
    }

    /// <summary>Book → Fountain draft + approve. Requires XAI_API_KEY.</summary>
    public Task<JobSnapshot> StartStage1Async(StartStage1Request req)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        return StartBackgroundJobAsync(
            ct => RunStage1Async(req, ct),
            new JobEnqueueMeta
            {
                Kind = "stage1",
                ProjectId = projectId,
                Message = "Queued Stage 1…",
            },
            lockResources: new[] { LockKeys.Stage(projectId) },
            lockReason: "stage1");
    }

    /// <summary>Stage 2 planner (Fountain → blueprint). Deterministic C#; no API key.</summary>
    public Task<JobSnapshot> StartStage2Async(StartStage2Request req)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        return StartBackgroundJobAsync(
            ct => RunStage2Async(req, ct),
            new JobEnqueueMeta
            {
                Kind = "stage2",
                ProjectId = projectId,
                Message = "Queued Stage 2…",
            },
            lockResources: new[] { LockKeys.Stage(projectId) },
            lockReason: "stage2");
    }

    /// <summary>C# PDF extract + optional Grok vision OCR → book_full.txt (prepare only).</summary>
    public Task<JobSnapshot> StartBookPrepareAsync(StartBookPrepareRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ProjectId))
            throw new InvalidOperationException("projectId required");
        return StartBackgroundJobAsync(
            ct => RunBookPrepareAsync(req, ct),
            new JobEnqueueMeta
            {
                Kind = "book_prepare",
                ProjectId = req.ProjectId,
                Message = "Queued book prepare…",
            },
            lockResources: new[] { LockKeys.Stage(req.ProjectId) },
            lockReason: "book prepare");
    }

    /// <summary>
    /// Full import path: prepare book text (unless skipped) then book→Fountain draft.
    /// Use for PDF/TXT Import; Screenplay “draft from book” can set <see cref="StartBookImportRequest.SkipPrepare"/>.
    /// </summary>
    public Task<JobSnapshot> StartBookImportAsync(StartBookImportRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ProjectId))
            throw new InvalidOperationException("projectId required");
        return StartBackgroundJobAsync(
            ct => RunBookImportAsync(req, ct),
            new JobEnqueueMeta
            {
                Kind = "book_import",
                ProjectId = req.ProjectId,
                Message = req.SkipPrepare
                    ? "Queued screenplay draft from book…"
                    : "Queued book import (prepare + screenplay)…",
            },
            lockResources: new[] { LockKeys.Stage(req.ProjectId) },
            lockReason: "book import");
    }

    private sealed class JobRunState
    {
        public JobSnapshot Snapshot { get; set; } = new() { Status = "idle" };
        public string? ActiveJobId { get; set; }
        public CancellationTokenSource Cts { get; set; } = new();
        public string UserId { get; set; } = "local";
        public string? ApiKey { get; set; }
        public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? StartedAt { get; set; }
        public bool UseLocalPool { get; set; }
        public List<string> HeldLocks { get; set; } = new();
        public List<string> PendingLockResources { get; set; } = new();
        public string? LockReason { get; set; }
        public SemaphoreSlim SnapLock { get; } = new(1, 1);
    }

    private async Task RunBookPrepareAsync(StartBookPrepareRequest req, CancellationToken ct)
    {
        var projectId = req.ProjectId;
        await _projects.ActivateAsync(projectId, ct);
        Snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "book_prepare",
            ProjectId = projectId,
            Message = "Preparing book (PDF extract / vision OCR)…",
            Index = 0,
            Total = 3,
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
                RegisterActiveJob();
        await PublishAsync();

        try
        {
            await AppendLogAsync("Book prepare (C# PdfPig + optional Grok vision)");
            var result = await _books.PrepareAsync(
                projectId,
                forceExtract: req.ForceExtract,
                forceVision: req.ForceVision,
                autoVision: req.AutoVision,
                visionModel: string.IsNullOrWhiteSpace(req.VisionModel) ? "grok-4.5" : req.VisionModel,
                onProgress: line =>
                {
                    _ = AppendLogAsync(line);
                    if (line.Contains("Extract", StringComparison.OrdinalIgnoreCase))
                        _ = UpdateAsync(s => { s.Index = 1; s.Message = line; });
                    else if (line.Contains("Vision", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("page", StringComparison.OrdinalIgnoreCase))
                        _ = UpdateAsync(s => { s.Index = Math.Max(s.Index, 2); s.Message = line; });
                    else
                        _ = UpdateAsync(s => s.Message = line);
                },
                ct: ct);

            await UpdateAsync(s => s.Index = 3);
            var msg = result.ReadyForStage1
                ? $"Book ready · {result.TextWords} words · quality={result.TextQuality} · {result.TextEngine}"
                : $"Book prepared but Stage 1 not ready · {result.Strategy}: {result.StrategyReason}";
            await FinishAsync(result.Ok ? "done" : "error", msg, result.Ok ? null : msg);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Book prepare failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task RunBookImportAsync(StartBookImportRequest req, CancellationToken ct)
    {
        var projectId = req.ProjectId;
        await _projects.ActivateAsync(projectId, ct).ConfigureAwait(false);

        // Progress: 0–4 prepare, 5–10 adapt (chunk messages bump index)
        Snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "book_import",
            ProjectId = projectId,
            Message = req.SkipPrepare
                ? "Writing screenplay from book…"
                : "Importing book (prepare + screenplay)…",
            Index = 0,
            Total = 10,
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        RegisterActiveJob();
        await PublishAsync().ConfigureAwait(false);

        try
        {
            var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
            var bookPath = Path.Combine(projectDir, "source", "book_full.txt");
            var needPrepare = !req.SkipPrepare;

            // TXT may already have book_full after upload; still allow force extract for PDF
            if (needPrepare && File.Exists(bookPath) && !req.ForceExtract && !req.ForceVision)
            {
                // Light skip if text already good and not forcing — still run prepare for PDF path consistency
                // Import always sets ForceExtract=true for PDF; SkipPrepare for re-draft only.
            }

            if (needPrepare)
            {
                await AppendLogAsync("Phase 1: prepare book text").ConfigureAwait(false);
                await UpdateAsync(s =>
                {
                    s.Index = 1;
                    s.Message = "Reading book…";
                }).ConfigureAwait(false);

                var prep = await _books.PrepareAsync(
                    projectId,
                    forceExtract: req.ForceExtract,
                    forceVision: req.ForceVision,
                    autoVision: req.AutoVision,
                    visionModel: string.IsNullOrWhiteSpace(req.VisionModel) ? "grok-4.5" : req.VisionModel,
                    onProgress: line =>
                    {
                        _ = AppendLogAsync(line);
                        _ = UpdateAsync(s =>
                        {
                            s.Message = line;
                            if (line.Contains("Extract", StringComparison.OrdinalIgnoreCase))
                                s.Index = Math.Max(s.Index, 2);
                            else if (line.Contains("Vision", StringComparison.OrdinalIgnoreCase) ||
                                     line.Contains("page", StringComparison.OrdinalIgnoreCase))
                                s.Index = Math.Max(s.Index, 3);
                            else
                                s.Index = Math.Max(s.Index, 2);
                        });
                    },
                    ct: ct).ConfigureAwait(false);

                if (!prep.Ok)
                {
                    await FinishAsync("error", prep.StrategyReason ?? "Book prepare failed",
                        prep.StrategyReason ?? "Book prepare failed").ConfigureAwait(false);
                    return;
                }

                await AppendLogAsync(
                    $"Book text ready · {prep.TextWords} words · {prep.TextEngine}").ConfigureAwait(false);
            }
            else
            {
                await AppendLogAsync("Skipping prepare — using existing book text").ConfigureAwait(false);
            }

            if (!File.Exists(bookPath))
            {
                await FinishAsync("error", "No book text after prepare",
                    "No book text after prepare").ConfigureAwait(false);
                return;
            }

            await UpdateAsync(s =>
            {
                s.Index = 5;
                s.Message = "Writing screenplay draft…";
            }).ConfigureAwait(false);
            await AppendLogAsync("Phase 2: book → Fountain screenplay").ConfigureAwait(false);

            if (!_chat.IsConfigured)
            {
                await FinishAsync("error", "Chat service not configured",
                    "Chat service not configured").ConfigureAwait(false);
                return;
            }

            var model = string.IsNullOrWhiteSpace(req.Model) ? "grok-4.5" : req.Model.Trim();
            var save = await ScreenplayService.CreateDraftFromBookAsync(
                _projects,
                projectId,
                _chat,
                model: model,
                onProgress: line =>
                {
                    _ = AppendLogAsync(line);
                    _ = UpdateAsync(s =>
                    {
                        s.Message = line;
                        // Map adapt progress into 5–9
                        if (line.Contains("chunk", StringComparison.OrdinalIgnoreCase))
                        {
                            var m = System.Text.RegularExpressions.Regex.Match(
                                line, @"(\d+)\s*/\s*(\d+)");
                            if (m.Success &&
                                int.TryParse(m.Groups[1].Value, out var cur) &&
                                int.TryParse(m.Groups[2].Value, out var tot) &&
                                tot > 0)
                            {
                                s.Index = 5 + (int)Math.Round(4.0 * Math.Clamp(cur, 0, tot) / tot);
                            }
                            else
                                s.Index = Math.Max(s.Index, 6);
                        }
                        else if (line.Contains("Merge", StringComparison.OrdinalIgnoreCase) ||
                                 line.Contains("Stitch", StringComparison.OrdinalIgnoreCase))
                            s.Index = Math.Max(s.Index, 9);
                        else if (line.Contains("repair", StringComparison.OrdinalIgnoreCase) ||
                                 line.Contains("retry", StringComparison.OrdinalIgnoreCase))
                            s.Index = Math.Max(s.Index, 8);
                        else
                            s.Index = Math.Max(s.Index, 6);
                    });
                },
                ct: ct).ConfigureAwait(false);

            if (!save.Ok)
            {
                await FinishAsync("error", save.Error ?? "Screenplay draft failed",
                    save.Error ?? "Screenplay draft failed").ConfigureAwait(false);
                return;
            }

            await UpdateAsync(s => s.Index = 10).ConfigureAwait(false);
            await FinishAsync(
                "done",
                save.Message ?? "Screenplay draft ready — review and approve").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Book import failed");
            await FinishAsync("error", ex.Message, ex.Message).ConfigureAwait(false);
        }
    }

    /// <summary>Generate portrait variants via C# Grok image API.</summary>
    public Task<JobSnapshot> StartCharacterVariantsAsync(StartCharacterVariantsRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.CharKey))
            throw new InvalidOperationException("charKey required");
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        return StartBackgroundJobAsync(
            ct => RunCharacterVariantsAsync(req, ct),
            new JobEnqueueMeta
            {
                Kind = "character_variants",
                ProjectId = projectId,
                CharKey = req.CharKey,
                Message = $"Queued portrait gen for {req.CharKey}…",
            },
            lockResources: new[] { LockKeys.Character(projectId, req.CharKey) },
            lockReason: $"char variants {req.CharKey}");
    }

    /// <summary>
    /// Short Grok video with VOICE LOCK + dialogue, extract MP3 for Characters Play sample.
    /// </summary>
    public Task<JobSnapshot> StartVoicePreviewAsync(StartVoicePreviewRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.CharKey))
            throw new InvalidOperationException("charKey required");
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        return StartBackgroundJobAsync(
            ct => RunVoicePreviewAsync(req, ct),
            new JobEnqueueMeta
            {
                Kind = "voice-preview",
                ProjectId = projectId,
                CharKey = req.CharKey,
                Message = req.Force
                    ? $"Queued voice regenerate for {req.CharKey}…"
                    : $"Queued voice sample for {req.CharKey}…",
            },
            lockResources: new[] { LockKeys.Character(projectId, req.CharKey) },
            lockReason: $"voice preview {req.CharKey}");
    }

    /// <summary>AI per-clip review (frames + prev tail) → draft suggestions for Apply → Regen.</summary>
    public Task<JobSnapshot> StartClipAutoReviewAsync(StartClipAutoReviewRequest req)
    {
        if (req.Scene <= 0 || req.Clip <= 0)
            throw new InvalidOperationException("scene and clip required");
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        return StartBackgroundJobAsync(
            ct => RunClipAutoReviewAsync(req, ct),
            new JobEnqueueMeta
            {
                Kind = "clip-auto-review",
                ProjectId = projectId,
                Scene = req.Scene,
                Clip = req.Clip,
                Message = $"Queued AI review S{req.Scene:D2}C{req.Clip:D2}…",
            },
            lockResources: new[] { LockKeys.Scene(projectId, req.Scene) },
            lockReason: $"auto-review S{req.Scene:D2}C{req.Clip:D2}");
    }

    /// <summary>Batch AI review: walk on-disk clips (optional scene filter; onlyMissing skips existing drafts).</summary>
    public Task<JobSnapshot> StartClipAutoReviewBatchAsync(StartClipAutoReviewBatchRequest req)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("projectId required");

        var sceneLabel = req.Scene is int sn && sn > 0 ? $"S{sn:D2}" : "all scenes";
        var mode = req.OnlyMissing ? "missing only" : "all clips";
        return StartBackgroundJobAsync(
            ct => RunClipAutoReviewBatchAsync(req, ct),
            new JobEnqueueMeta
            {
                Kind = "clip-auto-review-batch",
                ProjectId = projectId,
                Scene = req.Scene is int s && s > 0 ? s : null,
                Message = $"Queued batch auto-review ({sceneLabel}, {mode})…",
            },
            lockResources: req.Scene is int one && one > 0
                ? new[] { LockKeys.Scene(projectId, one) }
                : new[] { LockKeys.Stage(projectId) },
            lockReason: $"auto-review-batch {sceneLabel}");
    }

    private async Task RunClipAutoReviewAsync(StartClipAutoReviewRequest req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        await _projects.ActivateAsync(projectId, ct);

        Snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "clip-auto-review",
            ProjectId = projectId,
            Scene = req.Scene,
            Clip = req.Clip,
            Message = $"Reviewing S{req.Scene:D2}C{req.Clip:D2}…",
            Index = 0,
            Total = 100,
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        RegisterActiveJob();
        await PublishAsync();

        try
        {
            await AppendLogAsync(
                "AI review = sample prev tail + this clip → draft suggestions (no auto-apply)");
            var draft = await _clipAutoReview.ReviewAsync(
                projectId,
                req.Scene,
                req.Clip,
                onProgress: (index, total, line) =>
                {
                    _ = AppendLogAsync(line);
                    _ = UpdateAsync(s =>
                    {
                        s.Index = Math.Clamp(index, 0, Math.Max(1, total));
                        s.Total = Math.Max(1, total);
                        s.Message = line;
                    });
                },
                ct: ct);

            await AppendLogAsync(
                $"Draft: {draft.Suggestion}/{draft.Category} · {draft.Suggestions.Count} suggestion(s)");
            await FinishAsync(
                "done",
                $"Review ready S{req.Scene:D2}C{req.Clip:D2} — {draft.Suggestion} ({draft.Suggestions.Count} suggestions)");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Clip review cancelled");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Clip auto-review failed S{Scene}C{Clip}", req.Scene, req.Clip);
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task RunClipAutoReviewBatchAsync(StartClipAutoReviewBatchRequest req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        await _projects.ActivateAsync(projectId, ct);

        var coords = _reviewIndex.ListOnDiskClipCoords(projectId, req.Scene)
            .Where(c => !req.OnlyMissing || !_reviewIndex.HasDraft(projectId, c.Scene, c.Clip))
            .ToList();

        Snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "clip-auto-review-batch",
            ProjectId = projectId,
            Scene = req.Scene is int s0 && s0 > 0 ? s0 : null,
            Message = coords.Count == 0
                ? "No clips to auto-review"
                : $"Batch reviewing {coords.Count} clip(s)…",
            Index = 0,
            Total = Math.Max(1, coords.Count),
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        RegisterActiveJob();
        await PublishAsync();

        try
        {
            if (coords.Count == 0)
            {
                try { _reviewIndex.Rebuild(projectId, req.Scene); } catch { /* non-fatal */ }
                await FinishAsync("done", "Batch auto-review: nothing to do (no missing drafts)");
                return;
            }

            await AppendLogAsync(
                $"Batch auto-review: {coords.Count} clip(s)" +
                (req.OnlyMissing ? " (only missing drafts)" : " (all)") +
                (req.Scene is int sn && sn > 0 ? $" scene S{sn:D2}" : ""));

            var ok = 0;
            var failed = 0;
            for (var i = 0; i < coords.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (scene, clip) = coords[i];
                await UpdateAsync(s =>
                {
                    s.Index = i;
                    s.Total = coords.Count;
                    s.Scene = scene;
                    s.Clip = clip;
                    s.Message = $"Reviewing S{scene:D2}C{clip:D2} ({i + 1}/{coords.Count})…";
                });
                await AppendLogAsync($"--- S{scene:D2}C{clip:D2} ({i + 1}/{coords.Count}) ---");

                try
                {
                    var draft = await _clipAutoReview.ReviewAsync(
                        projectId,
                        scene,
                        clip,
                        onProgress: (index, total, line) =>
                        {
                            _ = AppendLogAsync($"  {line}");
                        },
                        ct: ct);
                    ok++;
                    await AppendLogAsync(
                        $"  → {draft.Suggestion}/{draft.Category} · {draft.Suggestions.Count} suggestion(s)");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    _log.LogWarning(ex, "Batch auto-review failed S{Scene}C{Clip}", scene, clip);
                    await AppendLogAsync($"  → ERROR: {ex.Message}");
                }
            }

            try
            {
                var index = _reviewIndex.Rebuild(projectId, req.Scene);
                await AppendLogAsync(
                    $"Review index rebuilt: {index.Clips.Count} row(s) → assets/review/index.json");
            }
            catch (Exception ex)
            {
                await AppendLogAsync($"Review index rebuild skipped: {ex.Message}");
            }

            await FinishAsync(
                "done",
                $"Batch auto-review done: {ok} ok, {failed} failed of {coords.Count}");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Batch auto-review cancelled");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Batch auto-review failed for {ProjectId}", projectId);
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task RunVoicePreviewAsync(StartVoicePreviewRequest req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        await _projects.ActivateAsync(projectId, ct);

        Snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "voice-preview",
            ProjectId = projectId,
            CharKey = req.CharKey,
            Message = req.Force
                ? $"Regenerating voice for {req.CharKey}…"
                : $"Generating voice sample for {req.CharKey}…",
            Index = 0,
            Total = 100,
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
        RegisterActiveJob();
        await PublishAsync();

        try
        {
            await AppendLogAsync(
                "Voice sample = short film video (VOICE LOCK + dialogue) → audio only");

            var path = await _voicePreview.GenerateAsync(
                projectId,
                req.CharKey,
                req.VoiceProfile,
                req.VoiceLabel,
                req.DisplayName,
                req.SampleText,
                force: req.Force,
                onProgress: (index, total, line) =>
                {
                    _ = AppendLogAsync(line);
                    _ = UpdateAsync(s =>
                    {
                        s.Index = Math.Clamp(index, 0, Math.Max(1, total));
                        s.Total = Math.Max(1, total);
                        s.Message = line;
                    });
                },
                ct: ct);

            await AppendLogAsync($"Saved {Path.GetFileName(path)}");
            await FinishAsync("done", $"Voice sample ready for {req.CharKey}");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Voice sample cancelled");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Voice preview failed for {Char}", req.CharKey);
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    /// <summary>
    /// Grok vision: classify book images → which characters appear, write plates to scenes.json.
    /// Cancellable. Falls back to heuristics if no API key.
    /// </summary>
    public Task<JobSnapshot> StartSortCharacterPlatesAsync(AttachCharacterPlatesRequest req)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        return StartBackgroundJobAsync(
            ct => RunSortCharacterPlatesAsync(req, ct),
            new JobEnqueueMeta
            {
                Kind = "character-plates",
                ProjectId = projectId,
                Message = "Queued character plate sort…",
            },
            lockResources: new[] { LockKeys.Stage(projectId) },
            lockReason: "character plates");
    }

    private async Task RunSortCharacterPlatesAsync(AttachCharacterPlatesRequest req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        await _projects.ActivateAsync(projectId, ct);

        Snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "character-plates",
            ProjectId = projectId,
            Message = req.UseGrok
                ? "Sorting book images onto characters with Grok vision…"
                : "Sorting book images onto characters (heuristic)…",
            Index = 0,
            Total = Math.Max(1, req.MaxImages),
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
                RegisterActiveJob();
        await PublishAsync();

        try
        {
            await AppendLogAsync(
                req.UseGrok
                    ? "Character plate sort (Grok vision classifies who appears on each page)"
                    : "Character plate sort (heuristic only)");

            var result = await _plates.AttachAsync(
                projectId,
                force: true, // job is always an explicit re-sort from UI
                copyIntoAssets: req.CopyIntoAssets,
                onlyCharKey: req.CharKey,
                useGrok: req.UseGrok,
                visionModel: string.IsNullOrWhiteSpace(req.VisionModel) ? "grok-4.5" : req.VisionModel,
                maxImages: req.MaxImages > 0 ? req.MaxImages : 32,
                onProgress: line =>
                {
                    _ = AppendLogAsync(line);
                    // "Grok vision 3/20: …"
                    var m = System.Text.RegularExpressions.Regex.Match(
                        line, @"Grok vision\s+(\d+)/(\d+)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success &&
                        int.TryParse(m.Groups[1].Value, out var i) &&
                        int.TryParse(m.Groups[2].Value, out var t))
                    {
                        _ = UpdateAsync(s =>
                        {
                            s.Index = i;
                            s.Total = t;
                            s.Message = line;
                        });
                    }
                    else
                        _ = UpdateAsync(s => s.Message = line);
                },
                ct: ct);

            if (result.AlreadySorted)
            {
                await FinishAsync("done", $"Already sorted ({result.SortedAt})");
                return;
            }

            if (!result.Ok && !string.IsNullOrEmpty(result.Reason))
            {
                await FinishAsync("error", result.Reason, result.Reason);
                return;
            }

            await UpdateAsync(s =>
            {
                s.Index = Math.Max(s.Index, result.ImagesClassified);
                if (result.ImagesClassified > 0)
                    s.Total = Math.Max(s.Total, result.ImagesClassified);
            });
            await AppendLogAsync(
                $"method={result.Method} updated={result.CharactersUpdated} " +
                $"skipped={result.CharactersSkipped} classified={result.ImagesClassified} " +
                $"text_skipped={result.ImagesSkippedText}");
            await FinishAsync(
                "done",
                $"Plates sorted ({result.Method}): {result.CharactersUpdated} character(s) updated");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Character plate sort failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    /// <summary>Lock/unlock character reference images (locks run vision style gate).</summary>
    public async Task<string> RunCharacterDesignActionAsync(
        string projectId,
        string action,
        string charKey,
        int variantIndex = 1,
        string? imagePath = null,
        CancellationToken ct = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("A generation job is already running.");

        ct.ThrowIfCancellationRequested();
        return action switch
        {
            "lock-variant" =>
                await _characters.LockVariantAsync(
                    projectId, charKey, Math.Clamp(variantIndex, 1, 3), ct).ConfigureAwait(false),
            "lock-image" when !string.IsNullOrWhiteSpace(imagePath) =>
                await _characters.LockFromPathAsync(
                    projectId,
                    charKey,
                    ResolveLockImagePath(projectId, imagePath!),
                    ct).ConfigureAwait(false),
            "lock-bookref" =>
                await _characters.LockBookRefAsync(
                    projectId, charKey, Math.Max(0, variantIndex), ct).ConfigureAwait(false),
            "unlock" =>
                _characters.Unlock(projectId, charKey)
                    ? $"Unlocked {charKey} — previous lock kept as variant 1 (best so far)"
                    : $"No locked ref for {charKey}",
            _ => throw new InvalidOperationException($"Unknown character action: {action}"),
        };
    }

    private string ResolveLockImagePath(string projectId, string imagePath)
    {
        if (File.Exists(imagePath))
            return Path.GetFullPath(imagePath);
        var projectDir = _projects.GetProjectDir(projectId);
        var cand = Path.Combine(projectDir, imagePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(cand))
            return Path.GetFullPath(cand);
        throw new InvalidOperationException($"Image not found: {imagePath}");
    }

    private async Task RunCharacterVariantsAsync(StartCharacterVariantsRequest req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        await _projects.ActivateAsync(projectId, ct);

        Snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "character",
            ProjectId = projectId,
            CharKey = req.CharKey,
            Message = $"Generating portraits for {req.CharKey}…",
            Index = 0,
            Total = 3,
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
                RegisterActiveJob();
        await PublishAsync();

        try
        {
            await AppendLogAsync($"Character design (C# / Grok image API) for {req.CharKey}");
            await UpdateAsync(s => s.Message = "Resolving refs + design prompt…");

            var result = await _characters.GenerateVariantsAsync(
                projectId,
                req.CharKey,
                n: req.Count,
                seedOptions: req,
                onProgress: line =>
                {
                    _ = AppendLogAsync(line);
                    var idx = TryParseVariantProgress(line);
                    if (idx > 0)
                        _ = UpdateAsync(s => { s.Index = idx; s.Message = line; });
                    else if (line.Contains("generating", StringComparison.OrdinalIgnoreCase))
                    {
                        // "generating 1 variant(s)" / "generating 3 variants"
                        var m = System.Text.RegularExpressions.Regex.Match(line, @"generating\s+(\d+)");
                        if (m.Success && int.TryParse(m.Groups[1].Value, out var total) && total > 0)
                            _ = UpdateAsync(s => { s.Total = total; s.Message = line; });
                        else
                            _ = UpdateAsync(s => s.Message = line);
                    }
                    else if (line.Contains("edit variant", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("Grok", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("book ref", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("ref image", StringComparison.OrdinalIgnoreCase))
                        _ = UpdateAsync(s =>
                        {
                            s.Index = Math.Max(s.Index, 1);
                            s.Message = line;
                        });
                },
                ct: ct);

            await UpdateAsync(s =>
            {
                s.Index = result.Paths.Count;
                s.Total = Math.Max(s.Total, result.Paths.Count);
            });
            await AppendLogAsync(
                $"mode={result.Mode} · {result.Paths.Count} file(s)" +
                (result.BookRefs.Count > 0
                    ? $" · book refs: {string.Join(", ", result.BookRefs)}"
                    : ""));
            await FinishAsync(
                "done",
                $"Variants ready for {req.CharKey} ({result.Mode}, {result.Paths.Count} image(s))");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Character variants failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private static int TryParseVariantProgress(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            line, @"variant[_\s-]*0*([1-3])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
            return n;
        m = System.Text.RegularExpressions.Regex.Match(line, @"\b([1-3])\s*/\s*3\b");
        if (m.Success && int.TryParse(m.Groups[1].Value, out n))
            return n;
        m = System.Text.RegularExpressions.Regex.Match(
            line, @"saved variant\s+([1-3])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out n))
            return n;
        return 0;
    }

    private async Task RunStage1Async(StartStage1Request req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        await _projects.ActivateAsync(projectId, ct);

        Snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "stage1",
            ProjectId = projectId,
            Message = "Building screenplay from book → Fountain…",
            Index = 0,
            Total = 0,
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
                RegisterActiveJob();
        await PublishAsync();

        try
        {
            await AppendLogAsync("Screenplay: book → Fountain (prompts/book_to_fountain.txt) → approve");
            // Sequential progress pump — no GetAwaiter; preserves line order for SignalR
            var progress = System.Threading.Channels.Channel.CreateUnbounded<string>(
                new System.Threading.Channels.UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                });
            var progressPump = Task.Run(async () =>
            {
                await foreach (var line in progress.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    await ReportStage1ProgressAsync(line).ConfigureAwait(false);
            }, CancellationToken.None);

            Stage1Result result;
            try
            {
                result = await _stage1.RunAsync(
                    projectId,
                    chunkPages: Math.Clamp(req.ChunkPages, 5, 30),
                    totalMinutes: req.TotalMinutes,
                    model: string.IsNullOrWhiteSpace(req.Model) ? "grok-4.5" : req.Model,
                    resume: req.Resume,
                    maxChunks: req.MaxChunks,
                    onProgress: line => progress.Writer.TryWrite(line),
                    ct: ct);
            }
            finally
            {
                progress.Writer.TryComplete();
                try { await progressPump.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* job cancelled */ }
            }

            var msg =
                $"Screenplay ready: {result.SceneCount} scenes · {result.CharacterCount} cast · " +
                $"{result.LocationCount} locations · V.O. {result.VoCueCount}/{result.TotalDialogueCues} ({result.VoPercent}%)";
            if (result.TotalDialogueCues > 0 && result.VoPercent >= 45)
                msg += " — narration-heavy (clip gen will lean on V.O.)";
            if (result.HardErrors.Count > 0)
                msg += $" · {result.HardErrors.Count} issue(s)";
            await FinishAsync(result.Ok || result.SceneCount > 0 ? "done" : "error", msg,
                result.Ok ? null : string.Join("; ", result.HardErrors.Take(3)));
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Stage 1 failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task RunStage2Async(StartStage2Request req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        await _projects.ActivateAsync(projectId, ct);

        Snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "stage2",
            ProjectId = projectId,
            Message = "Starting Stage 2 planner (C#)…",
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
                RegisterActiveJob();
        await PublishAsync();

        try
        {
            await AppendLogAsync("Stage 2: building shot plan from screenplay");
            ct.ThrowIfCancellationRequested();
            var resolution = await ResolveVideoResolutionAsync(projectId, req.Resolution, ct);
            var result = await _stage2.PlanAsync(
                projectId,
                resolution: resolution,
                scenes: string.IsNullOrWhiteSpace(req.Scenes) ? "all" : req.Scenes,
                onProgress: line =>
                {
                    _ = AppendLogAsync(line);
                    _ = UpdateAsync(s => s.Message = line);
                },
                ct: ct);

            await FinishAsync(
                "done",
                $"Stage 2 complete: {result.SceneCount} scenes · {result.ClipCount} clips · ~{result.DurationSeconds}s");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Stage 2 failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    public Task<JobSnapshot> StartRemuxAsync(StartRemuxRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ProjectId))
            throw new InvalidOperationException("projectId required");
        var locks = new List<string>();
        if (req.Scene is int sn && sn > 0)
            locks.Add(LockKeys.Scene(req.ProjectId, sn));
        if (req.RebuildWip || req.RefreshStaleScenes)
            locks.Add(LockKeys.Wip(req.ProjectId));
        if (locks.Count == 0 && req.Scene is int sn2 && sn2 > 0)
            locks.Add(LockKeys.Scene(req.ProjectId, sn2));
        if (locks.Count == 0)
            locks.Add(LockKeys.Wip(req.ProjectId));
        return StartBackgroundJobAsync(
            ct => RunRemuxAsync(req, ct),
            new JobEnqueueMeta
            {
                Kind = "remux",
                ProjectId = req.ProjectId,
                Scene = req.Scene,
                Message = "Queued remux / WIP…",
            },
            lockResources: locks,
            lockReason: "remux/wip",
            useLocalPool: true,
            failIfLocked: req.FailIfLocked);
    }

    private async Task RunRemuxAsync(StartRemuxRequest req, CancellationToken ct)
    {
        var projectId = req.ProjectId;
        await _projects.ActivateAsync(projectId, ct);
        Snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "remux",
            ProjectId = projectId,
            Scene = req.Scene,
            Message = "Remux / WIP…",
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
                RegisterActiveJob();
        await PublishAsync();
        try
        {
            if (!_remux.IsAvailable())
                throw new InvalidOperationException(
                    "ffmpeg not found. Install ffmpeg and ensure it is on PATH (or set FilmStudio:FfmpegPath).");

            var ignoreGate = req.IgnoreAssemblyGate;
            if (ignoreGate)
            {
                if (!EditLogService.IsValidAutoFailOverrideNote(req.IgnoreAssemblyGateReason))
                {
                    throw new InvalidOperationException(
                        "ignoreAssemblyGate requires IgnoreAssemblyGateReason " +
                        $"(≥{EditLogService.MinAutoFailOverrideNoteLength} chars, not just pass/ok).");
                }
                await AppendLogAsync(
                    $"WARNING: assembly gate disabled for this remux — {req.IgnoreAssemblyGateReason!.Trim()}");
            }

            var refreshed = 0;
            if (req.RefreshStaleScenes || req.RebuildWip)
            {
                // Before WIP: remux stale scenes AND any scene with assembly-blocked clips
                // so composites no longer embed failed auto-review / human-fail clips.
                var fresh = _projects.AssessWipFreshness(projectId);
                var toRemux = new SortedSet<int>(fresh.StaleScenes);
                if (!ignoreGate)
                {
                    var blocked = _editLogs.ListBlockedClipsOnDisk(projectId);
                    foreach (var sn in blocked.Select(x => x.Scene).Distinct())
                        toRemux.Add(sn);
                    if (blocked.Count > 0)
                    {
                        await AppendLogAsync(
                            $"Assembly gate: {blocked.Count} blocked clip(s) — " +
                            string.Join(", ", blocked.Select(x =>
                                $"S{x.Scene:D2}C{x.Clip:D2}")) +
                            " excluded from scene remux");
                    }
                }

                if (toRemux.Count > 0 || req.RefreshStaleScenes)
                {
                    await AppendLogAsync(
                        toRemux.Count > 0
                            ? $"Remuxing {toRemux.Count} scene composite(s) before WIP…"
                            : "No scenes to remux — stitching WIP from current composites");
                    var i = 0;
                    var remuxErrors = new List<string>();
                    foreach (var sn in toRemux)
                    {
                        ct.ThrowIfCancellationRequested();
                        i++;
                        await UpdateAsync(s =>
                        {
                            s.Scene = sn;
                            s.Index = i;
                            s.Total = toRemux.Count;
                            s.Message = $"Remux S{sn:D2} ({i}/{toRemux.Count})…";
                        });
                        try
                        {
                            await _remux.RemuxSceneAsync(projectId, sn,
                                line => { _ = OnRemuxProgressAsync(line); }, ct,
                                ignoreAssemblyGate: ignoreGate);
                            refreshed++;
                            await AppendLogAsync($"Remuxed S{sn:D2}");
                        }
                        catch (Exception ex)
                        {
                            // Loud failure — do not stitch WIP from a partial remux set.
                            remuxErrors.Add($"S{sn:D2}: {ex.Message}");
                            _log.LogWarning(ex, "Remux S{Scene} failed", sn);
                            await AppendLogAsync($"S{sn:D2} remux failed: {ex.Message}");
                        }
                    }

                    if (remuxErrors.Count > 0)
                    {
                        throw new InvalidOperationException(
                            $"Scene remux incomplete ({refreshed}/{toRemux.Count} ok) — " +
                            string.Join("; ", remuxErrors) +
                            (req.RebuildWip ? " (WIP not rebuilt)" : ""));
                    }
                }
            }
            else if (req.Scene is int sn && sn > 0)
            {
                await _remux.RemuxSceneAsync(projectId, sn,
                    line => { _ = OnRemuxProgressAsync(line); }, ct,
                    ignoreAssemblyGate: ignoreGate);
            }

            if (req.RebuildWip)
            {
                await UpdateAsync(s =>
                {
                    s.Scene = null;
                    s.Message = "Stitching WIP from scene composites…";
                });
                await _remux.RebuildWipAsync(projectId,
                    line => { _ = OnRemuxProgressAsync(line); }, ct);
            }

            var doneMsg = (req.RefreshStaleScenes, req.Scene is int dsn && dsn > 0, req.RebuildWip) switch
            {
                (true, _, true) when refreshed > 0 =>
                    $"Remuxed {refreshed} stale scene(s) + WIP rebuilt",
                (true, _, true) => "WIP rebuilt (no stale scenes)",
                (false, true, true) => $"Scene S{req.Scene:D2} composite + WIP rebuilt",
                (false, true, false) => $"Scene S{req.Scene:D2} composite rebuilt (WIP unchanged)",
                (false, false, true) => "WIP movie rebuilt from scene composites",
                _ => "Remux complete (nothing to do — pass scene and/or rebuildWip)",
            };
            await FinishAsync("done", doneMsg);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Remux failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task RunBatchGenAsync(StartBatchGenRequest req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        await _projects.ActivateAsync(projectId, ct);

        var hasClips = req.Clips is { Count: > 0 };
        var scenes = (hasClips ? req.Clips!.Select(c => c.Scene) : req.Scenes)
            .Distinct().OrderBy(s => s).ToList();
        Snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "batch",
            ProjectId = projectId,
            Message = hasClips
                ? $"Batch: {req.Clips!.Count} clip(s)…"
                : $"Batch: {scenes.Count} scene(s)…",
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
                RegisterActiveJob();
        await PublishAsync();

        try
        {
            await EnsureVideoProviderConfiguredAsync(projectId, ct).ConfigureAwait(false);

            using var bp = await _projects.LoadBlueprintAsync(projectId, ct)
                ?? throw new InvalidOperationException(
                    $"No Stage 2 blueprint for project {projectId}. Run Stage 2 first.");

            if (req.RequireLockedCharacters)
            {
                // Project-wide first (all cast voice + locked images), then per-scene mentions.
                EnsureCastReadyForVideo(projectId);
                foreach (var sn in scenes)
                    EnsureSceneCharactersLocked(projectId, sn);
            }

            var projectDir = _projects.GetProjectDir(projectId);
            Directory.CreateDirectory(Path.Combine(projectDir, "assets", "video"));

            // Pre-count work units
            var work = new List<(int Scene, int Clip, JsonElement ClipEl)>();
            if (hasClips)
            {
                // Explicit multi-select of specific clips — always force-regen (ignore OnlyMissing),
                // same as single-clip regen.
                foreach (var target in req.Clips!.OrderBy(c => c.Scene).ThenBy(c => c.Clip))
                {
                    var sceneEl = FindScene(bp.RootElement, target.Scene);
                    if (sceneEl is null)
                    {
                        await AppendLogAsync($"Scene {target.Scene}: not in blueprint — skip");
                        continue;
                    }
                    var clipEl = FindClipInScene(sceneEl.Value, target.Clip);
                    if (clipEl is null)
                    {
                        await AppendLogAsync($"S{target.Scene:D2}C{target.Clip}: not in blueprint — skip");
                        continue;
                    }
                    work.Add((Scene: target.Scene, Clip: target.Clip, ClipEl: clipEl.Value.Clone()));
                }
            }
            else
            {
                foreach (var sn in scenes)
                {
                    var sceneEl = FindScene(bp.RootElement, sn);
                    if (sceneEl is null)
                    {
                        await AppendLogAsync($"Scene {sn}: not in blueprint — skip");
                        continue;
                    }
                    if (!sceneEl.Value.TryGetProperty("veo_clips", out var clipsEl) ||
                        clipsEl.ValueKind != JsonValueKind.Array)
                    {
                        await AppendLogAsync($"Scene {sn}: no veo_clips — skip");
                        continue;
                    }

                    foreach (var c in clipsEl.EnumerateArray())
                    {
                        var cn = c.TryGetProperty("clip_number", out var n) && n.TryGetInt32(out var v) ? v : 0;
                        if (cn <= 0) continue;
                        var path = Path.Combine(projectDir, "assets", "video", $"scene_{sn:D2}_clip_{cn:D2}.mp4");
                        var missing = !File.Exists(path) || new FileInfo(path).Length < 1024;
                        if (!req.OnlyMissing || missing)
                            work.Add((Scene: sn, Clip: cn, ClipEl: c.Clone()));
                    }
                }
            }

            if (work.Count == 0)
            {
                await AppendLogAsync("Batch: nothing to generate (only_missing).");
                await FinishAsync("done", "No clips to generate");
                return;
            }

            // Fail before any API spend if the selected video model cannot do multi-clip / plates.
            await EnsureVideoModelCapabilitiesAsync(
                    projectId,
                    needContinue: work.Any(w => w.Clip > 1),
                    needReferenceImages: req.RequireLockedCharacters,
                    ct)
                .ConfigureAwait(false);

            var resolution = await ResolveVideoResolutionAsync(projectId, req.Resolution, ct);
            await UpdateAsync(s =>
            {
                s.Total = work.Count;
                s.Index = 0;
                s.Message = $"Batch: {work.Count} clip(s) across {scenes.Count} scene(s) @ {resolution}";
            });
            await AppendLogAsync(Snapshot.Message!);

            var done = 0;
            var failed = 0;
            for (var i = 0; i < work.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (sn, cn, clip) = work[i];
                await UpdateAsync(s =>
                {
                    s.Index = i + 1;
                    s.Scene = sn;
                    s.Clip = cn;
                    s.Message = $"Generating S{sn:D2} C{cn} ({i + 1}/{work.Count})…";
                });
                await AppendLogAsync(Snapshot.Message!);

                try
                {
                    // Previous clip element in same scene (for prompt context)
                    JsonElement? prevClipEl = null;
                    if (cn > 1)
                    {
                        var sceneEl = FindScene(bp.RootElement, sn);
                        if (sceneEl is not null)
                            prevClipEl = FindClipInScene(sceneEl.Value, cn - 1);
                    }

                    await GenerateOneClipAsync(
                        projectId, projectDir, sn, cn, clip, resolution, ct,
                        previousClipEl: prevClipEl,
                        blueprintRoot: bp.RootElement);
                    done++;
                    await AppendLogAsync($"Done S{sn:D2} C{cn}");
                }
                catch (OperationCanceledException)
                {
                    await FinishAsync("cancelled", "Cancelled by user");
                    return;
                }
                catch (Exception ex)
                {
                    failed++;
                    _log.LogError(ex, "Clip S{Scene}C{Clip} failed", sn, cn);
                    await AppendLogAsync($"Failed S{sn:D2} C{cn}: {ex.Message}");
                }
            }

            var status = failed > 0 && done == 0 ? "error"
                : failed > 0 ? "partial"
                : "done";
            var msg = status switch
            {
                "error" => $"Batch failed ({failed} clip(s) failed, none ok)",
                "partial" => $"Batch partial ({done} ok, {failed} failed)",
                _ => $"Batch finished ({done} clip(s))",
            };
            await FinishAsync(status, msg, failed > 0 ? msg : null);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Batch gen failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    private async Task RunSceneGenAsync(StartSceneGenRequest req, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        await _projects.ActivateAsync(projectId, ct);

        Snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "scene",
            ProjectId = projectId,
            Scene = req.Scene,
            Message = "Starting…",
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
                RegisterActiveJob();
        await PublishAsync();

        try
        {
            await EnsureVideoProviderConfiguredAsync(projectId, ct).ConfigureAwait(false);

            using var bp = await _projects.LoadBlueprintAsync(projectId, ct)
                ?? throw new InvalidOperationException(
                    $"No Stage 2 blueprint for project {projectId}. Run Stage 2 first.");

            var sceneEl = FindScene(bp.RootElement, req.Scene)
                ?? throw new InvalidOperationException($"Scene {req.Scene} not in blueprint.");

            if (req.RequireLockedCharacters)
            {
                EnsureCastReadyForVideo(projectId);
                EnsureSceneCharactersLocked(projectId, req.Scene);
            }

            if (!sceneEl.TryGetProperty("veo_clips", out var clipsEl) ||
                clipsEl.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"Scene {req.Scene} has no veo_clips.");
            }

            var clips = clipsEl.EnumerateArray().ToList();
            var projectDir = _projects.GetProjectDir(projectId);
            var videoDir = Path.Combine(projectDir, "assets", "video");
            Directory.CreateDirectory(videoDir);

            var todo = new List<(int ClipNum, JsonElement Clip)>();
            foreach (var c in clips)
            {
                var cn = c.TryGetProperty("clip_number", out var n) && n.TryGetInt32(out var v) ? v : 0;
                if (cn <= 0) continue;
                if (req.Clip is int onlyClip && onlyClip > 0 && cn != onlyClip)
                    continue;
                var path = Path.Combine(videoDir, $"scene_{req.Scene:D2}_clip_{cn:D2}.mp4");
                var missing = !File.Exists(path) || new FileInfo(path).Length < 1024;
                if (!req.OnlyMissing || missing)
                    todo.Add((cn, c.Clone()));
            }

            if (todo.Count == 0)
            {
                await AppendLogAsync($"Scene {req.Scene}: nothing to generate (only_missing).");
                await FinishAsync("done", "No clips to generate");
                return;
            }

            // Fail before any API spend if the selected video model cannot do multi-clip / plates.
            await EnsureVideoModelCapabilitiesAsync(
                    projectId,
                    needContinue: todo.Any(t => t.ClipNum > 1),
                    needReferenceImages: req.RequireLockedCharacters,
                    ct)
                .ConfigureAwait(false);

            var resolution = await ResolveVideoResolutionAsync(projectId, req.Resolution, ct);
            var startMsg = $"Scene {req.Scene}: {todo.Count} clip(s) @ {resolution}";
            await UpdateAsync(s =>
            {
                s.Total = todo.Count;
                s.Index = 0;
                s.Message = startMsg;
            });
            await AppendLogAsync(startMsg);

            var done = 0;
            var failed = 0;
            for (var i = 0; i < todo.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (cn, clip) = todo[i];
                await UpdateAsync(s =>
                {
                    s.Index = i + 1;
                    s.Clip = cn;
                    s.Message = $"Generating S{req.Scene:D2} C{cn} ({i + 1}/{todo.Count})…";
                });
                await AppendLogAsync(Snapshot.Message!);

                try
                {
                    JsonElement? prevClipEl = null;
                    if (cn > 1)
                    {
                        foreach (var (pcn, pclip) in todo)
                        {
                            if (pcn == cn - 1) { prevClipEl = pclip; break; }
                        }
                        // Also scan full scene clips for prev not in todo
                        if (prevClipEl is null)
                            prevClipEl = FindClipInScene(sceneEl, cn - 1);
                    }

                    await GenerateOneClipAsync(
                        projectId, projectDir, req.Scene, cn, clip, resolution, ct,
                        previousClipEl: prevClipEl,
                        blueprintRoot: bp.RootElement);
                    done++;
                    await AppendLogAsync($"Done S{req.Scene:D2} C{cn}");
                }
                catch (OperationCanceledException)
                {
                    await FinishAsync("cancelled", "Cancelled by user");
                    return;
                }
                catch (Exception ex)
                {
                    failed++;
                    _log.LogError(ex, "Clip S{Scene}C{Clip} failed", req.Scene, cn);
                    await AppendLogAsync($"Failed S{req.Scene:D2} C{cn}: {ex.Message}");
                    // Full-scene sequential gen: later clips need previous on disk — stop after first fail.
                    // Single-clip regen (req.Clip set) keeps trying only that one clip (already filtered).
                    if (req.Clip is null or <= 0 && i + 1 < todo.Count)
                    {
                        await AppendLogAsync(
                            "Stopping scene gen after first clip failure " +
                            $"(remaining {todo.Count - i - 1} clip(s) need previous video).");
                        break;
                    }
                }
            }

            // partial = some clips ok, some failed (not "done" — remux/continue need a clear signal)
            var status = failed > 0 && done == 0 ? "error"
                : failed > 0 ? "partial"
                : "done";
            var msg = status switch
            {
                "error" => $"Scene gen failed ({failed} clip(s) failed, none ok)",
                "partial" => $"Scene gen partial ({done} ok, {failed} failed)",
                _ => $"Generation finished ({done} clip(s))",
            };
            await FinishAsync(status, msg, failed > 0 ? msg : null);

            // P0 learning: single-clip regen (typical after auto-review apply)
            if (req.Clip is int regenClip && regenClip > 0)
            {
                try
                {
                    _learning.Append(new ReviewLearningEvent
                    {
                        ProjectId = projectId,
                        Type = "regen_after_review",
                        Scene = req.Scene,
                        Clip = regenClip,
                        Note = msg,
                        Outcome = status,
                        JobId = Snapshot.JobId,
                        ActionTaken = $"gen clip force only_missing={req.OnlyMissing}",
                    });
                }
                catch { /* non-fatal */ }
            }
        }
        catch (OperationCanceledException)
        {
            await FinishAsync("cancelled", "Cancelled by user");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scene gen failed");
            await FinishAsync("error", ex.Message, ex.Message);
        }
    }

    /// <summary>
    /// Before a regen overwrites a previously-rendered clip, copy it (plus its duration sidecar)
    /// into assets/video/_backup/ so a bad regen can be restored by hand. Keeps only the
    /// immediately-previous version — not unbounded history.
    /// </summary>
    private static void BackupExistingClipFile(string outPath, int scene, int clip)
    {
        if (!File.Exists(outPath)) return;
        try
        {
            var videoDir = Path.GetDirectoryName(outPath)!;
            var backupDir = Path.Combine(videoDir, "_backup");
            Directory.CreateDirectory(backupDir);
            var backupPath = Path.Combine(backupDir, $"scene_{scene:D2}_clip_{clip:D2}.mp4");
            File.Copy(outPath, backupPath, overwrite: true);

            var sidecar = outPath + ".duration.json";
            if (File.Exists(sidecar))
                File.Copy(sidecar, backupPath + ".duration.json", overwrite: true);
        }
        catch
        {
            // Best-effort safety net — never block a regen because the backup copy failed.
        }
    }

    private async Task GenerateOneClipAsync(
        string projectId,
        string projectDir,
        int scene,
        int clip,
        JsonElement clipEl,
        string resolution,
        CancellationToken ct,
        JsonElement? previousClipEl = null,
        JsonElement? blueprintRoot = null)
    {
        var profiles = _projects.LoadCharacterPromptProfiles(projectId);

        // Previous clip in this scene — Imagine /videos/extensions continues from that video.
        // Clip 2+ requires previous on disk (no gaps). Cast-set changes reseed fresh+refs (PR2).
        string? prevVisual = null;
        string? prevVideoPath = null;
        // Disposable working copy of prev for silence-trim / extend — never rewrite clip N-1 on disk.
        string? prevExtendWorkTemp = null;
        var cont = clipEl.TryGetProperty("veo_continuation_source", out var ce)
            ? (ce.GetString() ?? "none")
            : "none";
        var wantContinue =
            string.Equals(cont, "extend_previous", StringComparison.OrdinalIgnoreCase) ||
            clip > 1;

        string? prevOnDisk = null;
        if (clip > 1)
        {
            prevOnDisk = Path.Combine(
                projectDir, "assets", "video", $"scene_{scene:D2}_clip_{clip - 1:D2}.mp4");
            if (!File.Exists(prevOnDisk) || new FileInfo(prevOnDisk).Length < 1024)
            {
                throw new InvalidOperationException(
                    $"Generate S{scene:D2}C{clip - 1:D2} first — later clips continue from the previous video.");
            }

            // Breath-tail silence trim for extend input only. Mutating prevOnDisk in place used to
            // permanently shorten a finished clip when this job then failed/cancelled before C_N
            // was written (no backup of N-1). Work on a throwaway copy instead.
            prevExtendWorkTemp = Path.Combine(
                projectDir, "assets", "video", $"_prev_extend_s{scene:D2}c{clip:D2}.mp4");
            File.Copy(prevOnDisk, prevExtendWorkTemp, overwrite: true);
            prevVideoPath = prevExtendWorkTemp;
        }

        if (previousClipEl is { } prevEl &&
            prevEl.TryGetProperty("visual_prompt", out var pvp))
            prevVisual = pvp.GetString();

        if (prevVisual is null && wantContinue && blueprintRoot is { } root)
            prevVisual = FindClipVisualInBlueprint(root, scene, clip - 1);

        // PR2: reseed with locked refs when on-screen cast set changes (API drops refs on extend).
        var reseedFresh = false;
        // Imagine /videos/extensions rejects input video longer than 15s.
        // Bad extension-tail trims (or re-extend chains) can leave a prev clip over that cap —
        // clamp to the last ≤15s so continuity still uses the ending frames.
        string? extendInputTemp = null;
        try
        {
            // Breath-tail silence trim on the disposable copy only (never mutates clip N-1 on disk).
            if (prevVideoPath is not null && prevExtendWorkTemp is not null)
            {
                JsonElement? prevMetaForTail = previousClipEl;
                if (prevMetaForTail is null && blueprintRoot is { } brTail)
                    prevMetaForTail = FindClipElementInBlueprint(brTail, scene, clip - 1);
                var prevKeepTail = SpeechTailKeepSeconds(
                    prevMetaForTail, currentHasSpeech: ClipHasSpokenAudio(clipEl));
                await SilenceTrimClipAsync(
                        prevExtendWorkTemp, scene, clip - 1, ct, keepTailSeconds: prevKeepTail)
                    .ConfigureAwait(false);
            }

            if (prevVideoPath is not null && _opts.IdentityReseedOnCastChange)
            {
                var curKeys = ClipVideoPromptBuilder.ResolveOnScreenCharacterKeys(clipEl)
                    .Where(k => !(profiles.TryGetValue(k, out var cp) && cp.VoiceOnly))
                    .Select(k => k)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var prevKeys = previousClipEl is { } pe
                    ? ClipVideoPromptBuilder.ResolveOnScreenCharacterKeys(pe)
                        .Where(k => !(profiles.TryGetValue(k, out var pp) && pp.VoiceOnly))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : new List<string>();
                if (prevKeys.Count > 0 && !OnScreenSetsEqual(curKeys, prevKeys))
                {
                    reseedFresh = true;
                    await AppendLogAsync(
                        $"  [Identity] Cast set changed " +
                        $"[{string.Join(", ", prevKeys)}] → [{string.Join(", ", curKeys)}] — " +
                        "fresh gen with locked refs (not video-extend)");
                    prevVideoPath = null; // API: attach refs
                    // Keep prevVisual for continuity prose only
                }
            }

            // Silent → first spoken/VO: video-extend often clips the opening word (mouth stays closed
            // from the prior silent clip). Require prev on disk for order, but gen fresh + plates.
            if (prevVideoPath is not null)
            {
                JsonElement? prevMeta = previousClipEl;
                if (prevMeta is null && blueprintRoot is { } br)
                    prevMeta = FindClipElementInBlueprint(br, scene, clip - 1);
                if (prevMeta is { } pm && ClipHasSpokenAudio(clipEl) && !ClipHasSpokenAudio(pm))
                {
                    reseedFresh = true;
                    prevVideoPath = null;
                    await AppendLogAsync(
                        $"  [Speech] S{scene:D2}C{clip:D2} is first spoken after silence — " +
                        "fresh gen with locked refs (not video-extend) so the opening word is not clipped");
                }
            }

            if (prevVideoPath is not null)
            {
                var clamped = await ClampExtendInputIfNeededAsync(prevVideoPath, scene, clip, ct)
                    .ConfigureAwait(false);
                if (clamped is not null)
                {
                    extendInputTemp = clamped;
                    prevVideoPath = clamped;
                }
            }

            if (prevVideoPath is not null)
            {
                await AppendLogAsync(
                    $"  [Continuity] Imagine video-extend from S{scene:D2}C{clip - 1:D2} " +
                    $"({Path.GetFileName(prevVideoPath)})");
            }
            else if (reseedFresh && prevOnDisk is not null)
            {
                await AppendLogAsync(
                    $"  [Identity] Reseed S{scene:D2}C{clip:D2} after S{scene:D2}C{clip - 1:D2} " +
                    "(locked character refs attached)");
            }

            string? styleHead = null;
            try
            {
                var rules = _projectRules.GetActiveRulesBlock(projectId);
                if (!string.IsNullOrWhiteSpace(rules))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        rules, @"STYLE LOCK:\s*([^\n]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success)
                        styleHead = "STYLE LOCK: " + m.Groups[1].Value.Trim().TrimEnd('.', ' ');
                }
            }
            catch { /* non-fatal */ }

            var built = ClipVideoPromptBuilder.Build(
                clipEl,
                projectDir,
                characters: profiles,
                previousClipVisualPrompt: prevVisual,
                previousClipVideoPath: prevVideoPath,
                startFrameImagePath: null,
                maxRefs: 5,
                styleHead: styleHead,
                resolution: resolution);

            if (string.IsNullOrWhiteSpace(built.Prompt))
                throw new InvalidOperationException("clip missing visual_prompt");

            // Fresh / reseed: every on-screen cast key must have a locked ref attached
            if (prevVideoPath is null)
                EnsureFreshGenHasLockedRefs(projectId, projectDir, built, profiles);
            else
            {
                // Extend still requires locks on disk even when API cannot attach them
                EnsureOnScreenLocksExist(projectId, projectDir, built, profiles);
            }

            // P2/P4: active gen pack + approved project rules
            var addenda = new List<string>();
            try
            {
                var pack = _promptPacks.LoadActivePackText(PromptPackService.KindGen);
                if (!string.IsNullOrWhiteSpace(pack))
                    addenda.Add(pack.Trim());
                var rules = _projectRules.GetActiveRulesBlock(projectId);
                if (!string.IsNullOrWhiteSpace(rules))
                    addenda.Add(rules.Trim());
            }
            catch { /* non-fatal */ }

            if (addenda.Count > 0)
            {
                built = built.WithPrompt(built.Prompt.TrimEnd() + "\n\n" + string.Join("\n\n", addenda), " · learning-addenda");
            }

            // Pre-budget to xAI video ~4096 char hard cap (strip gen pack / house rules first).
            // Avoids a guaranteed first-attempt 400 on every clip.
            var preLen = built.Prompt.Length;
            var fitted = ClipVideoPromptBuilder.FitPromptToVideoBudget(built.Prompt);
            if (fitted.Length < preLen)
            {
                built = built.WithPrompt(fitted, $" · pre-budget {preLen}→{fitted.Length}");
                await AppendLogAsync(
                    $"  [Prompt] pre-budget {preLen}→{fitted.Length} chars (video hard cap {ClipVideoPromptBuilder.VideoPromptHardCapChars})");
            }

            // Persist + log full prompt for evaluation (admin logs surface this)
            await WriteAndLogPromptAsync(projectId, projectDir, scene, clip, built, ct).ConfigureAwait(false);

            if (built.Prompt.Contains("VOICE LOCK", StringComparison.OrdinalIgnoreCase))
                await AppendLogAsync("  [Voice] VOICE LOCK from character profile");
            if (built.ReferenceImagePaths.Count > 0)
                await AppendLogAsync(
                    $"  [Refs] attached={built.RefsAttachedToApi} count={built.ReferenceImagePaths.Count}: " +
                    string.Join(", ", built.ReferenceImagePaths.Select(Path.GetFileName)));
            else if (prevVideoPath is not null)
                await AppendLogAsync("  [Refs] video-extend — locked plates not attached to API (IDENTITY text only)");

            // Dialogue-aware duration (tight for short lines — billed per second)
            var duration = ClipDurationEstimator.EstimateForClip(clipEl);
            await AppendLogAsync($"  [Duration] estimated {duration}s (dialogue-aware, max {ClipDurationEstimator.MaxSeconds}s)");
            // Extension / ref: new portion typically max 10s
            if (prevVideoPath is not null || built.ReferenceImagePaths.Count > 0)
                duration = Math.Min(duration, 10);

            var model = await ResolveVideoModelAsync(projectId, ct);
            if (string.IsNullOrWhiteSpace(resolution))
                resolution = await ResolveVideoResolutionAsync(projectId, null, ct);

            var modeLabel = prevVideoPath is not null ? "video-extend" : built.Mode;
            await AppendLogAsync(
                $"  [Grok] Submit S{scene:D2}C{clip} duration={duration}s res={resolution} " +
                $"model={model} mode={modeLabel} {built.PromptLogSummary}");

            // Prefer official video continue; character refs only on fresh gens (API: no mix)
            var requestId = await _grok.SubmitGenerationAsync(
                built.Prompt,
                duration,
                resolution,
                model,
                ct,
                referenceImagePaths: prevVideoPath is null && built.ReferenceImagePaths.Count > 0
                    ? built.ReferenceImagePaths
                    : null,
                startFrameImagePath: null,
                continueFromVideoPath: prevVideoPath);
            await AppendLogAsync($"  [Grok] request_id={requestId}");

            var url = await _grok.PollForVideoUrlAsync(
                requestId,
                msg => { _ = AppendLogAsync($"  [Grok] {msg}"); },
                ct);

            var outPath = Path.Combine(
                projectDir, "assets", "video", $"scene_{scene:D2}_clip_{clip:D2}.mp4");

            BackupExistingClipFile(outPath, scene, clip);

            if (prevVideoPath is not null)
            {
                // Extension returns prev+new as one file — keep only the new portion as this clip
                var extendedTmp = Path.Combine(
                    projectDir, "assets", "video", $"_extend_s{scene:D2}c{clip:D2}.mp4");
                await _grok.DownloadToFileAsync(url, extendedTmp, ct);
                await AppendLogAsync(
                    $"  [Grok] extended video downloaded ({new FileInfo(extendedTmp).Length} bytes) — trimming new {duration}s");
                var trimmed = await TryTrimExtensionTailAsync(extendedTmp, outPath, duration, ct)
                    .ConfigureAwait(false);
                if (!trimmed)
                {
                    // Never accept prev+new as this clip — that poisons remux and the next extend.
                    try { if (File.Exists(outPath)) File.Delete(outPath); } catch { /* ignore */ }
                    try { File.Delete(extendedTmp); } catch { /* ignore */ }
                    throw new InvalidOperationException(
                        $"S{scene:D2}C{clip:D2}: extend-tail trim failed — not saving cumulative " +
                        "prev+new as this clip (retry; check ffmpeg).");
                }
                try { File.Delete(extendedTmp); } catch { /* ignore */ }
            }
            else
            {
                await _grok.DownloadToFileAsync(url, outPath, ct);
            }

            await AppendLogAsync($"  [Grok] saved {outPath}");

            // Trim trailing silence on THIS clip before any later clip extends from it.
            // Spoken lines keep a longer breath tail (~0.7s) so the next monologue clip does not butt-join.
            var keepTail = ClipHasSpokenAudio(clipEl)
                ? ClipSilenceTrimmer.SpeechBreathTailSeconds
                : ClipSilenceTrimmer.DefaultKeepTailSeconds;
            await SilenceTrimClipAsync(outPath, scene, clip, ct, keepTailSeconds: keepTail)
                .ConfigureAwait(false);

            // Always write duration sidecar (even when silence-trim is a no-op)
            await EnsureClipDurationSidecarAsync(outPath, scene, clip, ct).ConfigureAwait(false);

            try
            {
                var costProjectId = Snapshot.ProjectId ?? projectId ?? _projects.ActiveProjectId;
                await _costs.RecordVideoGenerationAsync(
                    costProjectId,
                    scene,
                    clip,
                    duration,
                    resolution,
                    model,
                    hasRefImage: built.ReferenceImagePaths.Count > 0 || prevVideoPath is not null,
                    isExtend: prevVideoPath is not null,
                    requestId: requestId,
                    ct: ct);
                await AppendLogAsync($"  [Cost] tracked list-rate for S{scene:D2}C{clip}");
            }
            catch (Exception ex)
            {
                await AppendLogAsync($"  [Cost] ledger write skipped: {ex.Message}");
            }
        }
        finally
        {
            if (extendInputTemp is not null)
            {
                try { File.Delete(extendInputTemp); } catch { /* ignore */ }
            }
            if (prevExtendWorkTemp is not null)
            {
                try { File.Delete(prevExtendWorkTemp); } catch { /* ignore */ }
            }
        }
    }

    private async Task WriteAndLogPromptAsync(
        string projectId,
        string projectDir,
        int scene,
        int clip,
        ClipVideoPromptBuilder.PromptBuildResult built,
        CancellationToken ct)
    {
        try
        {
            var dir = Path.Combine(projectDir, "assets", "video", "prompts");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"S{scene:D2}C{clip:D2}.txt");
            var header =
                $"# S{scene:D2}C{clip:D2}  {built.PromptLogSummary}\n" +
                $"# projectId: {projectId}\n" +
                $"# mode: {built.Mode}\n" +
                $"# castCount: {built.CastCount}\n" +
                $"# onScreen: {string.Join(", ", built.OnScreenKeys)}\n" +
                $"# refs: {string.Join(", ", built.ReferenceImagePaths.Select(Path.GetFileName))}\n" +
                $"# refsAttachedToApi: {built.RefsAttachedToApi}\n" +
                $"# startFrame: {built.StartFrameImagePath ?? "(none)"}\n" +
                $"# characters: {string.Join(", ", built.CharacterKeys)}\n\n";
            await File.WriteAllTextAsync(path, header + built.Prompt, ct).ConfigureAwait(false);

            var metaPath = Path.Combine(dir, $"S{scene:D2}C{clip:D2}.meta.json");
            var meta = new Dictionary<string, object?>
            {
                ["projectId"] = projectId,
                ["scene"] = scene,
                ["clip"] = clip,
                ["mode"] = built.Mode,
                ["castCount"] = built.CastCount,
                ["onScreenKeys"] = built.OnScreenKeys.ToList(),
                ["characterKeys"] = built.CharacterKeys.ToList(),
                ["refs"] = built.ReferenceImagePaths.Select(Path.GetFileName).ToList(),
                ["refsAttachedToApi"] = built.RefsAttachedToApi,
                ["styleHead"] = built.StyleHead,
                ["castCountLine"] = built.CastCountLine,
                ["actionText"] = built.ActionText,
                // Full prompt body on disk for manual / external AI review (PR5 project-local data)
                ["prompt"] = built.Prompt,
                ["promptLen"] = built.Prompt.Length,
                ["promptLogSummary"] = built.PromptLogSummary,
                ["startFrame"] = built.StartFrameImagePath,
                ["builtAtUtc"] = DateTimeOffset.UtcNow.ToString("o"),
            };
            var metaJson = System.Text.Json.JsonSerializer.Serialize(meta, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            }) + "\n";
            await File.WriteAllTextAsync(metaPath, metaJson, ct).ConfigureAwait(false);

            await AppendLogAsync(
                $"  [Prompt] saved {Path.GetRelativePath(projectDir, path)} + meta " +
                $"({built.Prompt.Length} chars, castCount={built.CastCount})");
            await AppendLogAsync("--- PROMPT BEGIN ---");
            const int chunk = 3500;
            for (var i = 0; i < built.Prompt.Length; i += chunk)
            {
                var len = Math.Min(chunk, built.Prompt.Length - i);
                await AppendLogAsync(built.Prompt.Substring(i, len));
            }
            await AppendLogAsync("--- PROMPT END ---");
        }
        catch (Exception ex)
        {
            await AppendLogAsync($"  [Prompt] log failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Imagine <c>/videos/extensions</c> rejects input longer than 15s
    /// (<c>Input video must not exceed 15 seconds</c>).
    /// </summary>
    private const double MaxVideoExtendInputSeconds = 15.0;

    /// <summary>
    /// If <paramref name="prevVideoPath"/> is longer than the API max, write a temp file
    /// with only the last ≤15s (ending frames for continuity) and return that path.
    /// Returns null when no clamp is needed (caller keeps the original path).
    /// </summary>
    private async Task<string?> ClampExtendInputIfNeededAsync(
        string prevVideoPath,
        int scene,
        int clip,
        CancellationToken ct)
    {
        if (!_remux.IsAvailable() || !File.Exists(prevVideoPath))
            return null;

        var total = await ClipSilenceTrimmer.ProbeDurationSecondsAsync(
            _remux.FfmpegPath, prevVideoPath, ct).ConfigureAwait(false);
        if (total is not > MaxVideoExtendInputSeconds)
            return null;

        // Slightly under 15s so float rounding / container padding never trips the API.
        var keepSec = Math.Min(total.Value, MaxVideoExtendInputSeconds - 0.05);
        var dir = Path.GetDirectoryName(prevVideoPath)!;
        var tmp = Path.Combine(dir, $"_extend_in_s{scene:D2}c{clip:D2}.mp4");
        var ok = await TryExtractVideoTailAsync(prevVideoPath, tmp, keepSec, ct)
            .ConfigureAwait(false);
        if (!ok)
        {
            await AppendLogAsync(
                $"  [Continuity] prev clip is {total.Value:F1}s (> {MaxVideoExtendInputSeconds:0}s API max) " +
                "but tail clamp failed — extend will likely be rejected");
            return null;
        }

        await AppendLogAsync(
            $"  [Continuity] prev clip {total.Value:F1}s exceeds {MaxVideoExtendInputSeconds:0}s extend limit — " +
            $"using last {keepSec:F1}s for API input");
        return tmp;
    }

    /// <summary>
    /// Extension API returns prev+new. Keep only the last <paramref name="extensionSeconds"/>
    /// as this clip file so remux still stitches independent clips.
    /// </summary>
    private async Task<bool> TryTrimExtensionTailAsync(
        string extendedVideoPath,
        string outClipPath,
        int extensionSeconds,
        CancellationToken ct)
    {
        var sec = Math.Max(1, extensionSeconds);
        return await TryExtractVideoTailAsync(extendedVideoPath, outClipPath, sec, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Extract the last <paramref name="tailSeconds"/> of a video into <paramref name="outPath"/>.
    /// <c>-sseof</c> is an input option and must precede <c>-i</c> (same pattern as frame review).
    /// </summary>
    private async Task<bool> TryExtractVideoTailAsync(
        string videoPath,
        string outPath,
        double tailSeconds,
        CancellationToken ct)
    {
        try
        {
            if (!_remux.IsAvailable()) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            var sec = Math.Max(0.5, tailSeconds);
            // Format with invariant culture so "14.95" never becomes "14,95".
            var secArg = sec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            // -sseof -N MUST come before -i (input option). After -i it is ignored/fails,
            // which previously left full prev+new files on disk and broke the next extend at 15s.
            var args =
                $"-hide_banner -nostats -loglevel error -y -sseof -{secArg} -i \"{videoPath}\" " +
                $"-t {secArg} -c:v libx264 -preset veryfast -crf 18 -c:a aac -b:a 128k \"{outPath}\"";
            var r = await FfmpegProcess.RunAsync(
                    _remux.FfmpegPath, args, ct, timeoutMs: 180_000)
                .ConfigureAwait(false);
            if (r.Success &&
                File.Exists(outPath) &&
                new FileInfo(outPath).Length >= 1024)
                return true;

            if (!string.IsNullOrWhiteSpace(r.StdErr))
                _log.LogWarning("ffmpeg tail extract failed: {Err}", r.StdErr.Trim());
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ffmpeg tail extract exception for {Path}", videoPath);
            return false;
        }
    }

    /// <summary>
    /// When the previous clip was spoken and this one is too, keep a longer end-breath on prev.
    /// </summary>
    private static double SpeechTailKeepSeconds(JsonElement? previousClipEl, bool currentHasSpeech)
    {
        if (!currentHasSpeech)
            return ClipSilenceTrimmer.DefaultKeepTailSeconds;
        if (previousClipEl is { } pe && ClipHasSpokenAudio(pe))
            return ClipSilenceTrimmer.SpeechBreathTailSeconds;
        // Prev silent / unknown — short tail is fine (fresh speech start does not need prev pause)
        return ClipSilenceTrimmer.DefaultKeepTailSeconds;
    }

    /// <summary>
    /// Cut trailing silence so the next video-extend starts on real content.
    /// Spoken clips keep a short breath so monologue joins (C2→C3) are not butt-joined.
    /// </summary>
    private async Task SilenceTrimClipAsync(
        string videoPath,
        int scene,
        int clip,
        CancellationToken ct,
        double? keepTailSeconds = null)
    {
        if (!_remux.IsAvailable() || !File.Exists(videoPath))
            return;
        try
        {
            var keep = keepTailSeconds ?? ClipSilenceTrimmer.DefaultKeepTailSeconds;
            var result = await ClipSilenceTrimmer.TrimTrailingSilenceAsync(
                _remux.FfmpegPath,
                videoPath,
                keepTailSeconds: keep,
                minTrimSavings: 0.4,
                log: _log,
                ct: ct).ConfigureAwait(false);
            if (result.Trimmed)
                await AppendLogAsync(
                    $"  [Audio] S{scene:D2}C{clip:D2} silence-trim {result.BeforeSec:F1}s → {result.AfterSec:F1}s ({result.Message})");
            else
                await AppendLogAsync(
                    $"  [Audio] S{scene:D2}C{clip:D2} silence-trim skip: {result.Message}");

            // Clip 2+ often starts with dead air at the join — trim leading silence too
            if (clip > 1)
            {
                var lead = await ClipSilenceTrimmer.TrimLeadingSilenceAsync(
                    _remux.FfmpegPath,
                    videoPath,
                    keepHeadSeconds: 0.08,
                    minTrimSavings: 0.25,
                    log: _log,
                    ct: ct).ConfigureAwait(false);
                if (lead.Trimmed)
                    await AppendLogAsync(
                        $"  [Audio] S{scene:D2}C{clip:D2} lead-silence-trim ({lead.Message})");
            }
        }
        catch (Exception ex)
        {
            await AppendLogAsync($"  [Audio] silence-trim error: {ex.Message}");
        }
    }

    /// <summary>
    /// Probe final clip length and write <c>*.mp4.duration.json</c> (needed even when trim skips).
    /// </summary>
    private async Task EnsureClipDurationSidecarAsync(
        string videoPath,
        int scene,
        int clip,
        CancellationToken ct)
    {
        if (!File.Exists(videoPath))
            return;
        try
        {
            if (_remux.IsAvailable())
            {
                var sec = await ClipSilenceTrimmer.ProbeDurationSecondsAsync(
                    _remux.FfmpegPath, videoPath, ct).ConfigureAwait(false);
                if (sec is > 0)
                {
                    await MediaDurationProbe.WriteDurationSidecarAsync(videoPath, sec.Value, ct)
                        .ConfigureAwait(false);
                    await AppendLogAsync(
                        $"  [Duration] S{scene:D2}C{clip:D2} sidecar {sec.Value:F2}s");
                    return;
                }
            }

        }
        catch (Exception ex)
        {
            await AppendLogAsync($"  [Duration] sidecar skip: {ex.Message}");
        }
    }

    /// <summary>
    /// Ordered ignore-case set equality for on-screen cast keys (PR2 reseed decision).
    /// </summary>
    private static bool OnScreenSetsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        // Inputs are expected already sorted ignore-case; still compare as sets.
        var setA = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        return setA.SetEquals(b);
    }

    /// <summary>
    /// Video-extend cannot attach plates to the API, but locked refs must still exist on disk
    /// so CHARACTER VARIABLES / future reseeds stay authoritative.
    /// </summary>
    private void EnsureOnScreenLocksExist(
        string projectId,
        string projectDir,
        ClipVideoPromptBuilder.PromptBuildResult built,
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles)
    {
        var missing = MissingOnScreenLockKeys(projectId, projectDir, built, profiles);
        if (missing.Count == 0) return;

        throw new InvalidOperationException(
            "Locked character reference images required on disk before video-extend " +
            "(identity continuity even though the API cannot attach plates). " +
            $"Missing ref for: {string.Join(", ", missing)}. " +
            "Open Characters → generate + lock a portrait for each on-screen role.");
    }

    /// <summary>
    /// On fresh (non-extend) gens, every non-voice-only character in the clip prompt must have
    /// a locked ref image actually attached — prevents identity drift across clips.
    /// </summary>
    private void EnsureFreshGenHasLockedRefs(
        string projectId,
        string projectDir,
        ClipVideoPromptBuilder.PromptBuildResult built,
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles)
    {
        var missing = MissingOnScreenLockKeys(projectId, projectDir, built, profiles);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Locked character reference images required for fresh video gen (avoids face drift). " +
                $"Missing ref for: {string.Join(", ", missing)}. " +
                "Open Characters → generate + lock a portrait for each on-screen role.");
        }

        var onScreen = OnScreenVisualKeys(built, profiles);
        if (onScreen.Count > 0 && built.ReferenceImagePaths.Count == 0)
        {
            throw new InvalidOperationException(
                "Fresh video gen built a prompt with on-screen cast but attached 0 reference images. " +
                "Lock portraits under Characters and retry.");
        }
    }

    private static List<string> OnScreenVisualKeys(
        ClipVideoPromptBuilder.PromptBuildResult built,
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles)
    {
        return (built.OnScreenKeys.Count > 0 ? built.OnScreenKeys : built.CharacterKeys)
            .Where(k => !(profiles.TryGetValue(k, out var p) && p.VoiceOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> MissingOnScreenLockKeys(
        string projectId,
        string projectDir,
        ClipVideoPromptBuilder.PromptBuildResult built,
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles)
    {
        var onScreen = OnScreenVisualKeys(built, profiles);
        var missing = new List<string>();
        foreach (var key in onScreen)
        {
            var path = ClipVideoPromptBuilder.ResolveCharacterRefPathPublic(projectDir, key)
                       ?? _projects.ResolveCharacterRefPath(projectId, key);
            if (path is null || !File.Exists(path))
                missing.Add(key);
        }
        return missing;
    }

    private static string? FindClipVisualInBlueprint(JsonElement root, int scene, int clipNum)
    {
        try
        {
            var c = FindClipElementInBlueprint(root, scene, clipNum);
            if (c is { } clip && clip.TryGetProperty("visual_prompt", out var vp))
                return vp.GetString();
        }
        catch { /* ignore */ }
        return null;
    }

    private static JsonElement? FindClipElementInBlueprint(JsonElement root, int scene, int clipNum)
    {
        try
        {
            if (!root.TryGetProperty("scenes", out var scenes) ||
                scenes.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var s in scenes.EnumerateArray())
            {
                if (!s.TryGetProperty("scene_number", out var sn) || !sn.TryGetInt32(out var n) || n != scene)
                    continue;
                return FindClipInScene(s, clipNum);
            }
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>
    /// True when the clip has spoken dialogue or VO text (not silent establish).
    /// </summary>
    internal static bool ClipHasSpokenAudio(JsonElement clipEl)
    {
        if (!clipEl.TryGetProperty("audio_payload", out var ap) ||
            ap.ValueKind != JsonValueKind.Object)
            return false;
        var dialogue = ap.TryGetProperty("dialogue", out var d) ? d.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(dialogue))
            return false;
        var delivery = (ap.TryGetProperty("delivery", out var del) ? del.GetString() ?? "none" : "none")
            .Trim().ToLowerInvariant();
        if (delivery is "none" or "")
            return false;
        return Stage2PlannerService.IsOnCameraDelivery(delivery) ||
               delivery is "voiceover_internal" or "internal" or "narration" or "vo" or "thought" or
                   "voiceover" or "voice_over" or "off_camera" or "offcamera";
    }

    private static JsonElement? FindClipInScene(JsonElement sceneEl, int clipNum)
    {
        if (!sceneEl.TryGetProperty("veo_clips", out var clips) ||
            clips.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var c in clips.EnumerateArray())
        {
            if (c.TryGetProperty("clip_number", out var cn) && cn.TryGetInt32(out var n) && n == clipNum)
                return c;
        }
        return null;
    }

    private static JsonElement? FindScene(JsonElement root, int sceneNum)
    {
        if (!root.TryGetProperty("scenes", out var scenes) ||
            scenes.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var s in scenes.EnumerateArray())
        {
            if (s.TryGetProperty("scene_number", out var n) && n.TryGetInt32(out var sn) && sn == sceneNum)
                return s;
        }
        return null;
    }

    /// <summary>
    /// Prefer explicit request resolution, else project Configuration, else app default.
    /// </summary>
    private async Task<string> ResolveVideoResolutionAsync(
        string projectId,
        string? requested,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return NormalizeResolution(requested);

        try
        {
            var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
            if (cfg.TryGetValue("resolution", out var el))
            {
                var fromCfg = el.ValueKind switch
                {
                    JsonValueKind.String => el.GetString(),
                    JsonValueKind.Number => el.ToString(),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(fromCfg))
                    return NormalizeResolution(fromCfg);
            }
        }
        catch
        {
            // fall through to app default
        }

        return NormalizeResolution(
            string.IsNullOrWhiteSpace(_opts.DefaultResolution) ? "720p" : _opts.DefaultResolution);
    }

    /// <summary>
    /// Fail closed before video spend when the selected model lacks continue/refs required for this job.
    /// </summary>
    private async Task EnsureVideoModelCapabilitiesAsync(
        string projectId,
        bool needContinue,
        bool needReferenceImages,
        CancellationToken ct)
    {
        if (!needContinue && !needReferenceImages)
            return;

        var modelId = await ResolveVideoModelAsync(projectId, ct).ConfigureAwait(false);
        var entry = SupportedModelCatalog.ResolveOrDefault(modelId, ModelCapability.Video);
        if (needContinue && !entry.SupportsVideoContinue)
        {
            throw new InvalidOperationException(
                $"Video model '{entry.Id}' does not support clip-to-clip continue " +
                "(required for clip 2+). Switch project video model to grok-imagine-video " +
                "(or another model with video-extend). " +
                (string.IsNullOrWhiteSpace(entry.Notes) ? "" : entry.Notes));
        }

        if (needReferenceImages && !entry.SupportsReferenceImages)
        {
            throw new InvalidOperationException(
                $"Video model '{entry.Id}' cannot attach locked character reference plates. " +
                "Switch project video model to grok-imagine-video, or disable the cast lock gate " +
                "only if you accept identity drift. " +
                (string.IsNullOrWhiteSpace(entry.Notes) ? "" : entry.Notes));
        }
    }

    /// <summary>
    /// Project <c>model_name</c> → catalog (endpoint/keys), else host <see cref="FilmStudioOptions.DefaultModel"/>.
    /// </summary>
    private async Task<string> ResolveVideoModelAsync(string projectId, CancellationToken ct)
    {
        string? fromCfg = null;
        try
        {
            var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
            if (cfg.TryGetValue("model_name", out var el) && el.ValueKind == JsonValueKind.String)
                fromCfg = el.GetString();
        }
        catch
        {
            /* use default */
        }

        var resolved = SupportedModelCatalog.ResolveOrDefault(
            fromCfg,
            ModelCapability.Video,
            fallbackId: _opts.DefaultModel);
        return resolved.Id;
    }

    private static string NormalizeResolution(string? value)
    {
        var v = (value ?? "720p").Trim().ToLowerInvariant();
        return v switch
        {
            "480" or "480p" => "480p",
            "720" or "720p" => "720p",
            "1080" or "1080p" => "1080p",
            _ => v.EndsWith('p') ? v : $"{v}p",
        };
    }

    /// <summary>
    /// Require env keys for the project's selected video model (not a hardcoded XAI_API_KEY message).
    /// MultiProvider IsConfigured is true if either provider has a key — that misdirects Gemini-only setups.
    /// </summary>
    private async Task EnsureVideoProviderConfiguredAsync(string projectId, CancellationToken ct)
    {
        var modelId = await ResolveVideoModelAsync(projectId, ct).ConfigureAwait(false);
        var entry = SupportedModelCatalog.ResolveOrDefault(modelId, ModelCapability.Video);

        // Ambient multi-user key (Grok) counts as configured for Xai models.
        if (entry.Provider == ModelProviderFamily.Xai &&
            !string.IsNullOrWhiteSpace(ApiKeyScope.Current))
            return;

        var missing = SupportedModelCatalog.MissingEnvKeys(entry);
        if (missing.Count == 0)
            return;

        var keys = string.Join(" / ", missing);
        throw new InvalidOperationException(
            $"{keys} is not set (required for video model '{entry.Id}' / {entry.ProviderId}).");
    }

    /// <summary>
    /// Project-wide spend gate: every cast seed must have an approved voice profile and
    /// (for on-screen roles) a locked ref image before any video generation.
    /// </summary>
    private void EnsureCastReadyForVideo(string projectId)
    {
        var missing = _projects.GetCastNotReadyForVideo(projectId);
        if (missing.Count == 0)
            return;

        var detail = string.Join("; ", missing);
        throw new InvalidOperationException(
            "Cast not ready for video gen — approve voice and locked image for every character first " +
            $"(avoids wasting API spend). Missing: {detail}. " +
            "Open Characters → set voice, then generate + lock a portrait. " +
            "Voice-only roles (e.g. Narrator) need a voice profile only.");
    }

    /// <summary>
    /// Scene-level safety net for on-screen keys mentioned in the blueprint that may not
    /// appear in cast seeds (still require a locked ref if they are not voice-only).
    /// </summary>
    private void EnsureSceneCharactersLocked(string projectId, int sceneNumber)
    {
        var unlocked = _projects.GetUnlockedOnScreenCharacters(projectId, sceneNumber);
        if (unlocked.Count == 0)
            return;

        var names = string.Join(", ", unlocked);
        throw new InvalidOperationException(
            $"Scene {sceneNumber}: locked character refs required before video gen. " +
            $"Missing lock(s): {names}. " +
            "Open Characters → lock a book plate or generate + lock a portrait. " +
            "(Only true voice-only roles skip images.)");
    }

    /// <summary>
    /// Remux progress → job Message + log + SignalR JobLog.
    /// Parses <c>(12.3%)</c> from ffmpeg progress lines into Index/Total (0–1000).
    /// </summary>
    private async Task OnRemuxProgressAsync(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        // Prefer compact progress lines for the badge; still log everything.
        var isProgress = line.Contains('[') && (
            line.Contains("time ", StringComparison.OrdinalIgnoreCase) ||
            line.Contains('%') ||
            line.Contains("frame ", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("speed ", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("complete", StringComparison.OrdinalIgnoreCase));

        int? pctTenths = null;
        var m = System.Text.RegularExpressions.Regex.Match(line, @"\((\d+(?:\.\d+)?)%\)");
        if (m.Success &&
            double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pct))
        {
            pctTenths = (int)Math.Clamp(Math.Round(pct * 10), 0, 1000);
        }

        await UpdateAsync(s =>
        {
            s.Log.Add(line);
            if (s.Log.Count > 120)
                s.Log = s.Log.TakeLast(120).ToList();
            // Keep live badge on progress; still update message for probe/errors
            if (isProgress || s.Message is null || s.Message.Length == 0 ||
                line.StartsWith("ffmpeg:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Remux", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("WIP", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Probing", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("complete", StringComparison.OrdinalIgnoreCase))
            {
                s.Message = line;
            }
            if (pctTenths is int p)
            {
                s.Total = 1000;
                s.Index = p;
            }
        });

        if (_sink is not null)
            await _sink.OnJobLogAsync(line);
    }

    private async Task ReportStage1ProgressAsync(string line)
    {
        // Single UpdateAsync so Index/Total + log stay atomic (no race losing counters)
        await UpdateAsync(s =>
        {
            if (s.Log.Count == 0 || s.Log[^1] != line)
            {
                s.Log.Add(line);
                if (s.Log.Count > 120)
                    s.Log = s.Log.TakeLast(120).ToList();
            }
            s.Message = line;

            var mChunks = System.Text.RegularExpressions.Regex.Match(
                line, @"Stage 1:\s+(\d+)\s+book chunk", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mChunks.Success && int.TryParse(mChunks.Groups[1].Value, out var totalChunks) && totalChunks > 0)
            {
                s.Total = Math.Max(s.Total, totalChunks);
                if (s.Index < 0) s.Index = 0;
                return;
            }

            // Chunk progress: Index = completed chunks (not current number while waiting).
            // "chunk 1/1 — waiting" → 0/1; "chunk 1/1 done" → 1/1.
            var m = System.Text.RegularExpressions.Regex.Match(
                line, @"chunk\s+(\d+)\s*/\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success &&
                int.TryParse(m.Groups[1].Value, out var idx) &&
                int.TryParse(m.Groups[2].Value, out var tot) &&
                tot > 0)
            {
                s.Total = Math.Max(s.Total, tot);
                var chunkDone = line.Contains("done", StringComparison.OrdinalIgnoreCase);
                s.Index = chunkDone
                    ? Math.Max(s.Index, idx)
                    : Math.Max(s.Index, Math.Max(0, idx - 1));
                return;
            }

            // Vision: page i/N while processing → completed = i-1 until last page finishes
            var mVis = System.Text.RegularExpressions.Regex.Match(
                line, @"Grok vision\s+(\d+)/(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mVis.Success &&
                int.TryParse(mVis.Groups[1].Value, out var vi) &&
                int.TryParse(mVis.Groups[2].Value, out var vt) &&
                vt > 0)
            {
                s.Total = Math.Max(s.Total, vt);
                s.Index = Math.Max(s.Index, Math.Max(0, vi - 1));
            }
        });
        if (_sink is not null)
            await _sink.OnJobLogAsync(line);
    }

    private async Task AppendLogAsync(string message)
    {
        _logLines.Enqueue(message);
        await UpdateAsync(s =>
        {
            // Avoid duplicate consecutive lines (AppendLog after Update that already set Message)
            if (s.Log.Count == 0 || s.Log[^1] != message)
            {
                s.Log.Add(message);
                if (s.Log.Count > 120)
                    s.Log = s.Log.TakeLast(120).ToList();
            }
            s.Message = message;
        });
        if (_sink is not null)
            await _sink.OnJobLogAsync(message);
    }

    private async Task UpdateAsync(Action<JobSnapshot> mutate)
    {
        var run = CurrentRun.Value;
        if (run is null) return;
        await run.SnapLock.WaitAsync();
        try
        {
            mutate(run.Snapshot);
            if (!string.IsNullOrEmpty(run.ActiveJobId))
            {
                _jobs.Update(run.ActiveJobId, rec =>
                {
                    rec.Status = run.Snapshot.Status;
                    rec.Kind = run.Snapshot.Kind;
                    rec.Message = run.Snapshot.Message;
                    rec.ProjectId = run.Snapshot.ProjectId;
                    rec.UserId = run.Snapshot.UserId;
                    rec.CharKey = run.Snapshot.CharKey;
                    rec.Scene = run.Snapshot.Scene;
                    rec.Clip = run.Snapshot.Clip;
                    rec.Index = run.Snapshot.Index;
                    rec.Total = run.Snapshot.Total;
                    rec.Log = run.Snapshot.Log.ToList();
                    rec.Error = run.Snapshot.Error;
                    rec.StartedAt = run.Snapshot.StartedAt;
                    rec.FinishedAt = run.Snapshot.FinishedAt;
                    if (run.Snapshot.JobId is null)
                        run.Snapshot.JobId = rec.JobId;
                });
            }
            await PublishAsync();
        }
        finally
        {
            run.SnapLock.Release();
        }
    }

    private async Task FinishAsync(string status, string message, string? error = null)
    {
        string? projectId = null;
        string? kind = null;
        await UpdateAsync(s =>
        {
            s.Status = status;
            s.Message = message;
            s.Error = error;
            s.FinishedAt = DateTimeOffset.UtcNow;
            if (s.Total > 0 && status == "done")
                s.Index = s.Total;
            projectId = s.ProjectId;
            kind = s.Kind;
        });
        await AppendLogAsync(message);

        // Scene list cache: clip/composite counts change on gen/remux/stage done
        if (status is "done" or "error" or "cancelled")
        {
            if (string.IsNullOrWhiteSpace(projectId))
                projectId = CurrentRun.Value?.Snapshot.ProjectId;
            _projects.InvalidateSceneListCache(projectId);
        }

        // PR4.5b: keep ARTIFACTS.md / artifact_index.json current after pipeline work
        if (status == "done" &&
            !string.IsNullOrWhiteSpace(projectId) &&
            ShouldRefreshArtifactIndex(kind))
        {
            await TryRefreshArtifactIndexAsync(projectId!).ConfigureAwait(false);
        }
    }

    private static bool ShouldRefreshArtifactIndex(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return false;
        return kind is
            "remux" or
            "gen-scene" or
            "gen-batch" or
            "clip-auto-review" or
            "clip-auto-review-batch" or
            "stage2" or
            "character-variants";
    }

    private async Task TryRefreshArtifactIndexAsync(string projectId)
    {
        try
        {
            var doc = await _artifactIndex.RebuildAsync(projectId).ConfigureAwait(false);
            await AppendLogAsync(
                $"  [Artifacts] map updated — readyForManualFinalReview={doc.ReadyForManualFinalReview} " +
                $"(ARTIFACTS.md, artifact_index.json, telemetry snapshots)");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Artifact index rebuild skipped for {ProjectId}", projectId);
            await AppendLogAsync($"  [Artifacts] map refresh skipped: {ex.Message}");
        }
    }

    private async Task PublishAsync()
    {
        if (_sink is null) return;
        var run = CurrentRun.Value;
        if (run is null) return;
        await _sink.OnJobUpdatedAsync(Clone(run.Snapshot));
    }

    private static JobSnapshot Clone(JobSnapshot s) => new()
    {
        JobId = s.JobId,
        Status = s.Status,
        Kind = s.Kind,
        Message = s.Message,
        ProjectId = s.ProjectId,
        UserId = s.UserId,
        CharKey = s.CharKey,
        Scene = s.Scene,
        Clip = s.Clip,
        Index = s.Index,
        Total = s.Total,
        Log = s.Log.ToList(),
        Error = s.Error,
        QueuedAt = s.QueuedAt,
        StartedAt = s.StartedAt,
        FinishedAt = s.FinishedAt,
    };
}
