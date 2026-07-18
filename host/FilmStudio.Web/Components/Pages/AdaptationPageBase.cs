using FilmStudio.Core.Models;
using FilmStudio.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace FilmStudio.Web.Components.Pages;

/// <summary>Shared project / job / status logic for Adaptation step pages.</summary>
public abstract class AdaptationPageBase : ComponentBase, IAsyncDisposable
{
    [Inject] protected EngineApiClient Engine { get; set; } = null!;
    [Inject] protected JobHubClient Hub { get; set; } = null!;
    [Inject] protected NavigationManager Nav { get; set; } = null!;

    public bool Busy;
    public string? Error;
    public string? Message;
    public string ProjectId = "Buster";
    public List<string> ProjectIds = new();
    public AdaptationStatus? Status;
    public JobSnapshot? Job;
    public IBrowserFile? PendingFile;

    public int TotalMinutes = 15;
    public int ChunkPages = 10;
    public string Model = "grok-4.5";
    public bool Resume;
    public string S2Resolution = "720p";

    private CancellationTokenSource? _pollCts;
    public int ProgressIndex;
    public int ProgressTotal;

    public bool JobRunning =>
        string.Equals(Job?.Status, "running", StringComparison.OrdinalIgnoreCase);

    /// <summary>import | screenplay | shots</summary>
    public abstract string StepKey { get; }

    public bool CanRunOutline =>
        Status is not null &&
        (Status.Book.ReadyForStage1 ||
         (Status.Stage1.Present &&
          Status.Stage1.SceneCount > 0 &&
          Status.Book.BookTextExists));

