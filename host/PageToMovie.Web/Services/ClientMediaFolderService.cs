using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;
using System.Linq;

namespace PageToMovie.Web.Services;

/// <summary>
/// Binds a user media folder and syncs gen clips from server proxy → disk → hash registry.
/// </summary>
public sealed class ClientMediaFolderService
{
    private readonly IJSRuntime _js;
    private readonly EngineApiClient _api;
    private readonly JobHubClient _hub;
    private readonly ActiveProjectState _activeProject;
    private bool _hubHooked;
    /// <summary>In-flight saves keyed by projectId|relativePath — avoids double JobUpdated.</summary>
    private readonly HashSet<string> _savingKeys = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Completed saves keyed by projectId|relativePath — a later notification for the same
    /// path (e.g. a single-clip job's "done" tick after its "running" tick already saved it) is a no-op.</summary>
    private readonly HashSet<string> _savedKeys = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Video-extend continuation-source duration (seconds), keyed by projectId|scene|clip,
    /// set by <see cref="PrepareExtendSourceAsync"/> right before requesting that clip's generation
    /// and consumed once by <see cref="SaveJobMediaAsync"/> to know where the new content starts
    /// inside the combined video Grok returns for a real video-extend call.</summary>
    private readonly Dictionary<string, double> _pendingExtendSourceSeconds = new(StringComparer.OrdinalIgnoreCase);

    public ClientMediaFolderService(IJSRuntime js, EngineApiClient api, JobHubClient hub, ActiveProjectState activeProject)
    {
        _js = js;
        _api = api;
        _hub = hub;
        _activeProject = activeProject;
    }

    /// <summary>
    /// When true, an explicit trigger (connecting a folder, or opening a project's media pages)
    /// checks whether any local media files are missing or out of date and syncs only those.
    /// Deliberately NOT fired on every page load / active-project change — that pulled the active
    /// project's whole media set on unrelated pages like the home screen. Defaults to true.
    /// </summary>
    public bool AutoSyncOnLogin { get; set; } = true;

    public void TriggerAutoSyncIfConnected()
    {
        if (AutoSyncOnLogin && IsConnected && !IsSyncing && !string.IsNullOrWhiteSpace(_activeProject.ProjectId))
        {
            _ = SyncProjectMediaToClientAsync(_activeProject.ProjectId);
        }
    }

    public string? FolderName { get; private set; }
    public string? FullPath { get; private set; }
    public bool IsConnected => !string.IsNullOrEmpty(FolderName) || !string.IsNullOrEmpty(FullPath);
    public string? LastStatus { get; private set; }

    public async Task SetFullPathAsync(string? path)
    {
        FullPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        try
        {
            await _js.InvokeVoidAsync("PageToMovieMedia.setFullPath", FullPath);
        }
        catch { /* ignore */ }
        Changed?.Invoke();
    }

