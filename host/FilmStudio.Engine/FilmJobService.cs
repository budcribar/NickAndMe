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
    private readonly IGrokVideoClient _grok;
    private readonly CharacterDesignService _characters;
    private readonly CharacterBookPlateService _plates;
    private readonly BookPrepareService _books;
    private readonly Stage1Service _stage1;
    private readonly Stage2PlannerService _stage2;
    private readonly IFfmpegRemux _remux;
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
        IGrokVideoClient grok,
        CharacterDesignService characters,
        CharacterBookPlateService plates,
        BookPrepareService books,
        Stage1Service stage1,
        Stage2PlannerService stage2,
        IFfmpegRemux remux,
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
        _stage1 = stage1;
        _stage2 = stage2;
        _remux = remux;
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

    public Task CancelAsync(string? jobId = null)
    {
        if (!string.IsNullOrWhiteSpace(jobId))
        {
            _jobs.TryCancel(jobId);
            if (_jobCts.TryGetValue(jobId, out var cts))
            {
                try { cts.Cancel(); } catch { /* ignore */ }
            }
            return Task.CompletedTask;
        }

        // Cancel all active job CTSes
        foreach (var kv in _jobCts.ToArray())
        {
            _jobs.TryCancel(kv.Key);
            try { kv.Value.Cancel(); } catch { /* ignore */ }
        }
        return Task.CompletedTask;
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

        // Fallback: create running job (legacy path)
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

                    if (useLocalPool)
                    {
                        await _localPool.RunAsync(
                            async ct =>
                            {
                                using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Cts.Token, ct);
                                await work(linked.Token);
                            },
                            run.Cts.Token);
                    }
                    else
                    {
                        await _apiPool.RunAsync(
                            userId,
                            async ct =>
                            {
                                using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Cts.Token, ct);
                                await work(linked.Token);
                            },
                            run.Cts.Token);
                    }

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
        if (req.Scenes is null || req.Scenes.Count == 0)
            throw new InvalidOperationException("At least one scene is required.");
        var projectId = string.IsNullOrWhiteSpace(req.ProjectId)
            ? _projects.ActiveProjectId
            : req.ProjectId;
        var locks = req.Scenes
            .Where(s => s > 0)
            .Select(s => LockKeys.Scene(projectId, s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return StartBackgroundJobAsync(
            ct => RunBatchGenAsync(req, ct),
            new JobEnqueueMeta
            {
                Kind = "batch",
                ProjectId = projectId,
                Message = $"Queued batch gen ({req.Scenes.Count} scenes)…",
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

    /// <summary>C# PDF extract + optional Grok vision OCR → book_full.txt.</summary>
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

    /// <summary>Lock/unlock character reference images.</summary>
    public Task<string> RunCharacterDesignActionAsync(
        string projectId,
        string action,
        string charKey,
        int variantIndex = 1,
        string? imagePath = null,
        CancellationToken ct = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("A generation job is already running.");

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return action switch
            {
                "lock-variant" =>
                    _characters.LockVariant(projectId, charKey, Math.Clamp(variantIndex, 1, 3)),
                "lock-image" when !string.IsNullOrWhiteSpace(imagePath) =>
                    _characters.LockFromPath(
                        projectId,
                        charKey,
                        ResolveLockImagePath(projectId, imagePath!)),
                "lock-bookref" =>
                    _characters.LockBookRef(projectId, charKey, Math.Max(0, variantIndex)),
                "unlock" =>
                    _characters.Unlock(projectId, charKey)
                        ? $"Unlocked {charKey} — previous lock kept as variant 1 (best so far)"
                        : $"No locked ref for {charKey}",
                _ => throw new InvalidOperationException($"Unknown character action: {action}"),
            };
        }, ct);
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
                $"{result.LocationCount} locations";
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
            await AppendLogAsync("Stage 2: C# Stage2PlannerService (deterministic, no API)");
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

            var refreshed = 0;
            if (req.RefreshStaleScenes)
            {
                // Play WIP: remux only stale scenes (clips newer / missing composite), then stitch WIP.
                var fresh = _projects.AssessWipFreshness(projectId);
                var toRemux = fresh.StaleScenes;
                await AppendLogAsync(
                    toRemux.Count > 0
                        ? $"Remuxing {toRemux.Count} stale scene composite(s) before WIP…"
                        : "No stale scenes — stitching WIP from current composites");
                var i = 0;
                foreach (var sn in toRemux)
                {
                    ct.ThrowIfCancellationRequested();
                    i++;
                    await UpdateAsync(s =>
                    {
                        s.Scene = sn;
                        s.Index = i;
                        s.Total = toRemux.Count;
                        s.Message = $"Remux stale S{sn:D2} ({i}/{toRemux.Count})…";
                    });
                    try
                    {
                        await _remux.RemuxSceneAsync(projectId, sn,
                            line => { _ = OnRemuxProgressAsync(line); }, ct);
                        refreshed++;
                        await AppendLogAsync($"Remuxed S{sn:D2}");
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Remux S{Scene} failed — continuing", sn);
                        await AppendLogAsync($"S{sn:D2} remux skipped: {ex.Message}");
                    }
                }
            }
            else if (req.Scene is int sn && sn > 0)
            {
                await _remux.RemuxSceneAsync(projectId, sn,
                    line => { _ = OnRemuxProgressAsync(line); }, ct);
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

        var scenes = req.Scenes.Distinct().OrderBy(s => s).ToList();
        Snapshot = new JobSnapshot
        {
            Status = "running",
            Kind = "batch",
            ProjectId = projectId,
            Message = $"Batch: {scenes.Count} scene(s)…",
            StartedAt = DateTimeOffset.UtcNow,
            Log = new List<string>(),
        };
                RegisterActiveJob();
        await PublishAsync();

        try
        {
            if (!_grok.IsConfigured)
                throw new InvalidOperationException("XAI_API_KEY is not set.");

            using var bp = await _projects.LoadBlueprintAsync(projectId, ct)
                ?? throw new InvalidOperationException(
                    $"No Stage 2 blueprint for project {projectId}. Run Stage 2 first.");

            if (req.RequireLockedCharacters)
            {
                foreach (var sn in scenes)
                    EnsureCharactersLocked(projectId, sn);
            }

            var projectDir = _projects.GetProjectDir(projectId);
            Directory.CreateDirectory(Path.Combine(projectDir, "assets", "video"));

            // Pre-count work units
            var work = new List<(int Scene, int Clip, JsonElement ClipEl)>();
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
                        work.Add((sn, cn, c.Clone()));
                }
            }

            if (work.Count == 0)
            {
                await AppendLogAsync("Batch: nothing to generate (only_missing).");
                await FinishAsync("done", "No clips to generate");
                return;
            }

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
                    await GenerateOneClipAsync(projectId, projectDir, sn, cn, clip, resolution, ct);
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

            var status = failed > 0 && done == 0 ? "error" : "done";
            var msg = failed > 0
                ? $"Batch finished with errors ({done} ok, {failed} failed)"
                : $"Batch finished ({done} clip(s))";
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
            if (!_grok.IsConfigured)
                throw new InvalidOperationException("XAI_API_KEY is not set.");

            using var bp = await _projects.LoadBlueprintAsync(projectId, ct)
                ?? throw new InvalidOperationException(
                    $"No Stage 2 blueprint for project {projectId}. Run Stage 2 first.");

            var sceneEl = FindScene(bp.RootElement, req.Scene)
                ?? throw new InvalidOperationException($"Scene {req.Scene} not in blueprint.");

            if (req.RequireLockedCharacters)
                EnsureCharactersLocked(projectId, req.Scene);

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
                    await GenerateOneClipAsync(projectId, projectDir, req.Scene, cn, clip, resolution, ct);
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
                }
            }

            var status = failed > 0 && done == 0 ? "error" : "done";
            var msg = failed > 0
                ? $"Finished with errors ({done} ok, {failed} failed)"
                : $"Generation finished ({done} clip(s))";
            await FinishAsync(status, msg, failed > 0 ? msg : null);
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

    private async Task GenerateOneClipAsync(
        string projectId,
        string projectDir,
        int scene,
        int clip,
        JsonElement clipEl,
        string resolution,
        CancellationToken ct)
    {
        var voices = _projects.LoadCharacterVoiceMap(projectId);
        var prompt = ClipVideoPromptBuilder.BuildPrompt(clipEl, projectDir, characterVoiceByKey: voices);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("clip missing visual_prompt");

        if (prompt.Contains("VOICE LOCK", StringComparison.OrdinalIgnoreCase))
            await AppendLogAsync("  [Voice] VOICE LOCK applied from character seed voice_profile");

        var refPaths = ClipVideoPromptBuilder.FindCharacterRefPaths(clipEl, projectDir, maxRefs: 3);
        if (refPaths.Count > 0)
            await AppendLogAsync(
                $"  [Refs] {refPaths.Count}: {string.Join(", ", refPaths.Select(Path.GetFileName))}");

        var duration = _opts.DefaultDurationSeconds;
        if (clipEl.TryGetProperty("duration_seconds", out var d) && d.TryGetInt32(out var ds))
            duration = Math.Clamp(ds, 1, 15);
        if (refPaths.Count > 0)
            duration = Math.Min(duration, 10);

        var model = _opts.DefaultModel;
        if (string.IsNullOrWhiteSpace(resolution))
            resolution = await ResolveVideoResolutionAsync(projectId, null, ct);

        await AppendLogAsync(
            $"  [Grok] Submit S{scene:D2}C{clip} duration={duration}s res={resolution} " +
            $"refs={refPaths.Count} promptLen={prompt.Length}");
        var requestId = await _grok.SubmitGenerationAsync(
            prompt, duration, resolution, model, ct, referenceImagePaths: refPaths);
        await AppendLogAsync($"  [Grok] request_id={requestId}");

        var url = await _grok.PollForVideoUrlAsync(
            requestId,
            msg => { _ = AppendLogAsync($"  [Grok] {msg}"); },
            ct);

        var outPath = Path.Combine(
            projectDir, "assets", "video", $"scene_{scene:D2}_clip_{clip:D2}.mp4");
        await _grok.DownloadToFileAsync(url, outPath, ct);
        await AppendLogAsync($"  [Grok] saved {outPath}");

        try
        {
            var cont = clipEl.TryGetProperty("veo_continuation_source", out var ce)
                ? ce.GetString() ?? "none"
                : "none";
            var costProjectId = Snapshot.ProjectId ?? projectId ?? _projects.ActiveProjectId;
            await _costs.RecordVideoGenerationAsync(
                costProjectId,
                scene,
                clip,
                duration,
                resolution,
                model,
                hasRefImage: refPaths.Count > 0,
                isExtend: string.Equals(cont, "extend_previous", StringComparison.OrdinalIgnoreCase),
                requestId: requestId,
                ct: ct);
            await AppendLogAsync($"  [Cost] tracked list-rate for S{scene:D2}C{clip}");
        }
        catch (Exception ex)
        {
            await AppendLogAsync($"  [Cost] ledger write skipped: {ex.Message}");
        }
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

    private void EnsureCharactersLocked(string projectId, int sceneNumber)
    {
        var unlocked = _projects.GetUnlockedOnScreenCharacters(projectId, sceneNumber);
        if (unlocked.Count == 0)
            return;

        var names = string.Join(", ", unlocked);
        throw new InvalidOperationException(
            $"Scene {sceneNumber}: locked character refs required before video gen. " +
            $"Missing lock(s): {names}. " +
            "Open Characters → lock a book plate or generate + lock a portrait. " +
            "(Narrator is voice-only and does not need an image.)");
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
        await UpdateAsync(s =>
        {
            s.Status = status;
            s.Message = message;
            s.Error = error;
            s.FinishedAt = DateTimeOffset.UtcNow;
            if (s.Total > 0 && status == "done")
                s.Index = s.Total;
            projectId = s.ProjectId;
        });
        await AppendLogAsync(message);

        // Scene list cache: clip/composite counts change on gen/remux/stage done
        if (status is "done" or "error" or "cancelled")
        {
            if (string.IsNullOrWhiteSpace(projectId))
                projectId = CurrentRun.Value?.Snapshot.ProjectId;
            _projects.InvalidateSceneListCache(projectId);
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