    protected override async Task OnInitializedAsync()
    {
        Hub.JobUpdated += OnJobUpdated;
        Hub.JobLog += OnJobLog;
        try
        {
            var projs = await Engine.GetProjectsAsync();
            ProjectIds = projs?.Projects.Select(p => p.Id ?? "").Where(s => s.Length > 0).ToList()
                         ?? new List<string> { "Buster" };
            if (projs?.Active?.Id is { Length: > 0 } aid)
                ProjectId = aid;
            else if (ProjectIds.Count > 0)
                ProjectId = ProjectIds[0];

            try { await Hub.StartAsync(); } catch { /* optional */ }

            var jobs = await Engine.GetJobAsync();
            Job = jobs?.Job;

            await LoadAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private void OnJobUpdated(JobSnapshot snap)
    {
        Job = snap;
        AbsorbProgressFromSnapshot(snap);
        AbsorbProgressFromLine(snap.Message);
        if (snap.Status is "done" or "error" or "cancelled")
        {
            _pollCts?.Cancel();
            if (snap.Status == "done" && ProgressTotal > 0)
                ProgressIndex = ProgressTotal;
            _ = InvokeAsync(async () =>
            {
                await SoftLoadAsync();
                if (snap.Status == "done")
                    Message = snap.Message ?? "Job finished";
                else if (snap.Status == "error")
                    Error = snap.Error ?? snap.Message ?? "Job failed";
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
        if (Job is null)
        {
            Job = new JobSnapshot
            {
                Status = "running",
                Message = line,
                Log = new List<string> { line },
            };
        }
        else
        {
            Job.Message = line;
            if (Job.Log.Count == 0 || Job.Log[^1] != line)
            {
                Job.Log.Add(line);
                if (Job.Log.Count > 120)
                    Job.Log = Job.Log.TakeLast(120).ToList();
            }
        }
        AbsorbProgressFromLine(line);
        if (Job is not null && ProgressTotal > 0)
        {
            Job.Index = Math.Max(Job.Index, ProgressIndex);
            Job.Total = Math.Max(Job.Total, ProgressTotal);
        }
        _ = InvokeAsync(StateHasChanged);
    }

    protected void AbsorbProgressFromSnapshot(JobSnapshot snap)
    {
        if (snap.Total > 0)
            ProgressTotal = Math.Max(ProgressTotal, snap.Total);
        if (snap.Index > 0)
            ProgressIndex = Math.Max(ProgressIndex, snap.Index);
    }

    protected void AbsorbProgressFromLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        var mChunks = System.Text.RegularExpressions.Regex.Match(
            line, @"Stage 1:\s+(\d+)\s+book chunk", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (mChunks.Success && int.TryParse(mChunks.Groups[1].Value, out var n) && n > 0)
        {
            ProgressTotal = Math.Max(ProgressTotal, n);
            return;
        }

        var mChunk = System.Text.RegularExpressions.Regex.Match(
            line, @"chunk\s+(\d+)\s*/\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (mChunk.Success &&
            int.TryParse(mChunk.Groups[1].Value, out var cIdx) &&
            int.TryParse(mChunk.Groups[2].Value, out var cTot) &&
            cTot > 0)
        {
            ProgressTotal = Math.Max(ProgressTotal, cTot);
            var done = line.Contains("done", StringComparison.OrdinalIgnoreCase);
            var completed = done ? cIdx : Math.Max(0, cIdx - 1);
            ProgressIndex = Math.Max(ProgressIndex, completed);
            return;
        }

        var mVis = System.Text.RegularExpressions.Regex.Match(
            line, @"(?:Grok vision|Reading page|page)\s+(\d+)\s*/\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (mVis.Success &&
            int.TryParse(mVis.Groups[1].Value, out var vIdx) &&
            int.TryParse(mVis.Groups[2].Value, out var vTot) &&
            vTot > 0)
        {
            ProgressTotal = Math.Max(ProgressTotal, vTot);
            ProgressIndex = Math.Max(ProgressIndex, Math.Max(0, vIdx - 1));
        }
    }

    public static bool IsJobInFlightMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        return message.Contains("waiting", StringComparison.OrdinalIgnoreCase)
               || message.Contains("calling", StringComparison.OrdinalIgnoreCase)
               || message.Contains("parsing", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Grok vision", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Reading page", StringComparison.OrdinalIgnoreCase);
    }

    public virtual async Task LoadAsync()
    {
        Busy = true;
        Error = null;
        try
        {
            var dto = await Engine.GetAdaptationAsync(ProjectId);
            Status = dto?.Adaptation;
            ApplyDefaultsFromStatus();
            var jobs = await Engine.GetJobAsync();
            Job = jobs?.Job;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = null;
        }
        finally { Busy = false; }
    }

    public async Task SoftLoadAsync()
    {
        try
        {
            var dto = await Engine.GetAdaptationAsync(ProjectId);
            Status = dto?.Adaptation;
            ApplyDefaultsFromStatus();
        }
        catch { /* ignore */ }
    }

    protected void ApplyDefaultsFromStatus()
    {
        if (Status?.Book.SuggestedTotalMinutes is int m && m > 0)
            TotalMinutes = Math.Clamp(m, 3, 180);
        if (Status?.Book.SuggestedChunkPages is int c && c > 0)
            ChunkPages = Math.Clamp(c, 5, 30);
    }

    public void OnFileSelected(InputFileChangeEventArgs e)
    {
        PendingFile = e.File;
        Message = $"Selected {e.File.Name} ({e.File.Size:N0} bytes)";
    }

    public async Task UploadAsync()
    {
        if (PendingFile is null) return;
        Busy = true;
        Error = null;
        try
        {
            const long max = 80 * 1024 * 1024;
            await using var stream = PendingFile.OpenReadStream(max);
            await Engine.UploadBookAsync(ProjectId, PendingFile.Name, stream);
            Message = $"Saved {PendingFile.Name}";
            PendingFile = null;
            await LoadAsync();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    public async Task PrepareBookAsync(bool forceVision)
    {
        Busy = true;
        Error = null;
        Message = null;
        try
        {
            await EnsureHubAsync();
            await Engine.StartBookPrepareAsync(
                ProjectId,
                forceExtract: true,
                forceVision: forceVision,
                autoVision: true);
            Message = forceVision
                ? "Re-reading book pages… watch the log below"
                : "Preparing book… watch the log below";
            var jobs = await Engine.GetJobAsync();
            Job = jobs?.Job;
            StartJobPolling();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    public async Task RunOutlineAsync()
    {
        Busy = true;
        Error = null;
        Message = null;
        try
        {
            await EnsureHubAsync();
            ProgressIndex = 0;
            ProgressTotal = 0;
            await Engine.StartStage1Async(new StartStage1Request
            {
                ProjectId = ProjectId,
                ChunkPages = ChunkPages,
                TotalMinutes = TotalMinutes,
                Model = Model,
                Resume = Resume,
            });
            Message = "Building screenplay… (may take a few minutes) — live log below";
            var jobs = await Engine.GetJobAsync();
            Job = jobs?.Job;
            AbsorbProgressFromSnapshot(Job ?? new JobSnapshot());
            StartJobPolling();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    public async Task RunShotsAsync()
    {
        Busy = true;
        Error = null;
        Message = null;
        try
        {
            await EnsureHubAsync();
            await Engine.StartStage2Async(new StartStage2Request
            {
                ProjectId = ProjectId,
                Resolution = S2Resolution,
                Scenes = "all",
            });
            Message = "Building shot plan… — live log below";
            var jobs = await Engine.GetJobAsync();
            Job = jobs?.Job;
            StartJobPolling();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    protected void StartJobPolling()
    {
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(1500, ct);
                    var jobs = await Engine.GetJobAsync(ct);
                    var snap = jobs?.Job;
                    if (snap is null) continue;
                    await InvokeAsync(() =>
                    {
                        Job = snap;
                        AbsorbProgressFromSnapshot(snap);
                        AbsorbProgressFromLine(snap.Message);
                        if (Job is not null && ProgressTotal > 0)
                        {
                            Job.Index = Math.Max(Job.Index, ProgressIndex);
                            Job.Total = Math.Max(Job.Total, ProgressTotal);
                        }
                        StateHasChanged();
                    });
                    if (snap.Status is "done" or "error" or "cancelled" or "idle")
                    {
                        if (snap.Status is "done" or "error" or "cancelled")
                            await InvokeAsync(async () => { await SoftLoadAsync(); StateHasChanged(); });
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { /* expected */ }
            catch { /* ignore poll errors */ }
        }, CancellationToken.None);
    }

    public async Task CancelAsync()
    {
        Busy = true;
        try
        {
            await Engine.CancelJobAsync();
            Message = "Cancel requested";
            var jobs = await Engine.GetJobAsync();
            Job = jobs?.Job;
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    protected async Task EnsureHubAsync()
    {
        if (Hub.IsConnected) return;
        try { await Hub.StartAsync(); } catch { /* ignore */ }
    }

    public static string NextStepLabel(string step) => step switch
    {
        "import_book" => "Import a screenplay, PDF, or text file",
        "fix_book_text" => "Prepare imported text, or import a screenplay",
        "sign_screenplay" => "Edit the screenplay and approve when ready",
        "run_stage1" => "Build the screenplay from the book",
        "run_stage2" => "Build the shot plan",
        "replan_stage2" => "Update the shot plan (screenplay changed)",
        "generate_clips" => "Open Scenes and create video clips",
        _ => "Looks complete — refine on Scenes or Characters",
    };

    public static string NextStepAlertClass(string step) => step switch
    {
        "generate_clips" or "done" => "alert-success",
        "replan_stage2" or "fix_book_text" or "sign_screenplay" => "alert-warning",
        _ => "alert-info",
    };

    public static string JobKindLabel(string? kind) => kind switch
    {
        "book_prepare" => "book",
        "stage1" => "screenplay",
        "stage2" => "shot plan",
        _ => kind ?? "",
    };

    /// <summary>Suggested path for /adaptation redirect.</summary>
    public static string SuggestedStepPath(AdaptationStatus? status)
    {
        if (status is null) return "/adaptation/import";
        return status.NextStep switch
        {
            "import_book" or "fix_book_text" => "/adaptation/import",
            "sign_screenplay" or "run_stage1" => "/adaptation/screenplay",
            "run_stage2" or "replan_stage2" => "/adaptation/shots",
            "generate_clips" or "done" => "/adaptation/shots",
            _ => "/adaptation/import",
        };
    }

    public virtual async ValueTask DisposeAsync()
    {
        _pollCts?.Cancel();
        Hub.JobUpdated -= OnJobUpdated;
        Hub.JobLog -= OnJobLog;
        await Task.CompletedTask;
    }
}
