using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FilmStudio.Core.Auth;
using FilmStudio.Core.Models;
using FilmStudio.Core.Options;

namespace FilmStudio.Web.Services;

/// <summary>HTTP client for FilmStudio.Api (C# backend).</summary>
public sealed class EngineApiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly AdminSessionService? _session;

    public EngineApiClient(HttpClient http, AdminSessionService? session = null)
    {
        _http = http;
        _session = session;
        SyncIdentityHeaders();
        if (_session is not null)
            _session.Changed += SyncIdentityHeaders;
    }

    /// <summary>Push X-User-Id / Bearer onto the shared HttpClient defaults (circuit-scoped client).</summary>
    public void SyncIdentityHeaders()
    {
        try
        {
            _http.DefaultRequestHeaders.Remove("Authorization");
            _http.DefaultRequestHeaders.Remove(AuthHeaders.UserId);
            if (_session is null) return;
            if (!string.IsNullOrWhiteSpace(_session.Token))
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _session.Token.Trim());
            var uid = string.IsNullOrWhiteSpace(_session.UserId) ? "local" : _session.UserId.Trim();
            _http.DefaultRequestHeaders.TryAddWithoutValidation(AuthHeaders.UserId, uid);
        }
        catch
        {
            // ignore header races
        }
    }

    private void ApplyAuth(HttpRequestMessage req)
    {
        SyncIdentityHeaders();
        if (_session is null) return;
        if (!string.IsNullOrWhiteSpace(_session.Token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.Token.Trim());
        if (!string.IsNullOrWhiteSpace(_session.UserId))
            req.Headers.TryAddWithoutValidation(AuthHeaders.UserId, _session.UserId.Trim());
    }

    private async Task<T?> SendJsonAsync<T>(HttpRequestMessage req, CancellationToken ct)
    {
        ApplyAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? $"{(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
            return default;
        return await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct);
    }

    public async Task<LoginResponse?> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest { Username = username, Password = password }, options: JsonOpts),
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadFromJsonAsync<LoginResponse>(JsonOpts, ct);
        if (body is null)
            return new LoginResponse { Ok = false, Error = "Empty response" };
        if (!resp.IsSuccessStatusCode && body.Ok)
            body.Ok = false;
        return body;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
            ApplyAuth(req);
            await _http.SendAsync(req, ct);
        }
        catch
        {
            // ignore — client clears token either way
        }
        finally
        {
            _session?.Clear();
        }
    }

    public async Task<MeResponse?> GetMeAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        return await SendJsonAsync<MeResponse>(req, ct);
    }

    public async Task<AdminStateDto?> GetAdminStateAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/state");
        return await SendJsonAsync<AdminStateDto>(req, ct);
    }

    public async Task<RuntimeConfigDto?> GetAdminConfigAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/config");
        return await SendJsonAsync<RuntimeConfigDto>(req, ct);
    }

    public async Task<RuntimeConfigDto?> SaveAdminConfigAsync(
        RuntimeConfigUpdateRequest body,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, "/api/admin/config")
        {
            Content = JsonContent.Create(body, options: JsonOpts),
        };
        return await SendJsonAsync<RuntimeConfigDto>(req, ct);
    }

    public async Task AdminCancelJobAsync(string jobId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/jobs/{Uri.EscapeDataString(jobId)}/cancel");
        await SendJsonAsync<object>(req, ct);
    }

    public async Task AdminReleaseLockAsync(string resource, bool force = true, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/locks/release")
        {
            Content = JsonContent.Create(new AdminReleaseLockRequest { Resource = resource, Force = force }, options: JsonOpts),
        };
        await SendJsonAsync<object>(req, ct);
    }

    public async Task<LocksDto?> GetLocksAsync(CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        return await _http.GetFromJsonAsync<LocksDto>("/api/locks", JsonOpts, ct);
    }

    public async Task EnsureHealthyAsync(CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.GetAsync("/health", ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<ProjectsDto?> GetProjectsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<ProjectsDto>("/api/projects", JsonOpts, ct);

    public async Task ActivateProjectAsync(string projectId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/activate",
            new { },
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(err);
        }
    }

    public async Task<ProjectsDto?> CreateProjectAsync(
        string name,
        string? title = null,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            "/api/projects",
            new { name, title },
            JsonOpts,
            ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(body) ?? resp.ReasonPhrase);
        return JsonSerializer.Deserialize<ProjectsDto>(body, JsonOpts);
    }

    public async Task<ProjectsDto?> DeleteProjectAsync(
        string projectId,
        CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}",
            ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(body) ?? resp.ReasonPhrase);
        return JsonSerializer.Deserialize<ProjectsDto>(body, JsonOpts);
    }

    /// <summary>
    /// Primary job for the current user (Phase F: uses list <c>?mine=1</c>, not bare GET).
    /// Prefers a running job, else most recent.
    /// </summary>
    public async Task<JobsDto?> GetJobAsync(CancellationToken ct = default)
    {
        var list = await GetJobsAsync(mine: true, ct: ct);
        if (list is null) return null;
        return new JobsDto
        {
            Ok = list.Ok,
            Running = list.Running,
            Job = JobListHelpers.PickPrimary(list.Jobs),
        };
    }

    /// <summary>Multi-job list. Requires mine, projectId, or userId (Phase F).</summary>
    public async Task<JobsListDto?> GetJobsAsync(
        bool mine = false,
        string? projectId = null,
        string? userId = null,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        var q = new List<string>();
        if (mine) q.Add("mine=1");
        if (!string.IsNullOrWhiteSpace(projectId))
            q.Add("projectId=" + Uri.EscapeDataString(projectId));
        if (!string.IsNullOrWhiteSpace(userId))
            q.Add("userId=" + Uri.EscapeDataString(userId));
        if (q.Count == 0)
            q.Add("mine=1"); // never call bare /api/jobs
        var url = "/api/jobs?" + string.Join("&", q);
        return await _http.GetFromJsonAsync<JobsListDto>(url, JsonOpts, ct);
    }

    public async Task<JobDetailDto?> GetJobByIdAsync(string jobId, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<JobDetailDto>(
            $"/api/jobs/{Uri.EscapeDataString(jobId)}",
            JsonOpts,
            ct);

    public async Task<CapacityDto?> GetCapacityAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<CapacityDto>("/api/capacity", JsonOpts, ct);

    public async Task StartSceneGenAsync(
        string projectId,
        int scene,
        bool onlyMissing = true,
        int? clip = null,
        string? resolution = null,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsJsonAsync(
            "/api/jobs/gen-scene",
            new StartSceneGenRequest
            {
                ProjectId = projectId,
                Scene = scene,
                Clip = clip,
                OnlyMissing = onlyMissing,
                Resolution = resolution,
            },
            JsonOpts,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? $"{(int)resp.StatusCode}");
        }
    }

    public async Task StartBatchGenAsync(
        string projectId,
        IReadOnlyList<int> scenes,
        bool onlyMissing = true,
        string? resolution = null,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsJsonAsync(
            "/api/jobs/gen-batch",
            new StartBatchGenRequest
            {
                ProjectId = projectId,
                Resolution = resolution,
                Scenes = scenes.ToList(),
                OnlyMissing = onlyMissing,
            },
            JsonOpts,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? $"{(int)resp.StatusCode}");
        }
    }

    public async Task CancelJobAsync(CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsJsonAsync("/api/jobs/cancel", new { }, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task CancelJobByIdAsync(string jobId, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsJsonAsync(
            $"/api/jobs/{Uri.EscapeDataString(jobId)}/cancel",
            new { },
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase);
        }
    }

    public async Task<ScenesListDto?> GetScenesAsync(string projectId, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        return await _http.GetFromJsonAsync<ScenesListDto>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes",
            JsonOpts,
            ct);
    }

    public async Task<SceneDetailDto?> GetSceneDetailAsync(
        string projectId,
        int sceneNumber,
        CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<SceneDetailDto>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}",
            JsonOpts,
            ct);

    public string ClipVideoUrl(string projectId, int sceneNumber, int clipNumber)
    {
        // Absolute API URL (same host as WIP) so <video src> never hits the Blazor port by mistake
        return $"{ApiBaseUrl}/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/clips/{clipNumber}/video";
    }

    public string CompositeVideoUrl(string projectId, int sceneNumber)
    {
        return $"{ApiBaseUrl}/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/composite";
    }

    /// <summary>Absolute stream URL for the WIP full movie (range requests enabled).</summary>
    public string WipMovieUrl(string projectId)
    {
        return $"{ApiBaseUrl}/api/projects/{Uri.EscapeDataString(projectId)}/movie/wip";
    }

    public async Task<WipMovieMetaDto?> GetWipMovieMetaAsync(
        string projectId,
        CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<WipMovieMetaDto>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/movie/wip/meta",
            JsonOpts,
            ct);

    public async Task<AdaptationDto?> GetAdaptationAsync(string projectId, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<AdaptationDto>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/adaptation",
            JsonOpts,
            ct);

    public async Task StartStage1Async(StartStage1Request req, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/api/jobs/stage1", req, JsonOpts, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase);
        }
    }

    public async Task StartStage2Async(StartStage2Request req, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/api/jobs/stage2", req, JsonOpts, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase);
        }
    }

    public async Task StartRemuxAsync(
        string projectId,
        int? scene = null,
        bool rebuildWip = true,
        bool refreshStaleScenes = false,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            "/api/jobs/remux",
            new StartRemuxRequest
            {
                ProjectId = projectId,
                Scene = scene,
                RebuildWip = rebuildWip,
                RefreshStaleScenes = refreshStaleScenes,
            },
            JsonOpts,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase);
        }
    }

    public async Task ReviewClipAsync(
        string projectId,
        int scene,
        int clip,
        string status,
        string note = "",
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/clips/review",
            new ClipReviewRequest
            {
                ProjectId = projectId,
                Scene = scene,
                Clip = clip,
                Status = status,
                Note = note,
            },
            JsonOpts,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase);
        }
    }

    public async Task ApproveSceneAsync(
        string projectId,
        int scene,
        string note = "",
        bool remux = false,
        bool rebuildWip = false,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{scene}/approve",
            new SceneApproveRequest
            {
                ProjectId = projectId,
                Scene = scene,
                Note = note,
                Remux = remux,
                RebuildWip = rebuildWip,
            },
            JsonOpts,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase);
        }
    }

    public async Task<EditLogDto?> GetEditLogAsync(string projectId, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<EditLogDto>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/edit-log",
            JsonOpts,
            ct);

    public async Task<ClipReviewsDto?> GetClipReviewsAsync(string projectId, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<ClipReviewsDto>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/clip-reviews",
            JsonOpts,
            ct);

    public async Task UploadBookAsync(
        string projectId,
        string fileName,
        Stream content,
        CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(streamContent, "file", fileName);
        using var resp = await _http.PostAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/adaptation/upload",
            form,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase);
        }
    }

    /// <summary>
    /// Import a Fountain file as the editable screenplay draft (approve on Screenplay).
    /// </summary>
    public async Task<FountainImportDto?> ImportFountainAsync(
        string projectId,
        string fileName,
        Stream content,
        CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(streamContent, "file", fileName);
        using var resp = await _http.PostAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/adaptation/import-fountain",
            form,
            ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(body) ?? resp.ReasonPhrase);
        return JsonSerializer.Deserialize<FountainImportDto>(body, JsonOpts);
    }

    public async Task<ScreenplayDto?> GetScreenplayAsync(
        string projectId,
        CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<ScreenplayDto>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/screenplay",
            JsonOpts,
            ct);

    public async Task<ScreenplaySaveDto?> SaveScreenplayAsync(
        string projectId,
        string text,
        CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/screenplay",
            new { text },
            JsonOpts,
            ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(body) ?? resp.ReasonPhrase);
        return JsonSerializer.Deserialize<ScreenplaySaveDto>(body, JsonOpts);
    }

    public async Task<ScreenplaySignOffDto?> SignOffScreenplayAsync(
        string projectId,
        string? text = null,
        CancellationToken ct = default)
    {
        object payload = text is null ? new { } : new { text };
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/screenplay/sign-off",
            payload,
            JsonOpts,
            ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(body) ?? resp.ReasonPhrase);
        return JsonSerializer.Deserialize<ScreenplaySignOffDto>(body, JsonOpts);
    }

    public async Task<ScreenplaySaveDto?> CreateScreenplayFromBookAsync(
        string projectId,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/screenplay/from-book",
            null,
            ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(body) ?? resp.ReasonPhrase);
        return JsonSerializer.Deserialize<ScreenplaySaveDto>(body, JsonOpts);
    }

    public async Task<BookContextDto?> GetBookContextAsync(
        string projectId,
        int sceneIndex,
        int line = 0,
        string? heading = null,
        string? fountainText = null,
        CancellationToken ct = default)
    {
        var qs = new List<string>
        {
            $"sceneIndex={sceneIndex}",
        };
        if (line > 0) qs.Add($"line={line}");
        if (!string.IsNullOrWhiteSpace(heading))
            qs.Add($"heading={Uri.EscapeDataString(heading)}");
        var url =
            $"/api/projects/{Uri.EscapeDataString(projectId)}/screenplay/book-context?{string.Join("&", qs)}";

        using var resp = await _http.PostAsJsonAsync(
            url,
            new { text = fountainText, heading, sceneIndex, line },
            JsonOpts,
            ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(raw) ?? resp.ReasonPhrase);
        return JsonSerializer.Deserialize<BookContextDto>(raw, JsonOpts);
    }

    public async Task<CostDto?> GetCostAsync(
        string projectId,
        string? draftResolution = null,
        string? heroResolution = null,
        double? assumeAvgRetries = null,
        CancellationToken ct = default)
    {
        var q = new List<string>();
        if (!string.IsNullOrWhiteSpace(draftResolution))
            q.Add($"draftResolution={Uri.EscapeDataString(draftResolution)}");
        if (!string.IsNullOrWhiteSpace(heroResolution))
            q.Add($"heroResolution={Uri.EscapeDataString(heroResolution)}");
        if (assumeAvgRetries is double r)
            q.Add($"assumeAvgRetries={r.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        var qs = q.Count > 0 ? "?" + string.Join("&", q) : "";
        return await _http.GetFromJsonAsync<CostDto>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/cost{qs}",
            JsonOpts,
            ct);
    }

    public async Task<CostBackfillDto?> BackfillCostAsync(string projectId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/cost/backfill",
            new { },
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase);
        }
        return await resp.Content.ReadFromJsonAsync<CostBackfillDto>(JsonOpts, ct);
    }

    public async Task<ConfigDto?> GetConfigAsync(string projectId, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<ConfigDto>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/config",
            JsonOpts,
            ct);

    public async Task<ConfigDto?> SaveConfigAsync(
        string projectId,
        Dictionary<string, object?> updates,
        CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/config",
            updates,
            JsonOpts,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase);
        }
        return await resp.Content.ReadFromJsonAsync<ConfigDto>(JsonOpts, ct);
    }

    public async Task<ExtractCastResultDto?> ExtractCastFromScreenplayAsync(
        string projectId,
        bool force = true,
        string model = "grok-4.5",
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/extract-cast",
            new { projectId, force, model },
            JsonOpts,
            ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonSerializer.Deserialize<ExtractCastResultDto>(raw, JsonOpts)
                   ?? new ExtractCastResultDto { Ok = false, Error = raw };
        }
        catch
        {
            return new ExtractCastResultDto
            {
                Ok = false,
                Error = string.IsNullOrWhiteSpace(raw) ? resp.ReasonPhrase : raw,
            };
        }
    }

    public async Task<CharactersDto?> GetCharactersAsync(string projectId, CancellationToken ct = default)
    {
        var dto = await _http.GetFromJsonAsync<CharactersDto>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters",
            JsonOpts,
            ct);
        // API returns root-relative /api/... paths; browser would hit Blazor host (7206), not Engine API (5088).
        if (dto?.Characters is not null)
        {
            foreach (var c in dto.Characters)
            {
                c.RefUrl = AbsolutizeMediaUrl(c.RefUrl)
                           ?? (c.Locked ? CharacterRefUrl(projectId, c.Key) : null);
                c.PreferredUrl = AbsolutizeMediaUrl(c.PreferredUrl)
                                 ?? (c.HasPreferred
                                     ? (c.Locked
                                         ? CharacterRefUrl(projectId, c.Key)
                                         : CharacterVariantUrl(projectId, c.Key, 1))
                                     : null);
                foreach (var b in c.BookRefs)
                {
                    b.Url = AbsolutizeMediaUrl(b.Url)
                            ?? (b.Exists && b.Index is int bi
                                ? CharacterBookRefUrl(projectId, c.Key, bi)
                                : null);
                }
                foreach (var v in c.Variants)
                {
                    v.Url = AbsolutizeMediaUrl(v.Url)
                            ?? (v.Exists && v.Index is int vi
                                ? CharacterVariantUrl(projectId, c.Key, vi)
                                : null);
                }
            }
        }
        return dto;
    }

    public string ApiBaseUrl =>
        (_http.BaseAddress?.ToString() ?? "http://127.0.0.1:5088").TrimEnd('/');

    /// <summary>
    /// Turn API root-relative media paths into absolute Engine API URLs for &lt;img src&gt;.
    /// </summary>
    public string? AbsolutizeMediaUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return url;
        if (url.StartsWith('/'))
            return ApiBaseUrl + url;
        return ApiBaseUrl + "/" + url.TrimStart('/');
    }

    public string CharacterRefUrl(string projectId, string charKey)
    {
        return $"{ApiBaseUrl}/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/ref";
    }

    public string CharacterVariantUrl(string projectId, string charKey, int index)
    {
        return $"{ApiBaseUrl}/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/variants/{index}";
    }

    public string CharacterBookRefUrl(string projectId, string charKey, int index)
    {
        return $"{ApiBaseUrl}/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/bookrefs/{index}";
    }

    public async Task UpdateCharacterVoiceAsync(
        string projectId,
        string charKey,
        string? voiceProfile,
        string? voiceLabel = null,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/voice",
            new UpdateCharacterVoiceRequest
            {
                ProjectId = projectId,
                CharKey = charKey,
                VoiceProfile = voiceProfile,
                VoiceLabel = voiceLabel,
            },
            JsonOpts,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase);
        }
    }

    /// <summary>
    /// Save look text; by default API runs AI scrub (literal + base look). Returns cleaned fields.
    /// </summary>
    public async Task DeleteCharacterImageAsync(
        string projectId,
        string charKey,
        string kind,
        int index = 0,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/delete-image",
            new DeleteCharacterImageRequest { Kind = kind, Index = index },
            JsonOpts,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase);
        }
    }

    public async Task<UpdateCharacterLookResult> UpdateCharacterLookAsync(
        string projectId,
        string charKey,
        string? description,
        string? visualLock = null,
        bool scrubWithAi = true,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/look",
            new UpdateCharacterLookRequest
            {
                ProjectId = projectId,
                CharKey = charKey,
                Description = description,
                VisualLock = visualLock,
                ScrubWithAi = scrubWithAi,
            },
            JsonOpts,
            ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(body) ?? resp.ReasonPhrase);

        try
        {
            return JsonSerializer.Deserialize<UpdateCharacterLookResult>(body, JsonOpts)
                   ?? new UpdateCharacterLookResult { Ok = true };
        }
        catch
        {
            return new UpdateCharacterLookResult { Ok = true, Message = "Look updated" };
        }
    }

    public async Task StartCharacterVariantsAsync(
        string projectId,
        string charKey,
        CancellationToken ct = default) =>
        await StartCharacterVariantsAsync(new StartCharacterVariantsRequest
        {
            ProjectId = projectId,
            CharKey = charKey,
            SeedMode = "auto",
        }, ct);

    public async Task StartCharacterVariantsAsync(
        StartCharacterVariantsRequest req,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            "/api/jobs/character-variants",
            req,
            JsonOpts,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase);
        }
    }

    /// <summary>Sync heuristic-only attach (no Grok). Prefer <see cref="StartSortCharacterPlatesAsync"/>.</summary>
    public async Task<AttachCharacterPlatesResult?> AttachBookPlatesAsync(
        string projectId,
        bool force = true,
        string? charKey = null,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/attach-book-plates",
            new AttachCharacterPlatesRequest
            {
                ProjectId = projectId,
                Force = force,
                CopyIntoAssets = true,
                CharKey = charKey,
                UseGrok = false,
            },
            JsonOpts,
            ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(body) ?? resp.ReasonPhrase);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("attach", out var att))
                return JsonSerializer.Deserialize<AttachCharacterPlatesResult>(att.GetRawText(), JsonOpts);
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>
    /// Start Grok vision job: classify book pages → character plates in scenes.json.
    /// Progress via SignalR; cancel with <see cref="CancelJobAsync"/>.
    /// </summary>
    public async Task StartSortCharacterPlatesAsync(
        string projectId,
        bool useGrok = true,
        int maxImages = 32,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            "/api/jobs/sort-character-plates",
            new AttachCharacterPlatesRequest
            {
                ProjectId = projectId,
                Force = true,
                CopyIntoAssets = true,
                UseGrok = useGrok,
                MaxImages = maxImages,
            },
            JsonOpts,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase);
        }
    }

    public async Task StartBookPrepareAsync(
        string projectId,
        bool forceExtract = true,
        bool forceVision = false,
        bool autoVision = true,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            "/api/jobs/book-prepare",
            new StartBookPrepareRequest
            {
                ProjectId = projectId,
                ForceExtract = forceExtract,
                ForceVision = forceVision,
                AutoVision = autoVision,
            },
            JsonOpts,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase);
        }
    }

    public async Task LockCharacterVariantAsync(
        string projectId,
        string charKey,
        int index,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/lock-variant",
            new { index },
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase);
        }
    }

    public async Task LockCharacterBookRefAsync(
        string projectId,
        string charKey,
        int index,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/lock-bookref",
            new { index },
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase);
        }
    }

    public async Task UnlockCharacterAsync(
        string projectId,
        string charKey,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/unlock",
            new { },
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase);
        }
    }

    /// <summary>Upload and lock an operator-provided character reference image.</summary>
    public async Task UploadCharacterRefAsync(
        string projectId,
        string charKey,
        Stream content,
        string fileName,
        CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var streamContent = new StreamContent(content);
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                _ => "image/png",
            });
        form.Add(streamContent, "file", fileName);

        using var resp = await _http.PostAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/upload-ref",
            form,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase);
        }
    }

    private static string? TryError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var e))
                return e.GetString();
        }
        catch { /* ignore */ }
        return json.Length > 200 ? json[..200] : json;
    }
}