    private async Task RefreshFullPathAsync()
    {
        try
        {
            var p = await _js.InvokeAsync<string?>("PageToMovieMedia.getFullPath");
            if (!string.IsNullOrWhiteSpace(p))
                FullPath = p.Trim();
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// A previously-connected folder was found (persisted via IndexedDB) but the browser needs a
    /// user gesture to re-grant permission — call <see cref="ReconnectAsync"/> from a button click.
    /// Set by <see cref="TryReconnectAsync"/>; distinct from "never connected" (<see cref="IsConnected"/> false,
    /// this also false) so the UI can offer a 1-click "Reconnect {name}" instead of a fresh folder picker.
    /// </summary>
    public bool NeedsReconnect { get; private set; }
    public string? PendingReconnectFolderName { get; private set; }

    /// <summary>
    /// One-shot operator message when a clip finished with a client proxy URL
    /// but was not saved to a local media folder (feature 8 / fallback path).
    /// </summary>
    public string? LocalSaveWarning { get; private set; }

    public event Action? Changed;

    public async Task EnsureHubHookAsync()
    {
        if (_hubHooked) return;
        _hubHooked = true;
        await _hub.EnsureStartedAsync();
        _hub.JobUpdated += OnJobUpdated;
    }

    private void OnJobUpdated(JobSnapshot snap)
    {
        if (snap is null) return;

        // TEMP diagnostic (see memory/session notes): music-generated audio was never reaching
        // the local folder despite generation succeeding server-side, with no JS console errors —
        // meaning SaveJobMediaAsync was seemingly never even being invoked. This makes every
        // JobUpdated tick visible via LastStatus (already surfaced on the Admin page) regardless
        // of which gate below returns early, so the next run pinpoints exactly which check is
        // swallowing music updates instead of guessing blind. Remove once root-caused.
        LastStatus = $"[diag] JobUpdated: kind={snap.Kind} status={snap.Status} " +
                     $"clientUrl={(string.IsNullOrEmpty(snap.ClientMediaUrl) ? "(null)" : "set")} " +
                     $"relPath={snap.ClientRelativePath ?? "(null)"} project={snap.ProjectId ?? "(null)"}";
        Changed?.Invoke();

        // "done"-only would drop every clip but the last in a multi-clip batch: ClientMediaUrl/
        // ClientRelativePath are set per-clip while Status stays "running" for the whole batch loop
        // (FilmJobService.RunBatchGenAsync → GenerateOneClipAsync), only flipping to "done" once, at
        // the very end. So both statuses must be accepted here; _savedKeys below is what prevents a
        // path that already saved on its "running" tick from being re-saved on a later "done" tick.
        if (!string.Equals(snap.Status, "done", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(snap.Status, "running", StringComparison.OrdinalIgnoreCase))
            return;
        if (string.IsNullOrWhiteSpace(snap.ClientMediaUrl) ||
            string.IsNullOrWhiteSpace(snap.ClientRelativePath) ||
            string.IsNullOrWhiteSpace(snap.ProjectId))
            return;

        var key = $"{snap.ProjectId}|{snap.ClientRelativePath}";
        lock (_savingKeys)
        {
            if (_savedKeys.Contains(key)) return; // already completed
        }
        LastStatus = $"[diag] Starting save: {snap.ClientRelativePath}";
        Changed?.Invoke();
        _ = SaveJobMediaAsync(snap);
    }

    public async Task<bool> ConnectFolderAsync()
    {
        try
        {
            var r = await _js.InvokeAsync<JsResult>("PageToMovieMedia.connectFolderAsync");
            if (r is { Success: true })
            {
                FolderName = r.FolderName;
                // Prefer path returned from JS (or previously stored full path if still valid).
                if (!string.IsNullOrWhiteSpace(r.FullPath))
                    FullPath = r.FullPath.Trim();
                else
                    await RefreshFullPathAsync();
                // Drop stale full path when its last segment no longer matches the folder name.
                if (!string.IsNullOrWhiteSpace(FullPath) && !string.IsNullOrWhiteSpace(FolderName))
                {
                    var normalized = FullPath.Replace('/', '\\').TrimEnd('\\');
                    var idx = normalized.LastIndexOf('\\');
                    var last = idx >= 0 ? normalized[(idx + 1)..] : normalized;
                    if (!string.Equals(last, FolderName, StringComparison.OrdinalIgnoreCase))
                    {
                        FullPath = null;
                        try { await _js.InvokeVoidAsync("PageToMovieMedia.setFullPath", (string?)null); } catch { /* ignore */ }
                    }
                }
                LastStatus = $"Media folder: {FullPath ?? FolderName}";
                LocalSaveWarning = null;
                NeedsReconnect = false;
                PendingReconnectFolderName = null;
                Changed?.Invoke();
                await EnsureHubHookAsync();
                TriggerAutoSyncIfConnected();
                return true;
            }
            LastStatus = r?.Error ?? "Could not connect folder";
            Changed?.Invoke();
            return false;
        }
        catch (Exception ex)
        {
            LastStatus = ex.Message;
            Changed?.Invoke();
            return false;
        }
    }

    /// <summary>
    /// Silent, no-gesture attempt to resume a previously-connected folder (the actual
    /// FileSystemDirectoryHandle persisted to IndexedDB by a prior <see cref="ConnectFolderAsync"/>).
    /// Call on app start (e.g. NavMenu's first render). If the browser still grants permission
    /// without asking, reconnects immediately with no UI at all. Otherwise sets
    /// <see cref="NeedsReconnect"/> so the UI can offer a 1-click "Reconnect" button wired to
    /// <see cref="ReconnectAsync"/> (which needs an actual click to satisfy the permission prompt).
    /// Never throws — a failed silent reconnect just leaves the folder disconnected, same as today.
    /// </summary>
    public async Task TryReconnectAsync()
    {
        if (IsConnected) return;
        try
        {
            var r = await _js.InvokeAsync<JsReconnectResult>("PageToMovieMedia.tryReconnectAsync");
            if (r is { Success: true })
            {
                FolderName = r.FolderName;
                await RefreshFullPathAsync();
                LastStatus = $"Media folder: {FullPath ?? FolderName}";
                NeedsReconnect = false;
                PendingReconnectFolderName = null;
                Changed?.Invoke();
                await EnsureHubHookAsync();
                // NOTE: no auto-sync here. This silent reconnect runs on app start (any page), so
                // syncing here re-pulled the active project's media on the home screen. The project's
                // own media pages trigger the sync explicitly instead.
                return;
            }
            if (string.Equals(r?.Reason, "prompt", StringComparison.OrdinalIgnoreCase))
            {
                NeedsReconnect = true;
                PendingReconnectFolderName = r!.FolderName;
                Changed?.Invoke();
            }
        }
        catch
        {
            // best-effort only — silent reconnect failures are not user-visible errors
        }
    }

    /// <summary>
    /// Re-grants permission on the remembered folder from a real user gesture (button click) — no
    /// folder-browser dialog, just a permission re-prompt on the same previously-chosen handle.
    /// </summary>
    public async Task<bool> ReconnectAsync()
    {
        try
        {
            var r = await _js.InvokeAsync<JsReconnectResult>("PageToMovieMedia.reconnectAsync");
            if (r is { Success: true })
            {
                FolderName = r.FolderName;
                LastStatus = $"Media folder: {FolderName}";
                NeedsReconnect = false;
                PendingReconnectFolderName = null;
                LocalSaveWarning = null;
                Changed?.Invoke();
                await EnsureHubHookAsync();
                TriggerAutoSyncIfConnected();
                return true;
            }
            LastStatus = r?.Error ?? "Could not reconnect folder";
            Changed?.Invoke();
            return false;
        }
        catch (Exception ex)
        {
            LastStatus = ex.Message;
            Changed?.Invoke();
            return false;
        }
    }

    /// <summary>Dismiss the local-save fallback warning (operator closed the banner).</summary>
    public void DismissLocalSaveWarning()
    {
        if (LocalSaveWarning is null) return;
        LocalSaveWarning = null;
        Changed?.Invoke();
    }

    private void NoteLocalSaveNeeded(string? connectError = null)
    {
        // Outcome-only copy (no server/provider jargon).
        if (!string.IsNullOrWhiteSpace(connectError) &&
            (connectError.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
             connectError.Contains("Edge", StringComparison.OrdinalIgnoreCase) ||
             connectError.Contains("does not support", StringComparison.OrdinalIgnoreCase) ||
             connectError.Contains("not support", StringComparison.OrdinalIgnoreCase)))
        {
            LocalSaveWarning =
                "Folder save requires Chrome or Edge. This clip is available for a limited time — open it soon, or use Chrome/Edge and connect a folder next time.";
        }
        else
        {
            LocalSaveWarning =
                "Your clip was generated but couldn’t be saved on this computer. Connect a folder to keep it permanently.";
        }
        LastStatus = LocalSaveWarning;
        Changed?.Invoke();
    }

    public async Task SaveJobMediaAsync(JobSnapshot snap)
    {
        var key = $"{snap.ProjectId}|{snap.ClientRelativePath}";
        lock (_savingKeys)
        {
            if (!_savingKeys.Add(key))
                return; // already saving this path
        }

        try
        {
            if (!IsConnected)
            {
                // Offer folder picker once; if declined / unsupported, surface feature-8 fallback.
                var ok = await ConnectFolderAsync();
                if (!ok)
                {
                    NoteLocalSaveNeeded(LastStatus);
                    return;
                }
            }

            LastStatus = $"Saving {snap.ClientRelativePath}…";
            Changed?.Invoke();

            var url = snap.ClientMediaUrl!;

            // Real video-extend (see FilmJobService.GenerateOneClipAsync + PrepareExtendSourceAsync
            // above): this job's video is Grok's combined [continuation-input + new content]
            // response, not a plain fresh generation. Slice out just the new tail before it ever
            // becomes this clip's saved/registered file — shipping the raw combined video would
            // reintroduce the exact "clip contains pieces of the previous clip" bug this feature
            // exists to fix, so on ANY failure here we surface it and return without saving,
            // rather than silently falling through to save the un-sliced video.
            var extendKey = $"{snap.ProjectId}|{snap.Scene}|{snap.Clip}";
            double? extendSourceSec = null;
            lock (_pendingExtendSourceSeconds)
            {
                if (_pendingExtendSourceSeconds.Remove(extendKey, out var sec))
                    extendSourceSec = sec;
            }
            string? extendSliceBlobUrl = null;
            if (extendSourceSec is { } srcSec)
            {
                var probe = await _js.InvokeAsync<JsProbeResult>("PageToMovieFfmpeg.probeDurationAsync", url);
                var combinedSec = probe is { Success: true, Seconds: > 0 } ? probe.Seconds : (double?)null;
                var newDurationSec = combinedSec is { } c && c > srcSec + 0.1 ? c - srcSec : (double?)null;
                var slice = newDurationSec is { } nd
                    ? await _js.InvokeAsync<JsTrimTailResult>("PageToMovieFfmpeg.trimTailAsync", url, nd, null)
                    : null;
                if (slice is not { Success: true } || string.IsNullOrWhiteSpace(slice.Url))
                {
                    LastStatus = $"Video-extend slice failed for {snap.ClientRelativePath} " +
                                 $"({slice?.Error ?? "duration probe failed"}) — retry the clip.";
                    Changed?.Invoke();
                    return;
                }
                extendSliceBlobUrl = slice.Url;
                url = slice.Url;
            }

            // Silence-trim in browser (ffmpeg.wasm) before write. Decision logic
            // (where to cut) lives once in ClipSilenceTrimmer (Core) — JS only does
            // the ffmpeg I/O. Longer breath tail for speech-style clips; lead trim on clip 2+.
            var clipNum = snap.Clip ?? 1;
            var isCredits = (snap.ClientRelativePath ?? "").Contains("credits", StringComparison.OrdinalIgnoreCase) ||
                            (snap.ClientRelativePath ?? "").Contains("sc18", StringComparison.OrdinalIgnoreCase) ||
                            snap.Scene == 18 ||
                            string.Equals(snap.Kind, "credits", StringComparison.OrdinalIgnoreCase);
            var isMusic = string.Equals(snap.Kind, "music", StringComparison.OrdinalIgnoreCase);
            var isSpeakBatch = string.Equals(snap.Kind, "speak-batch", StringComparison.OrdinalIgnoreCase);
            var keepTail = isCredits
                ? ClipSilenceTrimmer.DefaultKeepTailSeconds
                : ClipSilenceTrimmer.SpeechBreathTailSeconds; // safe default without dialogue metadata

            string? silenceMessage = null;
            string? trimmedBlobUrl = null;
            var urlToSave = url;
            // The extend slice is already tightly bounded to the requested new-content duration —
            // silence-trimming it further risks cutting real content rather than dead air, so skip
            // that pass entirely for this clip (unlike a plain fresh generation, which can be
            // arbitrarily longer than its useful content).
            // Music + speak-batch are pure audio files — never run video silence-trim.
            if (!isCredits && !isMusic && !isSpeakBatch && extendSliceBlobUrl is null)
            {
                var (trimmed, trimUrl, message) = await SilenceTrimAsync(
                    url,
                    keepTailSeconds: keepTail,
                    trimLeading: clipNum > 1,
                    keepHeadSeconds: 0.08);
                silenceMessage = message;
                if (trimmed && !string.IsNullOrWhiteSpace(trimUrl))
                {
                    trimmedBlobUrl = trimUrl;
                    urlToSave = trimUrl!;
                }
            }

            try
            {
                // Project-scoped path on the shared local folder — the server-facing bare path
                // (snap.ClientRelativePath) is kept separately below for RegisterMediaAsync, since
                // the server resolves that string under its own already-project-scoped directory
                // and would double-nest if it also carried this prefix.
                var clientPath = $"{snap.ProjectId}/{snap.ClientRelativePath}";
                var saved = await _js.InvokeAsync<JsSaveResult>(
                    "PageToMovieMedia.saveFromUrlAsync",
                    urlToSave,
                    clientPath,
                    null,
                    snap.MusicTakeId);

                if (saved is not { Success: true } || string.IsNullOrWhiteSpace(saved.Sha256))
                {
                    LastStatus = saved?.Error ?? "Save failed";
                    Changed?.Invoke();
                    return;
                }

                var scene = snap.Scene;
                var clip = snap.Clip;
                await _api.RegisterMediaAsync(snap.ProjectId!, new MediaRegisterRequest
                {
                    // Bare server-side path, NOT saved.RelativePath (which JS echoes back with the
                    // client-only project prefix baked in) — the server keys media_objects on the
                    // bare path within its own already-project-scoped directory.
                    RelativePath = snap.ClientRelativePath!,
                    Sha256 = saved.Sha256,
                    SizeBytes = saved.SizeBytes,
                    Kind = isCredits ? "credits" : isMusic ? "music" : isSpeakBatch ? "audio" : "clip",
                    Scene = scene,
                    Clip = clip,
                });

                var sil = string.IsNullOrWhiteSpace(silenceMessage)
                    ? ""
                    : $" · silence: {silenceMessage}";
                LastStatus =
                    $"Saved {Path.GetFileName(snap.ClientRelativePath)} ({saved.SizeBytes / 1024} KB){sil}";
                Changed?.Invoke();

                lock (_savingKeys)
                    _savedKeys.Add(key);
            }
            finally
            {
                if (trimmedBlobUrl is not null)
                {
                    try { await _js.InvokeVoidAsync("PageToMovieMedia.revokeUrl", trimmedBlobUrl); }
                    catch { /* best effort */ }
                }
                if (extendSliceBlobUrl is not null)
                {
                    try { await _js.InvokeVoidAsync("PageToMovieMedia.revokeUrl", extendSliceBlobUrl); }
                    catch { /* best effort */ }
                }
            }
        }
        catch (Exception ex)
        {
            LastStatus = ex.Message;
            Changed?.Invoke();
        }
        finally
        {
            lock (_savingKeys)
                _savingKeys.Remove(key);
        }
    }

    /// <summary>
    /// Analyze a clip's silence (browser ffmpeg.wasm), decide cut points with the real
    /// <see cref="ClipSilenceTrimmer"/> math (no JS port to drift), and either encode the
    /// trimmed slice or discard the analysis session. Never throws — failures degrade to
    /// "not trimmed" so a save is never blocked by a browser/codec hiccup.
    /// </summary>
    private async Task<(bool Trimmed, string? Url, string? Message)> SilenceTrimAsync(
        string url,
        double keepTailSeconds,
        bool trimLeading,
        double keepHeadSeconds,
        double minTrimSavings = 0.4)
    {
        string? token = null;
        try
        {
            var analysis = await _js.InvokeAsync<JsSilenceAnalysis>(
                "PageToMovieFfmpeg.analyzeSilenceAsync", url, new { });
            if (analysis is not { Success: true })
                return (false, null, "skip: " + (analysis?.Error ?? "analyze failed"));
            if (analysis.Token is null)
                return (false, null, analysis.Error ?? "skip: nothing to analyze");

            token = analysis.Token;
            var total = analysis.TotalSec;
            double startSec = 0, endSec = total;
            var notes = new List<string>();

            var cutAt = ClipSilenceTrimmer.ComputeCutPoint(analysis.Log ?? "", total, keepTailSeconds);
            if (cutAt is { } cut && (total - cut) >= minTrimSavings)
            {
                endSec = cut;
                notes.Add($"tail −{(total - cut):F2}s");
            }

            if (trimLeading)
            {
                var lead = ClipSilenceTrimmer.ComputeLeadInPoint(analysis.Log ?? "", total, keepHeadSeconds);
                if (lead is { } l && l >= 0.25 && endSec - l >= ClipSilenceTrimmer.MinClipSeconds - 0.25)
                {
                    startSec = l;
                    notes.Add($"head −{l:F2}s");
                }
            }

            if (startSec <= 0.001 && endSec >= total - 0.05)
            {
                await _js.InvokeVoidAsync("PageToMovieFfmpeg.discardSessionAsync", token);
                token = null;
                return (false, null, notes.Count > 0 ? string.Join("; ", notes) : "skip: no trailing/leading silence");
            }

            var durationSec = Math.Max(0.5, endSec - startSec);
            var enc = await _js.InvokeAsync<JsSilenceEncode>(
                "PageToMovieFfmpeg.encodeSliceAsync", token, startSec, durationSec);
            token = null; // encodeSliceAsync always consumes/cleans up the session
            if (enc is not { Success: true } || string.IsNullOrWhiteSpace(enc.Url))
                return (false, null, "skip: re-encode failed — " + (enc?.Error ?? ""));

            return (true, enc.Url, notes.Count > 0 ? string.Join("; ", notes) : "trimmed");
        }
        catch (Exception ex)
        {
            return (false, null, "skip: " + ex.Message);
        }
        finally
        {
            if (token is not null)
            {
                try { await _js.InvokeVoidAsync("PageToMovieFfmpeg.discardSessionAsync", token); }
                catch { /* best effort */ }
            }
        }
    }

    public async Task<string?> GetLocalBlobUrlAsync(string projectId, string relativePath)
    {
        if (!IsConnected) return null;
        try
        {
            var clientPath = $"{projectId}/{relativePath}";
            var r = await _js.InvokeAsync<JsBlobResult>("PageToMovieMedia.getBlobUrlAsync", clientPath, false);
            return r is { Success: true } ? r.Url : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Cheap metadata-only check of a local file — no blob URL created, no bytes read.</summary>
    public async Task<(bool Found, long SizeBytes)> StatLocalFileAsync(string projectId, string relativePath)
    {
        if (!IsConnected) return (false, 0);
        try
        {
            var clientPath = $"{projectId}/{relativePath}";
            var r = await _js.InvokeAsync<JsStatResult>("PageToMovieMedia.statLocalFileAsync", clientPath);
            return r is { Success: true } ? (true, r.SizeBytes) : (false, 0);
        }
        catch
        {
            return (false, 0);
        }
    }

    /// <summary>Compute SHA-256 content hash of a local file in the media folder.</summary>
    public async Task<(bool Found, string? Sha256, long SizeBytes)> Sha256LocalFileAsync(string projectId, string relativePath)
    {
        if (!IsConnected) return (false, null, 0);
        try
        {
            var clientPath = $"{projectId}/{relativePath}";
            var r = await _js.InvokeAsync<JsStatResult>("PageToMovieMedia.sha256LocalFileAsync", clientPath);
            return r is { Success: true } ? (true, r.Sha256, r.SizeBytes) : (false, null, 0);
        }
        catch
        {
            return (false, null, 0);
        }
    }

    /// <summary>
    /// The generalized "is my local copy of this file still current" check every media-playback
    /// call site should use instead of trusting whatever happens to be at that path — a local
    /// file at the expected filename is not guaranteed to be the *current* version (a later
    /// regen/promote may never have re-synced it to this browser). Works for any media kind
    /// (video clip today, audio track once that syncs the same way) since it's driven entirely
    /// by size comparison against the server's registered value, not clip-specific fields.
    /// Returns the local blob URL only when the local file's size matches what the server has
    /// registered as current; otherwise null, so the caller falls back to streaming from server.
    /// </summary>
    public async Task<string?> GetCurrentBlobUrlAsync(string projectId, string relativePath, long? expectedSizeBytes)
    {
        if (!IsConnected) return null;
        if (!await IsLocalCopyCurrentAsync(projectId, relativePath, expectedSizeBytes)) return null;
        return await GetLocalBlobUrlAsync(projectId, relativePath);
    }

    /// <summary>
    /// Shared freshness check behind <see cref="GetCurrentBlobUrlAsync"/> and
    /// <see cref="GetClipBytesAsync"/> — true when no expected size was given (nothing to compare
    /// against) or the local file's size matches it. Caller must have already confirmed
    /// <see cref="IsConnected"/>.
    /// </summary>
    private async Task<bool> IsLocalCopyCurrentAsync(string projectId, string relativePath, long? expectedSizeBytes)
    {
        if (expectedSizeBytes is null or <= 0) return true;
        var (found, localSize) = await StatLocalFileAsync(projectId, relativePath);
        return found && localSize == expectedSizeBytes;
    }

    public async Task<(bool Ok, string? Sha, long Size, string? Error)> RegisterBlobAsExportAsync(
        string projectId,
        string blobUrl,
        string relativePath)
    {
        try
        {
            if (!IsConnected && !await ConnectFolderAsync())
                return (false, null, 0, "Media folder required");

            // Client-only prefixed path for the shared local folder; the bare relativePath (not
            // saved.RelativePath, which JS echoes back prefixed) is what the server expects below.
            var clientPath = $"{projectId}/{relativePath}";
            var saved = await _js.InvokeAsync<JsSaveResult>(
                "PageToMovieMedia.saveBlobUrlAsync", blobUrl, clientPath);
            if (saved is not { Success: true } || string.IsNullOrWhiteSpace(saved.Sha256))
                return (false, null, 0, saved?.Error ?? "Save failed");

            await _api.RegisterMediaAsync(projectId, new MediaRegisterRequest
            {
                RelativePath = relativePath,
                Sha256 = saved.Sha256,
                SizeBytes = saved.SizeBytes,
                Kind = "export",
            });
            return (true, saved.Sha256, saved.SizeBytes, null);
        }
        catch (Exception ex)
        {
            return (false, null, 0, ex.Message);
        }
    }

    /// <summary>
    /// Copies an archived audio take's segment bytes back to their active paths within the local
    /// media folder — the client-side half of promoting a take (see ProjectStore.PromoteMusicVersionAsync
    /// for the server-side sidecar-metadata half, which must also be called). The bytes never leave
    /// the browser: unlike a fresh generation, there's no server proxy URL to download from, only a
    /// copy from one local path to another. <paramref name="archiveTakeId"/> should be the currently
    /// active take's own id (so whatever's being displaced archives under a real, identifiable id,
    /// same as a fresh regeneration would).
    /// </summary>
    public async Task<bool> PromoteMusicTakeAsync(string projectId, MusicVersionItem target, string archiveTakeId)
    {
        if (!IsConnected) return false;
        try
        {
            for (var i = 0; i < target.SegmentFileNames.Count; i++)
            {
                var fileName = target.SegmentFileNames[i];
                var fromRel = i < target.RelativePaths.Count ? target.RelativePaths[i] : $"assets/music/history/{fileName}";
                var toRel = $"assets/music/{fileName}";
                var fromClientPath = $"{projectId}/{fromRel}";
                var toClientPath = $"{projectId}/{toRel}";
                var res = await _js.InvokeAsync<JsSaveResult>(
                    "PageToMovieMedia.copyLocalFileAsync", fromClientPath, toClientPath, archiveTakeId);
                if (res is not { Success: true }) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Archived previous versions of one clip's video, newest first (see ClipPromptCompareViewer).
    /// Unlike every other method here, the returned paths already include the project prefix (JS
    /// builds them from the dirPrefix passed in) — pass them straight to GetLocalBlobUrlAsync's
    /// relativePath without adding projectId again.</summary>
    public async Task<IReadOnlyList<string>> ListClipHistoryRelativePathsAsync(string projectId, int scene, int clip)
    {
        if (!IsConnected) return Array.Empty<string>();
        try
        {
            var dirPrefix = $"{projectId}/assets/video/history";
            var r = await _js.InvokeAsync<JsHistoryResult>(
                "PageToMovieMedia.listClipHistoryAsync", dirPrefix, scene, clip);
            return r is { Success: true, Entries: not null }
                ? r.Entries.Select(e => e.RelativePath ?? "").Where(p => p.Length > 0).ToList()
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>True while MP4/sidecar files are actively downloading to the local client folder.</summary>
    public bool IsSyncing { get; private set; }
    public int SyncCurrent { get; private set; }
    public int SyncTotal { get; private set; }
    public string? SyncCurrentFile { get; private set; }
    public string? SyncProjectId { get; private set; }
    public double SyncPercent => SyncTotal > 0 ? Math.Round((double)SyncCurrent / SyncTotal * 100.0, 0) : 0;

    /// <summary>
    /// Sync project media files (MP4s and sidecars) from server to client local media folder.
    /// Called after Admin import or project load when a client folder is connected.
    /// </summary>
    public async Task<int> SyncProjectMediaToClientAsync(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return 0;

        if (!IsConnected)
        {
            await TryReconnectAsync();
        }

        if (!IsConnected)
        {
            LastStatus = "Connect local media folder to save project videos locally";
            Changed?.Invoke();
            return 0;
        }

        try
        {
            var syncList = await _api.GetProjectMediaSyncListAsync(projectId);
            if (syncList?.Files is null || syncList.Files.Count == 0)
            {
                LastStatus = "No media files to sync.";
                Changed?.Invoke();
                return 0;
            }

            // Smart Double-Lock Pre-Check: skip files that already exist locally with matching size AND
            // content hash. Each skip/fetch decision is logged (browser console) with its reason so a
            // file that keeps re-downloading every visit can be pinned to missing / size / hash.
            var outOfDateFiles = new List<ProjectMediaSyncFile>();
            var reasons = new List<string>();
            foreach (var file in syncList.Files)
            {
                var (found, localSize) = await StatLocalFileAsync(projectId, file.RelativePath);
                string? reason = null;
                if (!found) reason = "missing locally";
                else if (file.SizeBytes <= 0) reason = "server size unknown";
                else if (localSize != file.SizeBytes) reason = $"size {localSize} != server {file.SizeBytes}";
                else if (!string.IsNullOrWhiteSpace(file.Sha256))
                {
                    // Size matched — double-check the SHA-256 to catch byte-level content changes.
                    var (hasSha, localSha, _) = await Sha256LocalFileAsync(projectId, file.RelativePath);
                    if (!hasSha) reason = "local hash unavailable";
                    else if (!string.Equals(localSha, file.Sha256, StringComparison.OrdinalIgnoreCase)) reason = "hash differs";
                }

                if (reason is not null)
                {
                    outOfDateFiles.Add(file);
                    reasons.Add($"{file.RelativePath} — {reason}");
                }
            }

            if (reasons.Count > 0)
                Console.WriteLine($"[media-sync] {projectId}: fetching {reasons.Count}/{syncList.Files.Count} —\n  " + string.Join("\n  ", reasons));

            if (outOfDateFiles.Count == 0)
            {
                LastStatus = "All project media files are already up-to-date on local disk.";
                Changed?.Invoke();
                return 0;
            }

            IsSyncing = true;
            SyncProjectId = projectId;
            SyncCurrent = 0;
            SyncTotal = outOfDateFiles.Count;
            SyncCurrentFile = null;

            LastStatus = $"Syncing {outOfDateFiles.Count} out-of-date media file(s) to local folder…";
            Changed?.Invoke();

            var count = 0;
            for (var i = 0; i < outOfDateFiles.Count; i++)
            {
                var file = outOfDateFiles[i];
                SyncCurrent = i + 1;
                SyncCurrentFile = file.FileName;
                LastStatus = $"Downloading {file.FileName} to local folder ({SyncCurrent}/{SyncTotal})…";
                Changed?.Invoke();

                if (string.IsNullOrWhiteSpace(file.StreamUrl))
                    continue;

                // Client-only prefixed path for the shared local folder; the manifest's bare
                // file.RelativePath (not saved.RelativePath, echoed back prefixed) is what the
                // server expects in RegisterMediaAsync below.
                var clientPath = $"{projectId}/{file.RelativePath}";
                var saved = await _js.InvokeAsync<JsSaveResult>(
                    "PageToMovieMedia.saveFromUrlAsync",
                    file.StreamUrl,
                    clientPath,
                    null);

                if (saved is { Success: true } && !string.IsNullOrWhiteSpace(saved.Sha256))
                {
                    count++;
                    await _api.RegisterMediaAsync(projectId, new MediaRegisterRequest
                    {
                        RelativePath = file.RelativePath,
                        Sha256 = saved.Sha256,
                        SizeBytes = saved.SizeBytes,
                        Kind = file.IsMp4 ? "clip" : "sidecar",
                    });
                }
            }

            LastStatus = $"Media folder synced: {count} missing/updated file(s) saved on local disk";
            Changed?.Invoke();
            return count;
        }
        catch (Exception ex)
        {
            LastStatus = $"Sync error: {ex.Message}";
            Changed?.Invoke();
            return 0;
        }
        finally
        {
            IsSyncing = false;
            SyncCurrentFile = null;
            Changed?.Invoke();
        }
    }

    private sealed class JsResult
    {
        public bool Success { get; set; }
        public string? FolderName { get; set; }
        public string? FullPath { get; set; }
        public string? Error { get; set; }
    }

    private sealed class JsReconnectResult
    {
        public bool Success { get; set; }
        public string? FolderName { get; set; }
        /// <summary>When !Success: "none" (never connected before), "prompt" (needs a user gesture), "denied", or "error".</summary>
        public string? Reason { get; set; }
        public string? Error { get; set; }
    }

    private sealed class JsHistoryResult
    {
        public bool Success { get; set; }
        public List<JsHistoryEntry>? Entries { get; set; }
        public string? Error { get; set; }
    }

    private sealed class JsHistoryEntry
    {
        public string? RelativePath { get; set; }
        public long TimestampMs { get; set; }
    }

    private sealed class JsSaveResult
    {
        public bool Success { get; set; }
        public string? Sha256 { get; set; }
        public long SizeBytes { get; set; }
        public string? RelativePath { get; set; }
        public string? Error { get; set; }
    }

    private sealed class JsSilenceAnalysis
    {
        public bool Success { get; set; }
        public string? Token { get; set; }
        public double TotalSec { get; set; }
        public string? Log { get; set; }
        public string? Error { get; set; }
    }

    private sealed class JsSilenceEncode
    {
        public bool Success { get; set; }
        public string? Url { get; set; }
        public string? Error { get; set; }
    }

    private sealed class JsTrimTailResult
    {
        public bool Success { get; set; }
        public string? Url { get; set; }
        public double SourceDurationSec { get; set; }
        public double KeptSec { get; set; }
        public string? Error { get; set; }
    }

    private sealed class JsProbeResult
    {
        public bool Success { get; set; }
        public double Seconds { get; set; }
    }

    private sealed class JsUploadResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>Local blob URLs for a scene's background-music segments (in order), stopping at
    /// the first missing segment. Segment relative paths mirror
    /// MediaRegistryService.MusicSegmentRelativePath in PageToMovie.Engine (Web doesn't reference
    /// Engine, so the format is kept in sync here — same convention as the clip path below).</summary>
    public async Task<IReadOnlyList<string>> GetSceneMusicSegmentUrlsAsync(string projectId, int scene, int maxSegments = 20)
    {
        var urls = new List<string>();
        if (!IsConnected) return urls;
        for (var seg = 1; seg <= maxSegments; seg++)
        {
            var relPath = $"assets/music/scene_{scene:D2}_seg_{seg:D2}.wav";
            var url = await GetLocalBlobUrlAsync(projectId, relPath);
            if (string.IsNullOrWhiteSpace(url)) break;
            urls.Add(url);
        }
        return urls;
    }

    /// <summary>
    /// Client-side "prepare" step for real video-extend continuity (see FilmJobService.
    /// GenerateOneClipAsync): trims the previous clip's current local video down to the model's
    /// max input length and uploads it as the continuation source for the clip about to be
    /// generated. Call this before starting generation for <paramref name="clip"/> when the shot
    /// plan says it wants to extend from clip-1 and the active model supports real continue.
    /// Never throws and never blocks generation — a false return just means the server won't find
    /// an extend-source file and falls back to its default fresh-generation behavior, exactly as
    /// if this feature didn't exist (no local folder connected, no local copy of the previous
    /// clip yet, or a browser/codec hiccup are all treated the same way).
    /// </summary>
    public async Task<bool> PrepareExtendSourceAsync(
        string projectId, int scene, int clip, double maxInputSeconds)
    {
        if (clip <= 1 || !IsConnected) return false;
        string? trimUrl = null;
        try
        {
            var prevRelPath = $"assets/video/scene_{scene:D2}_clip_{clip - 1:D2}.mp4";
            var sourceUrl = await GetLocalBlobUrlAsync(projectId, prevRelPath);
            if (string.IsNullOrWhiteSpace(sourceUrl)) return false;

            var trim = await _js.InvokeAsync<JsTrimTailResult>(
                "PageToMovieFfmpeg.trimTailAsync", sourceUrl, maxInputSeconds, null);
            if (trim is not { Success: true } || string.IsNullOrWhiteSpace(trim.Url)) return false;
            trimUrl = trim.Url;

            var uploadUrl = _api.ClipUploadUrl(projectId, scene, clip, kind: "extend-source");
            var up = await _js.InvokeAsync<JsUploadResult>(
                "PageToMovieMedia.uploadUrlToServerAsync", trimUrl, uploadUrl);
            if (up is not { Success: true }) return false;

            lock (_pendingExtendSourceSeconds)
                _pendingExtendSourceSeconds[$"{projectId}|{scene}|{clip}"] = trim.KeptSec;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (trimUrl is not null)
            {
                try { await _js.InvokeVoidAsync("PageToMovieMedia.revokeUrl", trimUrl); }
                catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Local clip bytes for upload (e.g. dialogue re-verification). Same staleness guard as
    /// <see cref="GetCurrentBlobUrlAsync"/> — without it, a stale local copy (an older take that
    /// hasn't been overwritten by a since-promoted regeneration) gets silently uploaded and
    /// verified as if it were current, making re-verification look like it never picked up the
    /// new clip. Pass the server's currently-registered size (GetClipMediaStatusAsync) to enable
    /// the check; omit it to keep the old unconditional-trust behavior for other callers.
    /// </summary>
    public async Task<byte[]?> GetClipBytesAsync(string projectId, int scene, int clip, long? expectedSizeBytes = null)
    {
        if (!IsConnected) return null;
        try
        {
            var relPath = $"assets/video/scene_{scene:D2}_clip_{clip:D2}.mp4";
            if (!await IsLocalCopyCurrentAsync(projectId, relPath, expectedSizeBytes)) return null;
            var clientPath = $"{projectId}/{relPath}";
            var res = await _js.InvokeAsync<JsBytesResult>("PageToMovieMedia.getBytesAsync", clientPath);
            return res is { Success: true, Bytes: not null } ? res.Bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class JsBytesResult
    {
        public bool Success { get; set; }
        public byte[]? Bytes { get; set; }
        public string? Error { get; set; }
    }

    private sealed class JsBlobResult
    {
        public bool Success { get; set; }
        public string? Url { get; set; }
        public long SizeBytes { get; set; }
        public string? Error { get; set; }
    }

    private sealed class JsStatResult
    {
        public bool Success { get; set; }
        public long SizeBytes { get; set; }
        public string? Sha256 { get; set; }
        public long LastModifiedMs { get; set; }
        public string? Error { get; set; }
    }
    /// <summary>
    /// Write bytes into the connected media folder (e.g. voice clone samples).
    /// By default does <b>not</b> open a folder picker mid-flow — tries silent reconnect only.
    /// Pass <paramref name="promptToConnectFolder"/> only from an explicit "Connect folder" control.
    /// </summary>
    public async Task<(bool Ok, string? RelativePath, string? Sha256, long SizeBytes, string? Error)> SaveBytesAsync(
        string projectId, string relativePath, byte[] bytes, bool promptToConnectFolder = false)
    {
        if (bytes is null || bytes.Length == 0)
            return (false, null, null, 0, "Empty audio");
        if (!IsConnected)
            await TryReconnectAsync();
        if (!IsConnected)
        {
            if (!promptToConnectFolder)
                return (false, null, null, 0, "Media folder not connected — sample still saved on the project");
            var ok = await ConnectFolderAsync();
            if (!ok) return (false, null, null, 0, LastStatus ?? "Connect a media folder first");
        }
        try
        {
            var clientPath = relativePath.StartsWith(projectId + "/", StringComparison.OrdinalIgnoreCase)
                ? relativePath
                : $"{projectId.Trim()}/{relativePath.TrimStart('/')}";
            var b64 = Convert.ToBase64String(bytes);
            var res = await _js.InvokeAsync<JsSaveBytesResult>(
                "PageToMovieMedia.saveBytesBase64Async", b64, clientPath);
            if (res is { Success: true })
            {
                LastStatus = $"Saved {Path.GetFileName(clientPath)} to media folder";
                Changed?.Invoke();
                return (true, res.RelativePath ?? clientPath, res.Sha256, res.SizeBytes, null);
            }
            return (false, null, null, 0, res?.Error ?? "Could not write to media folder");
        }
        catch (Exception ex)
        {
            return (false, null, null, 0, ex.Message);
        }
    }

    /// <summary>List audio files under the media folder (optional project prefix).</summary>
    public async Task<IReadOnlyList<LocalAudioFile>> ListAudioFilesAsync(string? projectId = null)
    {
        if (!IsConnected) return Array.Empty<LocalAudioFile>();
        try
        {
            var prefix = string.IsNullOrWhiteSpace(projectId) ? "" : projectId.Trim();
            var res = await _js.InvokeAsync<JsListAudioResult>("PageToMovieMedia.listAudioFilesAsync", prefix);
            if (res is not { Success: true, Files: not null }) return Array.Empty<LocalAudioFile>();
            return res.Files
                .Select(f => new LocalAudioFile
                {
                    RelativePath = f.RelativePath ?? "",
                    Name = f.Name ?? Path.GetFileName(f.RelativePath ?? "") ?? "audio",
                    SizeBytes = f.SizeBytes,
                })
                .Where(f => f.RelativePath.Length > 0)
                .ToList();
        }
        catch
        {
            return Array.Empty<LocalAudioFile>();
        }
    }

    /// <summary>Read a file already in the media folder as bytes.</summary>
    public async Task<byte[]?> ReadLocalBytesAsync(string relativePath, int minBytes = 0)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(relativePath)) return null;
        try
        {
            var res = await _js.InvokeAsync<JsBytesResult>("PageToMovieMedia.getBytesAsync", relativePath, minBytes);
            return res is { Success: true, Bytes: not null } ? res.Bytes : null;
        }
        catch { return null; }
    }

    public sealed class LocalAudioFile
    {
        public string RelativePath { get; set; } = "";
        public string Name { get; set; } = "";
        public long SizeBytes { get; set; }
    }

    private sealed class JsSaveBytesResult
    {
        public bool Success { get; set; }
        public string? RelativePath { get; set; }
        public string? Error { get; set; }
        public long SizeBytes { get; set; }
        public string? Sha256 { get; set; }
    }

    private sealed class JsListAudioResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public List<JsListAudioEntry>? Files { get; set; }
    }

    private sealed class JsListAudioEntry
    {
        public string? RelativePath { get; set; }
        public string? Name { get; set; }
        public long SizeBytes { get; set; }
    }


}
