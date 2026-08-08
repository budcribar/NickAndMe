using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdaptationScreenplay
{
    public override string StepKey => "screenplay";

    private const string EditorId = "screenplay-main";

    private string _text = "";
    private string _loadedText = "";
    private bool _dirtyLocal;
    private bool _editorReady;
    private bool _jsInitStarted;
    private DateTime? _lastSavedUtc;
    private ScreenplayStatus? _screenplayStatus;
    private int _saveGeneration;
    private int _sceneCount;
    private List<string> _signOffWarnings = new();
    private CancellationTokenSource? _saveCts;
    private DotNetObjectReference<AdaptationScreenplay>? _dotNetRef;
    private BookContextDto? _bookContext;
    private bool _showBookModal;
    private ElementReference _bookWindowEl;
    private bool _bookWindowNeedsDragBind;
    private bool _bookLoading;
    private int _bookRequestGen;

    private ElementReference _editorHost;
    private ElementReference _previewEl;
    private ElementReference _scenesEl;

    private bool CanEdit => _editorReady && !Busy && !JobRunning;

    private bool _copied;

    /// <summary>Copy the full screenplay to the clipboard — read action, works even while read-only.</summary>
    private async Task CopyScreenplayAsync()
    {
        string text;
        try { text = await Js.InvokeAsync<string>("fountainEditor.getValue", EditorId); }
        catch { text = _text; }
        if (string.IsNullOrEmpty(text)) text = _text;

        try
        {
            await Js.InvokeVoidAsync("PageToMovieExport.copyTextAsync", text ?? "");
            _copied = true;
            StateHasChanged();
            await Task.Delay(1500);
            _copied = false;
            StateHasChanged();
        }
        catch { /* clipboard may be blocked (permissions / non-secure context) — non-fatal */ }
    }

    /// <summary>User has approved at least once (signed hash recorded).</summary>
    private bool WasEverSigned =>
        _screenplayStatus?.Signed == true
        || !string.IsNullOrWhiteSpace(_screenplayStatus?.SignedHash);

    /// <summary>Approved and not edited since sign-off.</summary>
    private bool IsApprovedClean =>
        _screenplayStatus?.Signed == true
        && _screenplayStatus.Dirty != true
        && !_dirtyLocal;

    /// <summary>Had an approval but draft changed (or local unsaved edits after sign-off).</summary>
    private bool NeedsReapprove =>
        WasEverSigned
        && (_screenplayStatus?.Dirty == true || _dirtyLocal)
        && !string.IsNullOrWhiteSpace(_text);


    private string StatusTitle
    {
        get
        {
            if (_screenplayStatus?.Title is { Length: > 0 } t)
                return t;
            return string.IsNullOrWhiteSpace(_text) ? "" : "Untitled";
        }
    }

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        await LoadEditorDataAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_bookWindowNeedsDragBind && _showBookModal)
        {
            _bookWindowNeedsDragBind = false;
            try
            {
                await Js.InvokeVoidAsync("fountainEditor.bindBookWindowDrag", _bookWindowEl);
            }
            catch { /* optional */ }
        }

        if (_jsInitStarted || string.IsNullOrEmpty(ProjectId)) return;
        // Wait until we have loaded draft at least once
        if (!_editorDataLoaded) return;

        _jsInitStarted = true;
        try
        {
            _dotNetRef = DotNetObjectReference.Create(this);
            await Js.InvokeVoidAsync(
                "fountainEditor.init",
                EditorId,
                _editorHost,
                _text,
                _dotNetRef,
                _previewEl,
                _scenesEl,
                Busy || JobRunning);
            _editorReady = true;
            if (!Busy && !JobRunning)
                await Js.InvokeVoidAsync("fountainEditor.setReadOnly", EditorId, false);
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Error = "Could not start the editor: " + ex.Message;
            _jsInitStarted = false;
        }
    }

    private bool _editorDataLoaded;

    private async Task LoadEditorDataAsync()
    {
        try
        {
            var doc = await Engine.GetScreenplayAsync(ProjectId);
            _text = doc?.Text ?? "";
            _loadedText = _text;
            _dirtyLocal = false;
            _screenplayStatus = doc?.Screenplay ?? Status?.Screenplay;
            _sceneCount = _screenplayStatus?.SceneHeadingCount ?? 0;
            if (doc?.Adaptation is not null)
                Status = doc.Adaptation;
            _editorDataLoaded = true;
            UpdateWarningsFromText(_text);

            if (_editorReady)
            {
                await Js.InvokeVoidAsync("fountainEditor.setValue", EditorId, _text);
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _editorDataLoaded = true; // still try to init empty editor
        }
    }

    public override async Task LoadAsync()
    {
        await base.LoadAsync();
        await LoadEditorDataAsync();
        if (_editorReady)
        {
            await Js.InvokeVoidAsync("fountainEditor.setValue", EditorId, _text);
            await Js.InvokeVoidAsync("fountainEditor.refresh", EditorId);
            if (!Busy && !JobRunning)
                await Js.InvokeVoidAsync("fountainEditor.setReadOnly", EditorId, false);
        }
        else
        {
            _jsInitStarted = false; // allow re-init after project switch
            StateHasChanged();
        }
    }

    [JSInvokable]
    public Task OnEditorChanged(string text, string[] warnings, int sceneCount)
    {
        _text = text ?? "";
        _sceneCount = sceneCount;
        _dirtyLocal = !string.Equals(_text, _loadedText, StringComparison.Ordinal);
        MapWarnings(warnings);
        ScheduleAutosave();
        return InvokeAsync(StateHasChanged);
    }

    /// <param name="openBookModal">True when user clicked Book link (popup); false = jump only.</param>
    [JSInvokable]
    public async Task OnSceneSelected(int line, int sceneIndex, string heading, bool openBookModal = false)
    {
        if (!openBookModal)
            return; // Scene click only navigates in the editor (handled in JS)

        _showBookModal = true;
        _bookWindowNeedsDragBind = true;
        _bookLoading = true;
        await InvokeAsync(StateHasChanged);

        var gen = ++_bookRequestGen;
        try
        {
            if (_editorReady)
            {
                try
                {
                    _text = await Js.InvokeAsync<string>("fountainEditor.getValue", EditorId) ?? _text;
                }
                catch { /* keep */ }
            }

            var ctx = await Engine.GetBookContextAsync(
                ProjectId,
                sceneIndex: Math.Max(1, sceneIndex),
                line: line,
                heading: heading,
                fountainText: _text);
            if (gen != _bookRequestGen) return;
            _bookContext = ctx;
        }
        catch (Exception ex)
        {
            if (gen == _bookRequestGen)
            {
                _bookContext = new BookContextDto
                {
                    Ok = false,
                    HasBook = Status?.Book.BookTextExists == true,
                    Heading = heading,
                    SceneIndex = sceneIndex,
                    Excerpt = "",
                    Message = ex.Message,
                };
            }
        }
        finally
        {
            if (gen == _bookRequestGen)
            {
                _bookLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private void CloseBookModal()
    {
        _showBookModal = false;
    }

    private void MapWarnings(string[]? codes)
    {
        _signOffWarnings = new List<string>();
        if (codes is null) return;
        foreach (var c in codes)
        {
            switch (c)
            {
                case "empty":
                    _signOffWarnings.Add("The screenplay is empty.");
                    break;
                case "no_scenes":
                    _signOffWarnings.Add("No scene headings found — add lines like INT. ROOM - DAY.");
                    break;
                case "very_short":
                    _signOffWarnings.Add("This draft is very short.");
                    break;
            }
        }
    }

    private void UpdateWarningsFromText(string text)
    {
        var codes = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) codes.Add("empty");
        else
        {
            if (text.Trim().Length < 40) codes.Add("very_short");
            // crude scene check until JS reports
            if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"(?im)^(INT|EXT|EST|I/E|\.)"))
                codes.Add("no_scenes");
        }
        MapWarnings(codes.ToArray());
    }

    private void ScheduleAutosave()
    {
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = new CancellationTokenSource();
        var ct = _saveCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(900, ct);
                await InvokeAsync(async () =>
                {
                    if (_dirtyLocal && !Busy && !ct.IsCancellationRequested)
                        await SaveDraftAsync(manual: false);
                });
            }
            catch (OperationCanceledException) { /* expected */ }
        });
    }

    private async Task InsertAsync(string snippet)
    {
        if (!_editorReady) return;
        try
        {
            await Js.InvokeVoidAsync("fountainEditor.insertAtCursor", EditorId, snippet);
            _text = await Js.InvokeAsync<string>("fountainEditor.getValue", EditorId);
            _dirtyLocal = !string.Equals(_text, _loadedText, StringComparison.Ordinal);
            ScheduleAutosave();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private async Task SyncTextFromEditorAsync()
    {
        if (!_editorReady) return;
        try
        {
            _text = await Js.InvokeAsync<string>("fountainEditor.getValue", EditorId) ?? _text;
        }
        catch { /* keep local */ }
    }

    private async Task SaveDraftAsync(bool manual)
    {
        await SyncTextFromEditorAsync();
        if (string.IsNullOrEmpty(_text) && !_dirtyLocal && !manual)
            return;

        var gen = ++_saveGeneration;
        try
        {
            if (manual) Busy = true;
            var result = await Engine.SaveScreenplayAsync(ProjectId, _text);
            if (gen != _saveGeneration) return;
            _loadedText = _text;
            _dirtyLocal = false;
            _lastSavedUtc = DateTime.UtcNow;
            _screenplayStatus = result?.Screenplay ?? _screenplayStatus;
            if (result?.Adaptation is not null)
                Status = result.Adaptation;
            if (manual)
                Message = result?.Message ?? "Draft saved";
        }
        catch (Exception ex)
        {
            if (manual || gen == _saveGeneration)
                Error = ex.Message;
        }
        finally
        {
            if (manual) Busy = false;
            StateHasChanged();
        }
    }

    private async Task SignOffAsync()
    {
        await SyncTextFromEditorAsync();

        // Soft gate: confirm if warnings
        if (string.IsNullOrWhiteSpace(_text))
        {
            Error = "Add some screenplay text before approving.";
            return;
        }

        try
        {
            var warnings = await Js.InvokeAsync<string[]>("fountainEditor.getWarnings", EditorId);
            MapWarnings(warnings);
        }
        catch { /* use local */ }

        if (_signOffWarnings.Count > 0)
        {
            // Still allow, but set message so user sees why
            Message = "Approving with notes: " + string.Join(" ", _signOffWarnings);
        }

        Busy = true;
        BusyMessage = "Saving…";
        Error = null;
        // Paint progress before the long sign-off call (includes cast build on the server)
        await InvokeAsync(StateHasChanged);

        // Continue always persists first (merged Save + approve).
        if (_dirtyLocal)
        {
            try { await SaveDraftAsync(manual: false); }
            catch { /* sign-off still sends full text */ }
            Busy = true;
            BusyMessage = "Approving…";
            await InvokeAsync(StateHasChanged);
        }
        else
        {
            BusyMessage = "Approving…";
        }

        try
        {
            if (_editorReady)
                await Js.InvokeVoidAsync("fountainEditor.setReadOnly", EditorId, true);

            // Server may spend many seconds extracting cast after approve
            BusyMessage = "Approving and preparing cast…";
            await InvokeAsync(StateHasChanged);

            var result = await Engine.SignOffScreenplayAsync(ProjectId, _text);
            _loadedText = _text;
            _dirtyLocal = false;
            _lastSavedUtc = DateTime.UtcNow;
            _screenplayStatus = result?.Screenplay;
            if (result?.Adaptation is not null)
                Status = result.Adaptation;
            else
                await SoftLoadAsync();
            Message = result?.Message ?? "Screenplay approved";
            if (result?.Ok == true)
            {
                BusyMessage = "Opening cast…";
                await InvokeAsync(StateHasChanged);
                Nav.NavigateTo("characters");
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Busy = false;
            BusyMessage = null;
            if (_editorReady)
            {
                try { await Js.InvokeVoidAsync("fountainEditor.setReadOnly", EditorId, false); }
                catch { /* ignore */ }
            }
        }
    }

    private async Task CreateFromBookAsync()
    {
        if (_dirtyLocal && !string.IsNullOrWhiteSpace(_text))
        {
            try { await SaveDraftAsync(manual: false); } catch { /* ignore */ }
        }

        Busy = true;
        BusyMessage = "Writing screenplay…";
        Error = null;
        Message = null;
        ProgressIndex = 0;
        ProgressTotal = 10;
        await InvokeAsync(StateHasChanged);
        try
        {
            await EnsureHubAsync();
            await Engine.StartBookImportAsync(
                ProjectId,
                skipPrepare: true,
                forceExtract: false,
                forceVision: false,
                autoVision: false,
                model: Model);
            var jobs0 = await Engine.GetJobAsync();
            Job = jobs0?.Job;
            AbsorbProgressFromSnapshot(Job ?? new JobSnapshot());
            // Shared poller re-renders soft progress while the long draft call is quiet.
            StartJobPolling();
            // Wait until draft job finishes (poller keeps Job + bar live)
            for (var i = 0; i < 3600; i++)
            {
                var snap = Job;
                if (snap is not null)
                {
                    BusyMessage = AdaptationPageBase.OperatorJobRunningMessage(snap);
                    await InvokeAsync(StateHasChanged);
                    var st = snap.Status ?? "";
                    if (st is "error" or "cancelled")
                    {
                        Error = snap.Error ?? snap.Message ?? "Could not create draft. Try again.";
                        return;
                    }
                    if (st == "done" &&
                        (string.IsNullOrEmpty(snap.Kind) ||
                         snap.Kind is "book_import" or "book_prepare" or "stage1"))
                        break;
                }
                await Task.Delay(1000);
            }

            Message = "Draft created from book";
            await SoftLoadAsync();
            await LoadEditorDataAsync();
            if (_editorReady)
                await Js.InvokeVoidAsync("fountainEditor.setValue", EditorId, _text);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Busy = false;
            BusyMessage = null;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = null;
        try
        {
            if (_jsInitStarted)
                await Js.InvokeVoidAsync("fountainEditor.dispose", EditorId);
        }
        catch { /* ignore */ }
        _dotNetRef?.Dispose();
        await base.DisposeAsync();
    }
    private async Task OnFilmLengthChangedAsync()
    {
        try { await SoftLoadAsync(); } catch { /* ignore */ }
    }

}