public sealed class ProjectsDto
{
    public bool Ok { get; set; }
    public ProjectInfo? Active { get; set; }
    public List<ProjectInfo> Projects { get; set; } = new();
}

public sealed class JobsListDto
{
    public bool Ok { get; set; }
    public bool Running { get; set; }
    public List<JobSnapshot> Jobs { get; set; } = new();
    public int Count { get; set; }
}

public sealed class JobDetailDto
{
    public bool Ok { get; set; }
    public JobSnapshot? Job { get; set; }
}

public sealed class CapacityDto
{
    public bool Ok { get; set; }
    public CapacityOptions? Capacity { get; set; }
    public bool Running { get; set; }
    public int RunningCount { get; set; }
    public bool UseFakes { get; set; }
}

/// <summary>Admin snapshot (Phase C: jobs + locks + counters).</summary>
public sealed class AdminStateDto
{
    public bool Ok { get; set; }
    public DateTimeOffset? GeneratedAt { get; set; }
    public AdminProcessDto? Process { get; set; }
    public CapacityOptions? Capacity { get; set; }
    public AdminJobsDto? Jobs { get; set; }
    public AdminProjectsDto? Projects { get; set; }
    public AdminCallerDto? Caller { get; set; }
    public List<AdminLockDto> Locks { get; set; } = new();
    public int ApiInFlight { get; set; }
    public int FfmpegInFlight { get; set; }
    public int CapacityRejects { get; set; }
    public int LockConflicts { get; set; }
    public AdminHttpTrafficDto? Http { get; set; }
    public FilmStudio.Core.Models.LoadSimLiveStateDto? LoadSim { get; set; }
    public List<ProcessSampleDto>? ProcessHistory { get; set; }
}

