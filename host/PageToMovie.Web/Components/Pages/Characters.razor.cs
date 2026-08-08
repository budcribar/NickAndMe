using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Characters
{
    private enum Mode { PickSource, WaitingGenerate, Compare }

    /// <summary>First choose how to set the look — only that path's UI is shown.</summary>
    private enum PictureRoute { Choose, Generate, Upload, Book }

    private sealed class Candidate
    {
        public string Kind { get; init; } = ""; // book | variant | locked | preferred
        public int Index { get; init; }
        public string Label { get; init; } = "";
        public string Url { get; init; } = "";
    }

    private sealed class PendingDelete
    {
        public string Kind { get; init; } = "";
        public int Index { get; init; }
    }

    private string? PreferredImageUrl
    {
        get
        {
            if (_selected is null || !_selected.HasPreferred) return null;
            return ListThumbUrl(_selected);
        }
    }

    /// <summary>List thumbnail: preferred/lock/variant1, else first book pic, else null (empty placeholder).</summary>
    private string? ListThumbUrl(CharacterSummary c)
    {
        if (c.VoiceOnly) return null;
        // Groups may optionally have a ref; do not invent one for list thumbs.
        // Locked / preferred ref always wins over old generate variants (uploads used to look
        // "gone" after reload when variant_01 still existed and was preferred in the list).
        if (c.Locked || !string.IsNullOrWhiteSpace(c.RefUrl))
            return CacheBust(Engine.CharacterRefUrl(_projectId, c.Key));
        if (c.HasPreferred)
        {
            if (c.PreferredUrl is { Length: > 0 } u)
                return CacheBust(Engine.AbsolutizeMediaUrl(u) ?? u);
            if (c.RefUrl is { Length: > 0 } r)
                return CacheBust(Engine.AbsolutizeMediaUrl(r) ?? r);
            if (c.Variants.Any(v => v.Index == 1 && v.Exists))
                return CacheBust(Engine.CharacterVariantUrl(_projectId, c.Key, 1));
        }
        var book = c.BookRefs.FirstOrDefault(b => b.Exists);
        if (book is not null)
        {
            if (!string.IsNullOrEmpty(book.Url))
                return CacheBust(Engine.AbsolutizeMediaUrl(book.Url) ?? book.Url);
            if (book.Index is int bi)
                return CacheBust(Engine.CharacterBookRefUrl(_projectId, c.Key, bi));
        }
        return null;
    }

    private static bool HasVoiceProfile(CharacterSummary c) =>
        !string.IsNullOrWhiteSpace(c.VoiceProfile);

    private bool ShowVoiceFields(CharacterSummary c)
    {
        if (_forceShowVoice) return true;               // explicit "Add voice…" opt-in
        if (c.HasVoiceCloneSample) return true;         // user recorded a clone sample
        // A SILENT non-human (background animal, e.g. a lamb) gets no voice prompt — and a cast-extraction
        // auto-fill (e.g. "soft lamb bleat") must not force it to show. A TALKING animal speaks, so it gets a
        // voice like any speaker and falls through. Keyed on whether the character speaks, not species alone.
        var isNonHuman = !string.IsNullOrWhiteSpace(c.SpeciesKind)
            && !c.SpeciesKind!.Trim().Equals("human", StringComparison.OrdinalIgnoreCase);
        if (isNonHuman && !c.Speaks) return false;
        if (c.Speaks) return true;                      // any speaking role offers a voice
        if (HasVoiceProfile(c)) return true;
        if (!string.IsNullOrWhiteSpace(c.VoiceLabel)) return true;
        if (!string.IsNullOrWhiteSpace(_editVoiceProfile) || !string.IsNullOrWhiteSpace(_editVoiceLabel))
            return true;
        return false;
    }

    private string PreferredImageLabel =>
        _selected is null ? ""
        : _selected.HasPreferred
            ? $"Preferred · {_selected.PreferredLabel}"
            : "No preferred image";

    private bool _busy;
    private bool _gateChecked;
    private string? _error;
    private string? _message;
    private List<string>? _lastCastExtractKeys;
    private string _projectId = "";
    private List<string> _projectIds = new();
    private List<CharacterSummary>? _chars;
    private CharacterPlatesState? _plates;
    private string? _selectedKey;
    private CharacterSummary? _selected;
    /// <summary>Look chosen in Compare mode, not yet confirmed locked (auto-flushed on cast switch).</summary>
    private Candidate? _pendingLockCandidate;
    private string? _chosenCandidateKey;
    private JobSnapshot? _job;
    private long _imgBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private Mode _mode = Mode.PickSource;
    private PictureRoute _pictureRoute = PictureRoute.Choose;

    private List<Candidate> _allCandidates = new();
    private Candidate? _zoomCandidate;
    private double _zoomScale = 1;
    private PendingDelete? _deleteConfirm;

    private bool _showBookCandidateGallery;
    private bool _loadingBookCandidates;
    private bool _savingBookRefs;
    private List<RankedBookCandidateDto>? _rankedBookCandidates;
    private readonly List<string> _selectedBookCandidatePaths = new();

    private async Task ToggleBookCandidateGalleryAsync()
    {
        _showBookCandidateGallery = !_showBookCandidateGallery;
        if (_showBookCandidateGallery && _selected is not null)
        {
            await LoadBookCandidatesAsync();
        }
    }

    private async Task LoadBookCandidatesAsync()
    {
        if (_selected is null || string.IsNullOrWhiteSpace(_projectId)) return;
        _loadingBookCandidates = true;
        _error = null;
        StateHasChanged();
        try
        {
            _rankedBookCandidates = await Engine.GetRankedBookCandidatesAsync(_projectId, _selected.Key);
            _selectedBookCandidatePaths.Clear();
            if (_rankedBookCandidates is not null)
            {
                foreach (var c in _rankedBookCandidates)
                {
                    if (c.IsSelected)
                        _selectedBookCandidatePaths.Add(c.PathRel);
                }
            }
        }
        catch (Exception ex)
        {
            _error = "Could not load book image candidates: " + ex.Message;
        }
        finally
        {
            _loadingBookCandidates = false;
            StateHasChanged();
        }
    }

    private void ToggleBookCandidateSelection(string pathRel)
    {
        var idx = _selectedBookCandidatePaths.FindIndex(p => string.Equals(p, pathRel, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            _selectedBookCandidatePaths.RemoveAt(idx);
        }
        else
        {
            if (_selectedBookCandidatePaths.Count >= 3)
            {
                _error = "Select up to 3 candidate pictures maximum.";
                return;
            }
            _selectedBookCandidatePaths.Add(pathRel);
        }
        _error = null;
    }

    private async Task ApplySelectedBookCandidatesAsync()
    {
        if (_selected is null || string.IsNullOrWhiteSpace(_projectId)) return;
        if (_selectedBookCandidatePaths.Count == 0)
        {
            // Clear stored book refs when nothing selected
            _savingBookRefs = true;
            try
            {
                await Engine.SetCharacterBookRefsAsync(_projectId, _selected.Key, []);
                await SoftReloadAsync();
                _seedOrder.Clear();
                _message = "Book pictures cleared.";
                _showBookCandidateGallery = false;
            }
            catch (Exception ex) { _error = ex.Message; }
            finally { _savingBookRefs = false; }
            return;
        }

        var ok = await EnsureGalleryBookSelectionAppliedAsync();
        if (ok && _selected is not null)
        {
            _message =
                $"Saved {_seedOrder.Count} book picture(s) for {_selected.DisplayName}. " +
                "Only those are selected for Generate (click Book tiles to change).";
        }
    }

    private int ApiMaxSeedRefs => Math.Max(1, _imageSeedLimits?.MaxReferenceImages ?? 3);

    private ImageSeedLimits? _imageSeedLimits;
    private readonly List<string> _seedOrder = new(); // "p", "v1", "b0"… click order = priority
    private string _editDescription = "";
    private string _editVisualLock = "";
    /// <summary>Last loaded/saved look text — skip scrub API when editors match.</summary>
    private string _savedLookDescription = "";
    private string _savedLookVisualLock = "";
    private bool _savingLook;
    private string? _lookSaveHint;
    private CancellationTokenSource? _lookSaveCts;
    private const int LookAutosaveDebounceMs = 800;
    private string _editVoiceLabel = "";
    private string _editVoiceProfile = "";
    private bool _panelPictureOpen = true;
    private bool _forceShowVoice;
    private bool _voicePreviewBusy;
    private string? _voicePreviewUrl;
    private string? _voicePreviewError;
    private string? _voicePreviewHint;
    private bool _voicePreviewStale;
    private long _voiceAudioBust;
    private bool _voiceCloneBusy;
    private bool _voiceRecRecording;
    private string? _voiceCloneHint;
    private string? _voiceCloneError;
    private string? _voiceClonePlayUrl;
    private long _voiceCloneBust;
    private string? _voiceSaveHint;
    private CancellationTokenSource? _voiceSaveCts;

    /// <summary>Kids short script for simple path / children's books; general film otherwise.</summary>
    private string VoiceCloneReadScript =>
        _useKidsScript ? VoiceCloneScripts.KidsShort : VoiceCloneScripts.GeneralFilm;

    private bool _simpleMode;
    private bool _useKidsScript;
    private bool _showKidsFullScript;
    private string? _focusHint;
    private bool _showMediaAudioPicker;
    private bool _loadingMediaAudio;
    private List<ClientMediaFolderService.LocalAudioFile> _mediaAudioFiles = new();

    private int SelectedSeedCount => _seedOrder.Count;

    private bool VoiceJobRunning =>
        _job is not null &&
        string.Equals(_job.Kind, "voice-preview", StringComparison.OrdinalIgnoreCase) &&
        (_job.Status is "running" or "queued") &&
        string.Equals(_job.CharKey, _selectedKey, StringComparison.OrdinalIgnoreCase);


    private bool _extractingCast;
    /// <summary>True when extract started with an existing cast (rebuild vs first build).</summary>
    private bool _rebuildCastHadExisting;

    private bool JobRunning =>
        string.Equals(_job?.Status, "running", StringComparison.OrdinalIgnoreCase);

    private bool PlateSortRunning =>
        JobRunning &&
        string.Equals(_job?.Kind, "character-plates", StringComparison.OrdinalIgnoreCase);

    private bool HasCast => _chars is { Count: > 0 };

    /// <summary>Operator-facing cast: hide group/chorus seeds (too abstract for average users).</summary>
    private IEnumerable<CharacterSummary> CharactersForUi =>
        _chars?.Where(c => !c.IsGroup) ?? Enumerable.Empty<CharacterSummary>();

    private int OperatorCastCount => CharactersForUi.Count();

    /// <summary>Every cast member has look (if needed) + voice — next is shot plan or scenes.</summary>
    private bool IsCastComplete =>
        OperatorCastCount > 0 &&
        CharactersForUi.All(c =>
            HasVoiceProfile(c) &&
            (c.VoiceOnly || c.HasPreferred || c.Locked));

    /// <summary>Show primary Build cast only when there is no cast yet.</summary>
    private bool NeedsCastBuild =>
        _chars is not null && OperatorCastCount == 0 && !_extractingCast;

    /// <summary>Book picture matching is now automated on cast extract.</summary>
    private bool NeedsFindCharacters => false;

    /// <summary>
    /// Single user step: closed cast + book-aware looks (description / visual_lock) for portraits.
    /// </summary>
    private async Task ExtractCastAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectId) || _extractingCast) return;
        _rebuildCastHadExisting = _chars is { Count: > 0 };
        _extractingCast = true;
        _busy = true;
        _error = null;
        _message = null; // progress card owns in-progress UI (not the green success alert)
        // Drop selection so we don't re-open a character that may disappear after rebuild.
        _selectedKey = null;
        _selected = null;
        StateHasChanged();
        try
        {
            var result = await Engine.ExtractCastFromScreenplayAsync(_projectId, force: true);
            if (result is null || result.Ok != true)
            {
                _error = result?.Error ?? "Could not build cast.";
                if (Session.IsAdmin && !string.IsNullOrWhiteSpace(result?.RawPath))
                    _error += $" (admin raw dump: {result.RawPath})";
                _message = null;
                _lastCastExtractKeys = null;
            }
            else
            {
                _lastCastExtractKeys = result.Characters?
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .ToList();
                // Operators get a short outcome; full key dump lives in the admin panel only.
                var n = result.CharacterCount > 0
                    ? result.CharacterCount
                    : _lastCastExtractKeys?.Count ?? 0;
                _message = !string.IsNullOrWhiteSpace(result.Message)
                    ? StripTrailingKeyDump(result.Message!)
                    : $"Cast ready · {n} character(s) — review looks, then lock portraits";
                await LoadAsync();
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _message = null;
        }
        finally
        {
            _extractingCast = false;
            _rebuildCastHadExisting = false;
            _busy = false;
            StateHasChanged();
        }
    }

    protected override async Task OnInitializedAsync()
    {
        await ActiveProject.EnsureLoadedAsync(Engine);
        Hub.JobUpdated += OnJobUpdated;
        Hub.JobLog += OnJobLog;
        try
        {
            try { await Session.EnsureHydratedAsync(); } catch { /* optional */ }

            var projs = await Engine.GetProjectsAsync();
            _projectIds = projs?.Projects.Select(p => p.Id ?? "").Where(s => s.Length > 0).ToList()
                          ?? new List<string>();
            if (projs?.Active?.Id is { Length: > 0 } aid &&
                _projectIds.Exists(id => string.Equals(id, aid, StringComparison.OrdinalIgnoreCase)))
            {
                _projectId = aid;
                ActiveProject.Set(aid, projs.Active?.Label ?? projs.Active?.Title ?? aid);
            }
            else if (_projectIds.Count > 0)
                _projectId = _projectIds[0];
            else
                _projectId = "";

            await ActiveProject.RefreshReadinessAsync(Engine);
            _gateChecked = true;
            if (!string.IsNullOrEmpty(_projectId))
                await Caps.RefreshAsync(Engine);

            ApplySimpleModeFromUri();
            // Easy-start lives entirely on /simple-voice (story + record). No cast list.
            if (_simpleMode)
            {
                Nav.NavigateTo("simple-voice");
                return;
            }


            if (string.IsNullOrEmpty(_projectId) || !ActiveProject.CanCharacters)
                return;

            try { await Hub.StartAsync(); } catch { /* optional */ }

            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;

            await LoadAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _gateChecked = true;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Optional: could wire JS keyboard; L/R handled via buttons for now
        await Task.CompletedTask;
    }

    private static string FriendlyCharacterJobStatus(JobSnapshot job)
    {
        var kind = job.Kind ?? "";
        if (string.Equals(kind, "character-plates", StringComparison.OrdinalIgnoreCase))
            return job.Total > 0
                ? $"Matching book pictures… ({job.Index} of {job.Total})"
                : "Matching book pictures…";
        if (string.Equals(kind, "character", StringComparison.OrdinalIgnoreCase))
            return "Creating portrait…";
        if (string.Equals(kind, "voice-preview", StringComparison.OrdinalIgnoreCase))
            return "Generating voice sample…";
        return "Working…";
    }

    private void OnJobUpdated(JobSnapshot snap)
    {
        // New job id → always take the snapshot (Index may be 0)
        // Same job → update as usual
        _job = snap;
        if ((snap.Status is "done" or "error" or "cancelled") &&
            string.Equals(snap.Kind, "voice-preview", StringComparison.OrdinalIgnoreCase))
        {
            _ = InvokeAsync(async () =>
            {
                _voicePreviewBusy = false;
                if (snap.Status == "done" &&
                    string.Equals(snap.CharKey, _selectedKey, StringComparison.OrdinalIgnoreCase))
                {
                    _error = null;
                    _voicePreviewError = null;
                    _voiceAudioBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _voicePreviewUrl = Engine.CharacterVoiceAudioUrl(
                        _projectId, snap.CharKey!, _voiceAudioBust);
                    _voicePreviewStale = false;
                    _voicePreviewHint = "Film voice sample ready.";
                    _message = null;
                }
                else if (snap.Status == "error")
                {
                    _message = null;
                    _voicePreviewError = Session.IsAdmin
                        ? (snap.Error ?? snap.Message ?? "Voice sample failed.")
                        : "Could not generate voice sample. Try again.";
                }
                else if (snap.Status == "cancelled")
                {
                    _voicePreviewError = null;
                    _voicePreviewHint = "Voice sample cancelled.";
                }
                StateHasChanged();
                await Task.CompletedTask;
            });
        }
        else if ((snap.Status is "done" or "error" or "cancelled") &&
            string.Equals(snap.Kind, "character-plates", StringComparison.OrdinalIgnoreCase))
        {
            _ = InvokeAsync(async () =>
            {
                await SoftReloadAsync();
                if (snap.Status == "done")
                {
                    _error = null;
                    _message = "Book pictures matched.";
                }
                else if (snap.Status == "error")
                {
                    _message = null;
                    _error = Session.IsAdmin
                        ? (snap.Error ?? snap.Message ?? "Could not match book pictures.")
                        : "Could not match book pictures. Try again.";
                }
                else if (snap.Status == "cancelled")
                {
                    _error = null;
                    _message = "Matching cancelled.";
                }
                StateHasChanged();
            });
        }
        else if ((snap.Status is "done" or "error" or "cancelled") &&
            string.Equals(snap.Kind, "character", StringComparison.OrdinalIgnoreCase))
        {
            _ = InvokeAsync(async () =>
            {
                // Leave "Generating…" as soon as the job finishes (even if files need a moment)
                if (snap.Status is "done" or "error" or "cancelled")
                {
                    if (_mode == Mode.WaitingGenerate)
                        _mode = Mode.PickSource;
                }

                await SoftReloadAsync();
                if (snap.Status == "done" &&
                    string.Equals(snap.CharKey, _selectedKey, StringComparison.OrdinalIgnoreCase))
                {
                    _error = null;
                    _message = null;
                    // Brief delay so variant files are visible after write/flush
                    await Task.Delay(150);
                    await SoftReloadAsync();
                    BeginCompareFromVariants();
                }
                else if (snap.Status == "error")
                {
                    _message = null;
                    _error = Session.IsAdmin
                        ? (snap.Error ?? snap.Message ?? "Portrait generation failed.")
                        : "Portrait generation failed. Try again.";
                    _mode = Mode.PickSource;
                }
                else if (snap.Status == "cancelled")
                {
                    _mode = Mode.PickSource;
                }
                StateHasChanged();
            });
        }
        else
        {
            _ = InvokeAsync(StateHasChanged);
        }
    }

    private void OnJobLog(string line)
    {
        if (_job is not null)
        {
            _job.Message = line;
            if (_job.Log.Count == 0 || _job.Log[^1] != line)
            {
                _job.Log.Add(line);
                if (_job.Log.Count > 80)
                    _job.Log = _job.Log.TakeLast(80).ToList();
            }
        }
        _ = InvokeAsync(StateHasChanged);
    }

    private async Task OnProjectChangedAsync()
    {
        ResetCompare();
        _mode = Mode.PickSource;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _busy = true;
        _error = null;
        try
        {
            var dto = await Engine.GetCharactersAsync(_projectId);
            _chars = dto?.Characters ?? new List<CharacterSummary>();
            _plates = dto?.CharacterPlates;
            _imageSeedLimits = dto?.ImageSeedLimits;
            _imgBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // Do not auto-open a character — user clicks the list to choose source.
            if (_selectedKey is not null && CharactersForUi.Any(c => c.Key == _selectedKey))
                await SelectCoreAsync(_selectedKey, resetMode: false, flushPending: false);
            else
            {
                _selectedKey = null;
                _selected = null;
                ResetCompare();
                _mode = Mode.PickSource;
            }

            FocusNarratorIfNeeded();

            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _chars = null;
            _plates = null;
            _imageSeedLimits = null;
        }
        finally { _busy = false; }
    }

    private async Task SoftReloadAsync()
    {
        try
        {
            var dto = await Engine.GetCharactersAsync(_projectId);
            _chars = dto?.Characters ?? new List<CharacterSummary>();
            _plates = dto?.CharacterPlates;
            _imageSeedLimits = dto?.ImageSeedLimits;
            _imgBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_selectedKey is not null)
                await SelectCoreAsync(_selectedKey, resetMode: false, flushPending: false);
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Server messages sometimes append " · Character_X, Character_Y…". Drop that list
    /// from the green banner (admin panel still shows full keys).
    /// </summary>
    private static string StripTrailingKeyDump(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return message;
        var idx = message.IndexOf(" · Character_", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) return message[..idx].TrimEnd();
        idx = message.IndexOf(" · Character_", StringComparison.Ordinal);
        if (idx > 0) return message[..idx].TrimEnd();
        // Also " — Character_Foo, Character_Bar"
        idx = message.IndexOf(" — Character_", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) return message[..idx].TrimEnd();
        return message.Trim();
    }

    /// <summary>
    /// Block cast-list switches while save/generate/etc. runs so async completion
    /// cannot paste one character's look into another's editors.
    /// </summary>
    private bool CastListLocked => _busy || _savingLook || JobRunning || _extractingCast;

    private Task SelectAsync(string key) => SelectCoreAsync(key, resetMode: true, flushPending: true);

    /// <summary>
    /// Switch cast member. When the operator leaves a character with a pending chosen look
    /// (or mid-compare selection), we lock it first so pictures do not vanish on switch.
    /// </summary>
    private async Task SelectCoreAsync(string key, bool resetMode, bool flushPending)
    {
        var switched = !string.Equals(_selectedKey, key, StringComparison.OrdinalIgnoreCase);
        // SoftReload re-selects the same key while _busy — allow that.
        if (switched && CastListLocked && !flushPending)
            return;

        if (switched && flushPending && _selected is not null && _pendingLockCandidate is not null)
        {
            try
            {
                await LockCandidateAsync(_pendingLockCandidate);
            }
            catch
            {
                // LockCandidateAsync already sets _error
            }
        }

        // Block switch only while another action still runs after flush.
        if (switched && CastListLocked)
            return;

        _selectedKey = key;
        _selected = CharactersForUi.FirstOrDefault(c =>
            string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
        _pendingLockCandidate = null;
        _chosenCandidateKey = null;
        if (_selected is not null)
        {
            _editDescription = _selected.Description ?? "";
            _editVisualLock = _selected.VisualLock ?? "";
            _savedLookDescription = _editDescription;
            _savedLookVisualLock = _editVisualLock;
            _lookSaveHint = null;
            _lookSaveCts?.Cancel();
            _lookSaveCts?.Dispose();
            _lookSaveCts = null;
            _editVoiceLabel = _selected.VoiceLabel ?? "";
            _editVoiceProfile = _selected.VoiceProfile ?? "";
            _forceShowVoice = false;
            RefreshVoiceClonePlayUrl();
            _voiceCloneHint = null;
            _voiceCloneError = null;
            _voicePreviewUrl = null;
            _voicePreviewError = null;
            _voicePreviewHint = null;
            _voicePreviewStale = false;
            _ = InvokeAsync(() => TryLoadCachedVoiceAsync());
        }
        if (switched)
        {
            _deleteConfirm = null;
            ApplyPanelsForSelected();
        }
        if (resetMode)
        {
            ResetCompare();
            _mode = Mode.PickSource;
            if (switched)
                _pictureRoute = PictureRoute.Choose;
            _error = null;
            _message = null;
            ResetSeedSelection();
            ApplyPanelsForSelected();
        }
    }

    private static string CandidateKey(Candidate c) => $"{c.Kind}:{c.Index}";

    /// <summary>
    /// Look & voice live on one card — keep it open so neither section is buried.
    /// </summary>
    private void ApplyPanelsForSelected()
    {
        // Single card for picture + voice; always expanded when a character is selected.
        _panelPictureOpen = true;
    }


    private void ResetSeedSelection()
    {
        _seedOrder.Clear();
        if (_selected is null) return;
        // Book plates first (identity from the book), then preferred lock, then gen options.
        // Old order put preferred+variants first and often filled the 3-ref cap before any book pic.
        AddBookRefsToSeedOrder();
        if (_selected.HasPreferred && _seedOrder.Count < ApiMaxSeedRefs)
            _seedOrder.Add("p");
        foreach (var v in _selected.Variants.Where(x => x.Exists).OrderBy(x => x.Index))
        {
            if (_seedOrder.Count >= ApiMaxSeedRefs) break;
            var key = $"v{v.Index ?? 0}";
            if (!_seedOrder.Contains(key, StringComparer.OrdinalIgnoreCase))
                _seedOrder.Add(key);
        }
    }

    /// <summary>After the operator picks book pictures, those become the generate seeds.</summary>
    private void PreferBookRefsAsSeeds()
    {
        _seedOrder.Clear();
        if (_selected is null) return;
        AddBookRefsToSeedOrder();
        // Keep preferred only if there is room — never let old variants crowd out book plates
        if (_selected.HasPreferred && _seedOrder.Count < ApiMaxSeedRefs)
            _seedOrder.Add("p");
    }

    private void AddBookRefsToSeedOrder()
    {
        if (_selected is null) return;
        foreach (var b in _selected.BookRefs.Where(x => x.Exists).OrderBy(x => x.Index ?? 0))
        {
            if (_seedOrder.Count >= ApiMaxSeedRefs) break;
            if (b.Index is not int i) continue;
            var key = $"b{i}";
            if (!_seedOrder.Contains(key, StringComparer.OrdinalIgnoreCase))
                _seedOrder.Add(key);
        }
    }

    private int SeedRank(string key)
    {
        var i = _seedOrder.FindIndex(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? 0 : i + 1;
    }

    private void ToggleSeedKey(string key)
    {
        var i = _seedOrder.FindIndex(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
            _seedOrder.RemoveAt(i);
        else
            _seedOrder.Add(key);
    }

    private void RequestDeleteImage(string kind, int index)
    {
        _deleteConfirm = new PendingDelete { Kind = kind, Index = index };
        _error = null;
        _message = null;
    }

    private void CancelDeleteImage() => _deleteConfirm = null;

    private async Task ConfirmDeleteImageAsync()
    {
        if (_selected is null || _deleteConfirm is null) return;
        _busy = true;
        _error = null;
        try
        {
            await Engine.DeleteCharacterImageAsync(
                _projectId, _selected.Key, _deleteConfirm.Kind, _deleteConfirm.Index);
            _deleteConfirm = null;
            await SoftReloadAsync();
            ResetSeedSelection();
            _message = "Picture deleted.";
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private static bool IsWeakBookPlate(string? fileName)
    {
        var n = (fileName ?? "").ToLowerInvariant();
        return n.Contains("sampled") || n.Contains("text_page") || n.Contains("ocr");
    }

    private static string BookPlateKindLabel(string? fileName)
    {
        var n = (fileName ?? "").ToLowerInvariant();
        if (n.Contains("cover")) return "cover";
        if (n.Contains("sparse")) return "art";
        if (n.Contains("sampled")) return "text?";
        if (n.Contains("embedded")) return "embed";
        if (n.Contains("bookref")) return "page";
        return "page";
    }

    /// <summary>Book-guided path only when the project has a PDF or page images.</summary>
    private bool CanUseBookPictures =>
        ActiveProject.Status?.Book is { } book
        && (book.PdfExists || book.PageImageCount > 0);

    private void ChoosePictureRoute(PictureRoute route)
    {
        _pictureRoute = route;
        _error = null;
        if (route == PictureRoute.Book)
            _ = ToggleBookCandidateGalleryAsync();
        if (route == PictureRoute.Choose)
            _showBookCandidateGallery = false;
        StateHasChanged();
    }

    /// <summary>Book path: ensure selected plates are seeds, then generate 3 looks.</summary>
    private async Task StartBookGuidedGenerateAsync()
    {
        if (_selected is null) return;
        if (_selectedBookCandidatePaths.Count > 0)
        {
            var ok = await EnsureGalleryBookSelectionAppliedAsync();
            if (!ok) return;
        }
        if (!_selected.BookRefs.Any(b => b.Exists) && SelectedSeedCount == 0)
        {
            _error = "Select at least one book picture first.";
            return;
        }
        _seedOrder.Clear();
        AddBookRefsToSeedOrder();
        await StartRegenerateAsync();
    }

    private void BackToSource()
    {
        CloseLookZoom();
        ResetCompare();
        _mode = Mode.PickSource;
        if (_selected?.HasPreferred == true)
            _pictureRoute = PictureRoute.Choose;
    }

    private void ResetCompare()
    {
        _allCandidates = new();
        CloseLookZoom();
    }

    private async Task StartRegenerateAsync()
    {
        if (_selected is null) return;

        // Gallery checkmarks are the intended seeds — do not require a separate "Use for generation"
        // click, and do not mix in preferred/variants the operator did not rank as tiles.
        if (_selectedBookCandidatePaths.Count > 0)
        {
            var prepared = await EnsureGalleryBookSelectionAppliedAsync();
            if (!prepared)
                return;
        }

        if (SelectedSeedCount == 0 && string.IsNullOrWhiteSpace(_editDescription))
        {
            _error = "Select book pictures (or another reference) or enter a description.";
            return;
        }

        var maxSend = ApiMaxSeedRefs;
        var sendOrder = _seedOrder.Take(maxSend).ToList();
        var includePref = sendOrder.Any(k => k is "p");
        var variants = new List<int>();
        var books = new List<int>();
        foreach (var k in sendOrder)
        {
            if (k.Length >= 2 && k[0] == 'v' && int.TryParse(k[1..], out var vi))
                variants.Add(vi);
            if (k.Length >= 2 && k[0] == 'b' && int.TryParse(k[1..], out var bi))
                books.Add(bi);
        }

        // Always 3 options so the pick grid is useful on first and later generates
        // (engine otherwise uses 1 when the character is already locked).
        await StartGenerateCoreAsync(new StartCharacterVariantsRequest
        {
            ProjectId = _projectId,
            CharKey = _selected.Key,
            Count = 3,
            SeedMode = SelectedSeedCount == 0 ? "none" : "explicit",
            IncludePreferred = includePref,
            IncludeLockedRef = includePref,
            BookRefIndices = books,
            VariantIndices = variants,
            SeedOrderKeys = sendOrder,
            MaxRefs = maxSend,
            DescriptionOverride = _editDescription,
            VisualLockOverride = _editVisualLock,
            PersistDescription = true,
        });
    }

    /// <summary>
    /// Persist gallery checkmarks as book refs and set generate seed order to those plates only.
    /// </summary>
    private async Task<bool> EnsureGalleryBookSelectionAppliedAsync()
    {
        if (_selected is null || _selectedBookCandidatePaths.Count == 0)
            return true;

        _savingBookRefs = true;
        _error = null;
        StateHasChanged();
        try
        {
            var paths = _selectedBookCandidatePaths.Take(ApiMaxSeedRefs).ToList();
            var ok = await Engine.SetCharacterBookRefsAsync(_projectId, _selected.Key, paths);
            if (!ok)
            {
                _error = "Could not save the selected book pictures for generation.";
                return false;
            }

            await SoftReloadAsync();
            // ONLY the checked book plates — not preferred, not previous options
            _seedOrder.Clear();
            AddBookRefsToSeedOrder();
            if (_seedOrder.Count == 0)
            {
                _error = "Book pictures were saved but could not be loaded as references. Try again.";
                return false;
            }

            _showBookCandidateGallery = false;
            return true;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            return false;
        }
        finally
        {
            _savingBookRefs = false;
            StateHasChanged();
        }
    }

    private async Task StartSortCharacterPlatesAsync(bool useGrok = true)
    {
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            try { await Hub.StartAsync(); } catch (Exception hex) { _error = $"SignalR: {hex.Message}"; }
            await Engine.StartSortCharacterPlatesAsync(_projectId, useGrok: useGrok, maxImages: 32);
            // Progress card owns in-progress UI (one Cancel there) — no green status banner
            _message = null;
            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private async Task StartGenerateCoreAsync(StartCharacterVariantsRequest req)
    {
        if (_selected is null) return;
        _busy = true;
        _error = null;
        _message = null;
        // Reset progress UI immediately so a prior 3/3 bar never carries over
        var total = req.Count > 0 ? req.Count : 3;
        _job = new JobSnapshot
        {
            Status = "queued",
            Kind = "character",
            ProjectId = _projectId,
            CharKey = req.CharKey,
            Message = "Starting…",
            Index = 0,
            Total = total,
            Log = new List<string>(),
            JobId = Guid.NewGuid().ToString("N"), // temporary until server job id arrives
        };
        _mode = Mode.WaitingGenerate;
        StateHasChanged();
        try
        {
            try { await Hub.StartAsync(); } catch (Exception hex) { _error = $"SignalR: {hex.Message}"; }
            await Engine.StartCharacterVariantsAsync(req);
            var jobs = await Engine.GetJobAsync();
            if (jobs?.Job is { } j &&
                (string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase)))
            {
                // Never adopt a finished job's Index/Total for the new run
                if (j.Index > 0 && string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase))
                    j.Index = 0;
                _job = j;
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _mode = Mode.PickSource;
            _job = null;
        }
        finally { _busy = false; }
    }

    private void BeginCompareFromVariants()
    {
        if (_selected is null)
        {
            _mode = Mode.PickSource;
            return;
        }

        var vars = _selected.Variants.Where(v => v.Exists).OrderBy(v => v.Index).ToList();
        if (vars.Count == 0)
        {
            _mode = Mode.PickSource;
            _error = "Generate finished but no pictures found.";
            return;
        }

        _allCandidates = vars.Select(v => new Candidate
        {
            Kind = "variant",
            Index = v.Index ?? 1,
            Label = $"Option {v.Index}",
            // Bust cache so second generate doesn't show stale first-round images
            Url = CacheBust(Engine.CharacterVariantUrl(_projectId, _selected.Key, v.Index ?? 1)),
        }).ToList();

        _mode = Mode.Compare;
        _panelPictureOpen = true;
        _message = null;
    }


    private void OpenLookZoom(Candidate c)
    {
        _zoomCandidate = c;
        _zoomScale = 1;
    }

    private void CloseLookZoom()
    {
        _zoomCandidate = null;
        _zoomScale = 1;
    }

    private void ToggleLookZoomScale()
    {
        _zoomScale = _zoomScale > 1.01 ? 1 : 2;
    }

    private void ZoomPrev()
    {
        if (_zoomCandidate is null || _allCandidates.Count == 0) return;
        var i = _allCandidates.FindIndex(x =>
            string.Equals(x.Url, _zoomCandidate.Url, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Label, _zoomCandidate.Label, StringComparison.Ordinal));
        if (i < 0) i = 0;
        i = (i - 1 + _allCandidates.Count) % _allCandidates.Count;
        _zoomCandidate = _allCandidates[i];
        _zoomScale = 1;
    }

    private void ZoomNext()
    {
        if (_zoomCandidate is null || _allCandidates.Count == 0) return;
        var i = _allCandidates.FindIndex(x =>
            string.Equals(x.Url, _zoomCandidate.Url, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Label, _zoomCandidate.Label, StringComparison.Ordinal));
        if (i < 0) i = 0;
        i = (i + 1) % _allCandidates.Count;
        _zoomCandidate = _allCandidates[i];
        _zoomScale = 1;
    }

    private async Task LockFromZoomAsync()
    {
        if (_zoomCandidate is null) return;
        var c = _zoomCandidate;
        CloseLookZoom();
        await LockCandidateAsync(c);
    }

    private async Task LockCandidateAsync(Candidate c, bool overrideStyle = false, string? overrideReason = null)
    {
        if (_selected is null) return;
        // Remember choice so a cast-list switch can finish the save if this call is in flight.
        _pendingLockCandidate = c;
        _chosenCandidateKey = CandidateKey(c);
        var charKey = _selected.Key;
        var display = _selected.DisplayName;
        _busy = true;
        _error = null;
        if (overrideStyle) { _styleRejectCandidate = null; _styleRejectMessage = null; }
        try
        {
            if (c.Kind == "variant")
                await Engine.LockCharacterVariantAsync(_projectId, charKey, c.Index, overrideStyle, overrideReason);
            else if (c.Kind == "book")
                await Engine.LockCharacterBookRefAsync(_projectId, charKey, c.Index, overrideStyle, overrideReason);
            else
                throw new InvalidOperationException($"Cannot lock look kind '{c.Kind}'.");
            _styleRejectCandidate = null;
            _styleRejectMessage = null;

            // Saved thumbnail/icon is the confirmation — no redundant "Saved look" banner.
            _pendingLockCandidate = null;
            _chosenCandidateKey = null;
            _imgBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await SoftReloadAsync();
            await RefreshNavReadinessAsync();
            // Stay on this character; show the preferred look (do not wipe list state for others).
            ResetCompare();
            _mode = Mode.PickSource;
            _pictureRoute = PictureRoute.Choose;
            ApplyPanelsForSelected();
            ResetSeedSelection();
            if (_selected is not null)
            {
                foreach (var v in _selected.Variants.Where(x => x.Exists))
                {
                    var key = $"v{v.Index ?? 0}";
                    if (!_seedOrder.Contains(key, StringComparer.OrdinalIgnoreCase))
                        _seedOrder.Add(key);
                }
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            // Style-gate rejection is overridable: the creator can lock any look regardless of the
            // project's default medium (photoreal character in an animated film, or vice versa).
            if (!overrideStyle && IsStyleGateRejection(ex.Message))
            {
                _styleRejectCandidate = c;
                _styleRejectMessage = ex.Message;
            }
            // Keep _pendingLockCandidate so switching cast can retry / flush once.
        }
        finally { _busy = false; }
    }

    private Candidate? _styleRejectCandidate;
    private string? _styleRejectMessage;

    /// <summary>Keep the classifier's verdict (close the override prompt without locking).</summary>
    private void DismissStyleReject()
    {
        _styleRejectCandidate = null;
        _styleRejectMessage = null;
        _error = null;
        _pendingLockCandidate = null;
        _chosenCandidateKey = null;
    }

    private static bool IsStyleGateRejection(string? message)
    {
        var m = message ?? "";
        return m.Contains("does not match the project style", StringComparison.OrdinalIgnoreCase)
            || m.Contains("live-action", StringComparison.OrdinalIgnoreCase)
            || m.Contains("could not read the portrait", StringComparison.OrdinalIgnoreCase)
            || m.Contains("style check", StringComparison.OrdinalIgnoreCase);
    }

    private void OnLookDescriptionInput(ChangeEventArgs e)
    {
        _editDescription = e.Value?.ToString() ?? "";
        ScheduleAutoSaveLook();
    }

    private void OnLookVisualLockInput(ChangeEventArgs e)
    {
        _editVisualLock = e.Value?.ToString() ?? "";
        ScheduleAutoSaveLook();
    }

    /// <summary>
    /// Debounced autosave: wait until typing pauses (~800ms) so we do not hit the API on every keystroke.
    /// Same pattern as voice profile autosave on this card.
    /// </summary>
    private void ScheduleAutoSaveLook()
    {
        _lookSaveCts?.Cancel();
        _lookSaveCts?.Dispose();
        _lookSaveCts = new CancellationTokenSource();
        var token = _lookSaveCts.Token;
        _lookSaveHint = "Pending…";
        _ = AutoSaveLookDebouncedAsync(token);
    }

    private async Task AutoSaveLookDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(LookAutosaveDebounceMs, token);
            if (token.IsCancellationRequested || _selected is null) return;
            _lookSaveHint = "Saving…";
            await InvokeAsync(StateHasChanged);
            await SaveLookAsync(silent: true);
            if (!token.IsCancellationRequested)
            {
                _lookSaveHint = "Saved";
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (TaskCanceledException) { /* typing continued — new debounce wins */ }
        catch (Exception ex)
        {
            _lookSaveHint = "Save failed";
            _error = ex.Message;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <param name="silent">Autosave: no full-page busy, no toast spam; skip AI scrub (cheap disk write).</param>
    private async Task SaveLookAsync(bool silent = false)
    {
        if (_selected is null) return;

        // Snapshot identity + text — never re-read _selected after await for the POST.
        var charKey = _selected.Key;
        var displayName = _selected.DisplayName;

        // No text change → no API
        var desc = _editDescription ?? "";
        var vis = _editVisualLock ?? "";
        if (string.Equals(desc, _savedLookDescription, StringComparison.Ordinal) &&
            string.Equals(vis, _savedLookVisualLock, StringComparison.Ordinal))
        {
            if (!silent)
            {
                _error = null;
                _message = "No look changes.";
            }
            return;
        }

        if (!silent)
        {
            _busy = true;
            _error = null;
            _message = null;
        }
        _savingLook = true;
        try
        {
            // Autosave: no Grok scrub (cost + latency every pause). Explicit saves / generate can scrub.
            var result = await Engine.UpdateCharacterLookAsync(
                _projectId,
                charKey,
                description: desc,
                visualLock: vis,
                scrubWithAi: !silent);

            var stillOnChar = string.Equals(_selectedKey, charKey, StringComparison.OrdinalIgnoreCase);
            if (stillOnChar && !silent)
            {
                if (!string.IsNullOrWhiteSpace(result.Description))
                    _editDescription = result.Description!;
                if (result.VisualLock is not null)
                    _editVisualLock = result.VisualLock;
            }

            // Saved thumbnail/icon is the confirmation — no redundant "Saved look" banner.

            // Soft reload on silent is fine but keep editors stable if scrub didn't rewrite.
            await SoftReloadAsync();
            if (stillOnChar &&
                string.Equals(_selectedKey, charKey, StringComparison.OrdinalIgnoreCase) &&
                _selected is not null)
            {
                if (!silent && !string.IsNullOrWhiteSpace(result.Description))
                    _editDescription = result.Description!;
                else if (silent)
                {
                    // Keep what the operator typed; mark as saved baseline
                }
                else
                    _editDescription = _selected.Description ?? _editDescription ?? "";

                if (!silent && result.VisualLock is not null)
                    _editVisualLock = result.VisualLock;
                else if (!silent)
                    _editVisualLock = _selected.VisualLock ?? _editVisualLock ?? "";

                _savedLookDescription = _editDescription ?? "";
                _savedLookVisualLock = _editVisualLock ?? "";
            }
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                _error = ex.Message;
                _message = null;
            }
            else throw;
        }
        finally
        {
            if (!silent) _busy = false;
            _savingLook = false;
        }
    }

    private async Task SaveVoiceAsync(bool silent = false)
    {
        if (_selected is null) return;
        if (!silent)
        {
            _busy = true;
            _error = null;
        }
        try
        {
            await Engine.UpdateCharacterVoiceAsync(
                _projectId,
                _selected.Key,
                voiceProfile: _editVoiceProfile,
                voiceLabel: _editVoiceLabel);
            if (!silent)
                _message = $"Saved voice for {_selected.DisplayName}";
            await SoftReloadAsync();
            if (_selected is not null)
            {
                _editVoiceLabel = _selected.VoiceLabel ?? "";
                _editVoiceProfile = _selected.VoiceProfile ?? "";
            }
            try { await ActiveProject.RefreshReadinessAsync(Engine); } catch { /* nav */ }
            if (IsCastComplete && !silent)
                _message = null;
        }
        catch (Exception ex)
        {
            if (!silent) _error = ex.Message;
            else throw;
        }
        finally
        {
            if (!silent) _busy = false;
        }
    }

    private void OnVoiceLabelInput(ChangeEventArgs e)
    {
        _editVoiceLabel = e.Value?.ToString() ?? "";
        MarkVoiceStaleIfPlaying();
        ScheduleAutoSaveVoice();
    }

    private void OnVoiceProfileInput(ChangeEventArgs e)
    {
        _editVoiceProfile = e.Value?.ToString() ?? "";
        MarkVoiceStaleIfPlaying();
        ScheduleAutoSaveVoice();
    }

    private void ScheduleAutoSaveVoice()
    {
        _voiceSaveCts?.Cancel();
        _voiceSaveCts?.Dispose();
        _lookSaveCts?.Cancel();
        _lookSaveCts?.Dispose();
        _voiceSaveCts = new CancellationTokenSource();
        var token = _voiceSaveCts.Token;
        _ = AutoSaveVoiceDebouncedAsync(token);
    }

    private async Task AutoSaveVoiceDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(700, token);
            if (token.IsCancellationRequested || _selected is null) return;
            _voiceSaveHint = "Saving…";
            await InvokeAsync(StateHasChanged);
            await SaveVoiceAsync(silent: true);
            if (!token.IsCancellationRequested)
            {
                _voiceSaveHint = "Saved";
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (TaskCanceledException) { }
        catch (Exception ex)
        {
            _voiceSaveHint = "Save failed";
            _error = ex.Message;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void MarkVoiceStaleIfPlaying()
    {
        if (!string.IsNullOrEmpty(_voicePreviewUrl))
            _voicePreviewStale = true;
    }

    private async Task TryLoadCachedVoiceAsync()
    {
        if (_selected is null) return;
        try
        {
            var st = await Engine.GetVoicePreviewStatusAsync(
                _projectId,
                _selected.Key,
                voiceProfile: _editVoiceProfile,
                voiceLabel: _editVoiceLabel);
            if (st is { Exists: true, Matches: true })
            {
                _voiceAudioBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _voicePreviewUrl = Engine.CharacterVoiceAudioUrl(
                    _projectId, _selected.Key, _voiceAudioBust);
                _voicePreviewStale = false;
                _voicePreviewHint = "Cached film voice sample.";
            }
            else if (st is { Exists: true, Matches: false })
            {
                _voicePreviewStale = true;
                _voicePreviewHint = "Saved sample is out of date — Regenerate after edits.";
            }
            StateHasChanged();
        }
        catch { /* optional */ }
    }

    /// <param name="force">true = always regenerate (after editing profile).</param>
    private async Task PlayVoicePreviewAsync(bool force)
    {
        if (_selected is null) return;
        _voicePreviewError = null;
        _voicePreviewHint = null;
        StateHasChanged();

        try
        {
            if (!force)
            {
                var st = await Engine.GetVoicePreviewStatusAsync(
                    _projectId,
                    _selected.Key,
                    voiceProfile: _editVoiceProfile,
                    voiceLabel: _editVoiceLabel);
                if (st is { Exists: true, Matches: true })
                {
                    _voiceAudioBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _voicePreviewUrl = Engine.CharacterVoiceAudioUrl(
                        _projectId, _selected.Key, _voiceAudioBust);
                    _voicePreviewStale = false;
                    _voicePreviewHint = "Cached film voice sample.";
                    return;
                }
            }

            _voicePreviewBusy = true;
            _voicePreviewUrl = null;
            _voicePreviewHint = force
                ? "Regenerating film voice sample…"
                : "Generating film voice sample (short clip)…";
            StateHasChanged();

            await Engine.StartVoicePreviewAsync(new StartVoicePreviewRequest
            {
                ProjectId = _projectId,
                CharKey = _selected.Key,
                VoiceProfile = _editVoiceProfile,
                VoiceLabel = _editVoiceLabel,
                DisplayName = _selected.DisplayName,
                // force: always regen; cache miss also generates (service skips only matching cache)
                Force = force,
            });
            // Job progress via SignalR; keep busy until done handler clears it
            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;
        }
        catch (Exception ex)
        {
            _voicePreviewError = ex.Message;
            _voicePreviewBusy = false;
        }
    }

    private async Task RefreshNavReadinessAsync()
    {
        try { await ActiveProject.RefreshReadinessAsync(Engine); }
        catch { /* nav gates */ }
    }

    private async Task UnlockAsync()
    {
        if (_selected is null) return;
        _busy = true;
        _error = null;
        try
        {
            await Engine.UnlockCharacterAsync(_projectId, _selected.Key);
            _message = $"Unlocked {_selected.DisplayName}";
            await LoadAsync();
            ResetCompare();
            _mode = Mode.PickSource;
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }



    private void ApplySimpleModeFromUri()
    {
        var querySimple = false;
        try
        {
            var uri = Nav.ToAbsoluteUri(Nav.Uri);
            var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            querySimple = q.TryGetValue("simple", out var sVals) &&
                          sVals.Any(v => v is "1" or "true" or "yes");
            if (q.TryGetValue("focus", out var fVals))
                _focusHint = fVals.FirstOrDefault();
        }
        catch
        {
            /* ignore */
        }

        // project.json studioPath is source of truth; ?simple=1 still works as override entry
        _simpleMode = ActiveProject.IsSimpleVoice || querySimple;

        _useKidsScript = _simpleMode ||
            VoiceCloneScripts.LooksLikeChildrensStory(
                ActiveProject.Label,
                genre: null,
                projectId: _projectId);
    }

    private async Task ExitSimplePathAsync()
    {
        if (string.IsNullOrEmpty(_projectId)) return;
        try
        {
            await Engine.SetStudioPathAsync(_projectId, ProjectStudioPaths.Full);
            ActiveProject.Set(ActiveProject.ProjectId, ActiveProject.Label, ActiveProject.ParentProjectId, ProjectStudioPaths.Full);
            _simpleMode = false;
            _useKidsScript = VoiceCloneScripts.LooksLikeChildrensStory(
                ActiveProject.Label, null, _projectId);
            Nav.NavigateTo("characters", forceLoad: false);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private void FocusNarratorIfNeeded()
    {
        if (_chars is null || _chars.Count == 0) return;
        var wantNarrator = _simpleMode
            || string.Equals(_focusHint, "narrator", StringComparison.OrdinalIgnoreCase);
        if (!wantNarrator) return;

        var ui = CharactersForUi.ToList();
        if (ui.Count == 0) return;
        var pick = ui.FirstOrDefault(c =>
                       (c.Key?.Contains("narrator", StringComparison.OrdinalIgnoreCase) ?? false)
                       || (c.DisplayName?.Contains("narrator", StringComparison.OrdinalIgnoreCase) ?? false))
                   ?? ui.FirstOrDefault(c => c.VoiceOnly)
                   ?? ui[0];
        if (pick is null) return;
        if (!string.Equals(_selectedKey, pick.Key, StringComparison.OrdinalIgnoreCase))
            _ = SelectAsync(pick.Key);
    }

    private void RefreshVoiceClonePlayUrl()
    {
        if (_selected?.HasVoiceCloneSample == true && !string.IsNullOrEmpty(_projectId) && !string.IsNullOrEmpty(_selectedKey))
        {
            _voiceCloneBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _voiceClonePlayUrl = Engine.CharacterVoiceCloneSampleUrl(_projectId, _selectedKey, _voiceCloneBust);
        }
        else
            _voiceClonePlayUrl = null;
    }


    /// <summary>
    /// Simple path: pick Instant voice clone model so apply-ready status is clear once the key is set.
    /// Does not block recording if the key is still missing.
    /// </summary>

    private async Task ApplyVoiceCloneToProviderAsync()
    {
        if (_selected is null || string.IsNullOrEmpty(_selectedKey) || string.IsNullOrEmpty(_projectId))
            return;
        _voiceCloneBusy = true;
        _voiceCloneError = null;
        _voiceCloneHint = "Applying voice…";
        try
        {
            var result = await Engine.ApplyVoiceCloneAsync(_projectId, _selectedKey);
            if (!result.Ok)
            {
                _voiceCloneError = result.Error ?? "Apply failed";
                return;
            }
            _voiceCloneHint = result.Message
                ?? (result.UsedMock
                    ? "Demo voice applied. Preview saved."
                    : $"Voice applied ({result.ProviderId ?? "provider"}) — id saved on this character.");
            await LoadAsync();
            if (!string.IsNullOrEmpty(_selectedKey))
                await SelectCoreAsync(_selectedKey, resetMode: false, flushPending: false);
        }
        catch (Exception ex)
        {
            _voiceCloneError = ex.Message;
        }
        finally
        {
            _voiceCloneBusy = false;
        }
    }

    private async Task EnsureSimpleVoiceModelAsync()
    {
        try
        {
            var cfgDto = await Engine.GetConfigAsync(_projectId);
            var map = cfgDto?.Config;
            string voice = "none";
            if (map is not null && map.TryGetValue("voice_model_name", out var el)
                && el.ValueKind == System.Text.Json.JsonValueKind.String)
                voice = el.GetString() ?? "none";

            if (!string.IsNullOrWhiteSpace(voice)
                && !voice.Equals("none", StringComparison.OrdinalIgnoreCase)
                && !voice.Equals("disabled", StringComparison.OrdinalIgnoreCase))
            {
                await Caps.RefreshAsync(Engine);
                return;
            }

            // First clone-step model from the catalog only (never invent an id).
            var defaultClone = SupportedModelCatalog.FirstEnabledVoiceCloneModelId();
            if (string.IsNullOrWhiteSpace(defaultClone))
                return;
            await Engine.SaveConfigAsync(_projectId, new Dictionary<string, object?>
            {
                ["voice_model_name"] = defaultClone,
            });
            await Caps.RefreshAsync(Engine);
        }
        catch
        {
            // Non-fatal — recording still works without a selected model
        }
    }

    private async Task StartVoiceCloneMicAsync()
    {
        _voiceCloneError = null;
        _voiceCloneHint = null;
        try
        {
            // Prefer already-authorized media folder; never open a picker just to start recording.
            if (!MediaFolder.IsConnected)
                await MediaFolder.TryReconnectAsync();

            var result = await Js.InvokeAsync<VoiceCaptureStartResult>("PageToMovieVoiceCapture.start");
            if (result is null || !result.Ok)
            {
                _voiceCloneError = string.IsNullOrWhiteSpace(result?.Error)
                    ? "Could not access the microphone. Check browser permissions and try again."
                    : result!.Error;
                return;
            }
            _voiceRecRecording = true;
            _voiceCloneHint = "Recording — read the script, then tap Done.";
            await InvokeAsync(StateHasChanged);
        }
        catch (JSException jex)
        {
            _voiceCloneError = "Microphone failed: " + jex.Message;
            _voiceRecRecording = false;
        }
        catch (Exception ex)
        {
            _voiceCloneError = ex.Message;
            _voiceRecRecording = false;
        }
    }

    private async Task StopVoiceCloneMicAsync()
    {
        if (_selected is null || string.IsNullOrEmpty(_selectedKey)) return;
        _voiceCloneBusy = true;
        _voiceCloneError = null;
        _voiceCloneHint = "Saving…";
        StateHasChanged();
        try
        {
            var result = await Js.InvokeAsync<VoiceCaptureStopResult>("PageToMovieVoiceCapture.stop");
            _voiceRecRecording = false;
            if (result is null || !result.Ok || string.IsNullOrEmpty(result.Base64))
            {
                _voiceCloneError = result?.Error ?? "No audio captured";
                return;
            }
            var raw = Convert.FromBase64String(result.Base64);
            var name = string.IsNullOrWhiteSpace(result.FileName) ? "voice_clone_sample.webm" : result.FileName!;
            await PersistVoiceCloneSampleAsync(raw, name);
        }
        catch (Exception ex)
        {
            _voiceCloneError = ex.Message;
            _voiceRecRecording = false;
        }
        finally
        {
            _voiceCloneBusy = false;
            StateHasChanged();
        }
    }

    private async Task CancelVoiceCloneMicAsync()
    {
        try { await Js.InvokeVoidAsync("PageToMovieVoiceCapture.cancel"); } catch { }
        _voiceRecRecording = false;
        _voiceCloneHint = "Recording cancelled.";
    }

    private async Task OpenMediaFolderAudioPickerAsync()
    {
        _voiceCloneError = null;
        _showMediaAudioPicker = true;
        _loadingMediaAudio = true;
        StateHasChanged();
        try
        {
            if (!MediaFolder.IsConnected)
            {
                var ok = await MediaFolder.ConnectFolderAsync();
                if (!ok)
                {
                    _voiceCloneError = MediaFolder.LastStatus ?? "Connect a folder first to pick existing audio.";
                    _showMediaAudioPicker = false;
                    return;
                }
            }
            var files = await MediaFolder.ListAudioFilesAsync(_projectId);
            // Prefer project-scoped files; if empty, list whole folder
            if (files.Count == 0)
                files = await MediaFolder.ListAudioFilesAsync(null);
            _mediaAudioFiles = files.ToList();
            _voiceCloneHint = _mediaAudioFiles.Count > 0
                ? $"Found {_mediaAudioFiles.Count} file(s) — choose one."
                : "No audio files found yet.";
        }
        catch (Exception ex)
        {
            _voiceCloneError = ex.Message;
        }
        finally
        {
            _loadingMediaAudio = false;
            StateHasChanged();
        }
    }

    private async Task PickMediaFolderAudioAsync(ClientMediaFolderService.LocalAudioFile file)
    {
        if (_selected is null || string.IsNullOrEmpty(_selectedKey)) return;
        _voiceCloneBusy = true;
        _voiceCloneError = null;
        _voiceCloneHint = $"Loading {file.Name}…";
        StateHasChanged();
        try
        {
            var bytes = await MediaFolder.ReadLocalBytesAsync(file.RelativePath);
            if (bytes is null || bytes.Length == 0)
            {
                _voiceCloneError = "Could not read that file.";
                return;
            }
            await PersistVoiceCloneSampleAsync(bytes, file.Name);
            _showMediaAudioPicker = false;
        }
        catch (Exception ex)
        {
            _voiceCloneError = ex.Message;
        }
        finally
        {
            _voiceCloneBusy = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Write clone sample to the client media folder (source of truth for large media) and
    /// mirror metadata/bytes to the server so previews still work.
    /// </summary>
    private async Task PersistVoiceCloneSampleAsync(byte[] bytes, string fileName)
    {
        if (_selected is null || string.IsNullOrEmpty(_selectedKey)) return;
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is not (".webm" or ".mp3" or ".wav" or ".m4a" or ".ogg" or ".aac" or ".mp4"))
            ext = ".webm";
        if (ext == ".mp4") ext = ".webm";
        var safeKey = PageToMovie.Core.Utils.FileNameSanitizer.SanitizeFileName(_selectedKey ?? "character");
        var rel = $"assets/characters/{safeKey}/voice_clone_sample{ext}";

        // Client media folder when already connected (no folder picker mid-recording).
        // Always mirror to the server so previews still work.
        if (!MediaFolder.IsConnected)
            await MediaFolder.TryReconnectAsync();

        await MediaFolder.SaveBytesAsync(
            _projectId, rel, bytes, promptToConnectFolder: false);
        _voiceCloneHint = "Saving…";

        await using var ms = new MemoryStream(bytes);
        if (string.IsNullOrWhiteSpace(_selectedKey))
            throw new InvalidOperationException("No character selected for voice sample.");
        await Engine.UploadVoiceCloneSampleAsync(_projectId, _selectedKey!, ms, "voice_clone_sample" + ext);
        await SoftReloadAsync();
        RefreshVoiceClonePlayUrl();
        _voiceCloneBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        RefreshVoiceClonePlayUrl();
        _voiceCloneHint = "Sample saved.";
    }

    private async Task OnVoiceCloneUploadAsync(InputFileChangeEventArgs e)
    {
        // Legacy OS file picker path — prefer media folder; still supported if invoked.
        if (_selected is null || _selected.VoiceOnly || _selected.IsGroup) return;
        var file = e.File;
        if (file is null) return;
        _voiceCloneBusy = true;
        _voiceCloneError = null;
        try
        {
            const long max = 15 * 1024 * 1024;
            await using var stream = file.OpenReadStream(max);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            await PersistVoiceCloneSampleAsync(ms.ToArray(), file.Name);
        }
        catch (Exception ex) { _voiceCloneError = ex.Message; }
        finally { _voiceCloneBusy = false; }
    }

    private async Task DeleteVoiceCloneSampleAsync()
    {
        if (string.IsNullOrEmpty(_projectId) || string.IsNullOrEmpty(_selectedKey)) return;
        _voiceCloneBusy = true;
        try
        {
            await Engine.DeleteVoiceCloneSampleAsync(_projectId, _selectedKey);
            _voiceCloneHint = "Sample removed.";
            _voiceClonePlayUrl = null;
            await ReloadSelectedCharacterAsync();
        }
        catch (Exception ex) { _voiceCloneError = ex.Message; }
        finally { _voiceCloneBusy = false; }
    }

    private async Task ReloadSelectedCharacterAsync()
    {
        if (string.IsNullOrEmpty(_projectId)) return;
        try
        {
            var dto = await Engine.GetCharactersAsync(_projectId);
            _chars = dto?.Characters ?? new List<CharacterSummary>();
            _selected = CharactersForUi.FirstOrDefault(c =>
                string.Equals(c.Key, _selectedKey, StringComparison.OrdinalIgnoreCase));
            if (_selected is not null)
            {
                _editVoiceLabel = _selected.VoiceLabel ?? "";
                _editVoiceProfile = _selected.VoiceProfile ?? "";
                RefreshVoiceClonePlayUrl();
            }
        }
        catch { }
    }

    private sealed class VoiceCaptureStartResult
    {
        public bool Ok { get; set; }
        public string? MimeType { get; set; }
        public string? Error { get; set; }
    }

    private sealed class VoiceCaptureStopResult
    {
        public bool Ok { get; set; }
        public string? MimeType { get; set; }
        public string? Base64 { get; set; }
        public string? FileName { get; set; }
        public string? Error { get; set; }
        public long ByteLength { get; set; }
    }

    private async Task OnUploadRefAsync(InputFileChangeEventArgs e)
    {
        if (_selected is null || _selected.VoiceOnly || _selected.IsGroup) return;
        var file = e.File;
        if (file is null) return;

        // Capture identity before any re-render; buffer bytes while InputFile is still mounted.
        var charKey = _selected.Key;
        var display = _selected.DisplayName;
        var fileName = file.Name;
        byte[] bytes;
        try
        {
            const long max = 25 * 1024 * 1024;
            await using var stream = file.OpenReadStream(max);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            bytes = ms.ToArray();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            return;
        }

        if (bytes.Length < 64)
        {
            _error = "That image is empty or too small.";
            return;
        }

        _busy = true;
        _error = null;
        _message = null;
        try
        {
            await using var stream = new MemoryStream(bytes, writable: false);
            await Engine.UploadCharacterRefAsync(_projectId, charKey, stream, fileName);
            // Saved thumbnail/icon is the confirmation — no redundant "Saved look" banner.
            _imgBust = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await SoftReloadAsync();
            try { await ActiveProject.RefreshReadinessAsync(Engine); } catch { /* nav */ }
            ResetCompare();
            _mode = Mode.PickSource;
            _pictureRoute = PictureRoute.Choose;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task CancelAsync()
    {
        _busy = true;
        try
        {
            await Engine.CancelJobAsync();
            _message = "Cancel requested";
            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job;
            if (_mode == Mode.WaitingGenerate)
                _mode = Mode.PickSource;
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private string CacheBust(string url) =>
        url + (url.Contains('?') ? "&" : "?") + "v=" + _imgBust;

    private static string Trunc(string? s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "—";
        return s.Length <= n ? s : s[..(n - 1)] + "…";
    }

    public async ValueTask DisposeAsync()
    {
        _voiceSaveCts?.Cancel();
        _voiceSaveCts?.Dispose();
        _voiceSaveCts = null;
        _lookSaveCts?.Cancel();
        _lookSaveCts?.Dispose();
        _lookSaveCts = null;
        await DisposeAsyncCore();
    }

    private async ValueTask DisposeAsyncCore()

    {
        Hub.JobUpdated -= OnJobUpdated;
        Hub.JobLog -= OnJobLog;
        await Task.CompletedTask;
    }
}