public sealed class ProcessSampleDto
{
    public DateTimeOffset At { get; set; }
    public double WorkingSetMb { get; set; }
    public double GcHeapMb { get; set; }
    public int ThreadCount { get; set; }
}

public sealed class AdminHttpTrafficDto
{
    public long TotalLifetime { get; set; }
    public int RequestsLast5Sec { get; set; }
    public int RequestsLast30Sec { get; set; }
    public int NonAdminLast5Sec { get; set; }
    public int NonAdminLast30Sec { get; set; }
    public Dictionary<string, int>? ByPrefixLast5Sec { get; set; }
    public Dictionary<string, int>? ByPrefixLast30Sec { get; set; }
}

public sealed class AdminLockDto
{
    public string? Resource { get; set; }
    public string? UserId { get; set; }
    public string? Reason { get; set; }
    public string? JobId { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class LocksDto
{
    public bool Ok { get; set; }
    public List<AdminLockDto> Locks { get; set; } = new();
    public string? UserId { get; set; }
}

public sealed class AdminProcessDto
{
    public long UptimeSec { get; set; }
    public double WorkingSetMb { get; set; }
    public double GcHeapMb { get; set; }
    public int ThreadCount { get; set; }
    public string? Environment { get; set; }
    public bool UseFakes { get; set; }
}

public sealed class AdminJobsDto
{
    public bool Running { get; set; }
    public int Count { get; set; }
    public List<AdminJobItemDto> Items { get; set; } = new();
}

public sealed class AdminJobItemDto
{
    public string? JobId { get; set; }
    public string? UserId { get; set; }
    public string? ProjectId { get; set; }
    public string? Kind { get; set; }
    public int? Scene { get; set; }
    public int? Clip { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
    public int Index { get; set; }
    public int Total { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public long? AgeMs { get; set; }
}

public sealed class AdminProjectsDto
{
    public string? Active { get; set; }
    public string? Workspace { get; set; }
}

public sealed class AdminCallerDto
{
    public string? UserId { get; set; }
    public List<string> Roles { get; set; } = new();
}

public sealed class JobsDto
{
    public bool Ok { get; set; }
    public bool Running { get; set; }
    public JobSnapshot? Job { get; set; }
}

public sealed class ConfigDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public Dictionary<string, JsonElement>? Config { get; set; }
}

public sealed class ExtractCastResultDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public string? Path { get; set; }
    public int CharacterCount { get; set; }
    public List<string>? Characters { get; set; }
    public string? MovieTitle { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public string? RawPath { get; set; }
}

public sealed class CharactersDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public List<CharacterSummary> Characters { get; set; } = new();
    /// <summary>pipeline_state.character_plates — whether import sorted plates into scenes.json.</summary>
    public CharacterPlatesState? CharacterPlates { get; set; }
    /// <summary>Grok ≤ 3, Gemini ≤ 14 — from image_provider / image_model_name.</summary>
    public ImageSeedLimits? ImageSeedLimits { get; set; }
}

public sealed class EditLogDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public EditLogDocument? EditLog { get; set; }
}

public sealed class ClipReviewsDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public Dictionary<string, string>? Reviews { get; set; }
}

public sealed class ScenesListDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public int SceneCount { get; set; }
    public int ClipCount { get; set; }
    public int ClipsOnDisk { get; set; }
    public List<SceneSummary> Scenes { get; set; } = new();
}

public sealed class WipMovieMetaDto
{
    public bool Ok { get; set; }
    public bool Exists { get; set; }
    /// <summary>True if missing or inputs newer than WIP (or stale scene composites).</summary>
    public bool Stale { get; set; }
    public bool CanBuild { get; set; }
    public string? Reason { get; set; }
    public string? ProjectId { get; set; }
    public string? Path { get; set; }
    public long Bytes { get; set; }
    public string? UpdatedAt { get; set; }
    public string? Url { get; set; }
    public List<int> StaleScenes { get; set; } = new();
}

public sealed class SceneDetailDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public SceneDetail? Scene { get; set; }
}

public sealed class AdaptationDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public AdaptationStatus? Adaptation { get; set; }
}

public sealed class FountainImportDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public string? Title { get; set; }
    public int SceneCount { get; set; }
    public int SceneHeadingCount { get; set; }
    public int CharacterCount { get; set; }
    public int LocationCount { get; set; }
    public string? Message { get; set; }
    public AdaptationStatus? Adaptation { get; set; }
}

public sealed class ScreenplayDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public string Text { get; set; } = "";
    public ScreenplayStatus? Screenplay { get; set; }
    public AdaptationStatus? Adaptation { get; set; }
}

public sealed class ScreenplaySaveDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public string? Message { get; set; }
    public ScreenplayStatus? Screenplay { get; set; }
    public AdaptationStatus? Adaptation { get; set; }
}

public sealed class ScreenplaySignOffDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public string? Title { get; set; }
    public int SceneCount { get; set; }
    public int CharacterCount { get; set; }
    public int LocationCount { get; set; }
    public bool HashChanged { get; set; }
    public string? Message { get; set; }
    public ScreenplayStatus? Screenplay { get; set; }
    public AdaptationStatus? Adaptation { get; set; }
}

public sealed class BookContextDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public bool HasBook { get; set; }
    public int? PageNumber { get; set; }
    public int SceneIndex { get; set; }
    public string? Heading { get; set; }
    public string Excerpt { get; set; } = "";
    public string? MatchReason { get; set; }
    public int TotalPages { get; set; }
    public string? Message { get; set; }
}

public sealed class CostDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public CostReport? Cost { get; set; }
}

public sealed class CostBackfillDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public CostBackfillResult? Backfill { get; set; }
}
