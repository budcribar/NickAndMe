using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Auth;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;

namespace PageToMovie.Web.Services;

/// <summary>HTTP client for PageToMovie.Api (C# backend).</summary>
public sealed class EngineApiClient
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly AdminSessionService? _session;
    private readonly EngineApiOptions _opts;

    /// <summary>Short-lived media token for &lt;img&gt;/&lt;video&gt; query auth (not the session JWT).</summary>
    private string? _mediaToken;
    private DateTimeOffset _mediaTokenExpires = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _mediaTokenLock = new(1, 1);
    private int _mediaRefreshQueued;

    public async Task<ModelsCatalogResponse> GetModelsCatalogAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/models-catalog");
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadFromJsonAsync<ModelsCatalogResponse>(JsonOpts, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(body?.Error ?? "Failed to load models catalog");
        return body ?? new ModelsCatalogResponse();
    }

    /// <summary>Raw catalog JSON for SupportedModelCatalog.TryLoadFromJson (WASM has no file).</summary>
    public async Task<string?> GetModelsCatalogJsonAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("/api/models/catalog-json", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch { return null; }
    }

    public async Task<string> SaveModelsCatalogRawAsync(string rawJson, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, "/api/admin/models-catalog")
        {
            Content = new StringContent(rawJson, System.Text.Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadFromJsonAsync<ModelsCatalogSaveResponse>(JsonOpts, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(body?.Error ?? "Failed to save models catalog");
        return body?.Message ?? "Models catalog saved successfully.";
    }

    public async Task<ModelsCatalogValidateResponse> ValidateModelsCatalogRawAsync(string rawJson, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/models-catalog/validate")
        {
            Content = new StringContent(rawJson, System.Text.Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadFromJsonAsync<ModelsCatalogValidateResponse>(JsonOpts, ct)
            ?? new ModelsCatalogValidateResponse();
        if (!resp.IsSuccessStatusCode && string.IsNullOrWhiteSpace(body.Error))
            body.Error = "Catalog validation request failed";
        return body;
    }

    public EngineApiClient(
        HttpClient http,
        AdminSessionService? session = null,
        IOptions<EngineApiOptions>? opts = null)
    {
        _http = http;
        _session = session;
        _opts = opts?.Value ?? new EngineApiOptions();
        SyncIdentityHeaders();
        if (_session is not null)
            _session.Changed += OnSessionChanged;
    }

    public async Task<CatalogUpdateScanClientResult> CheckModelsCatalogUpdatesAsync(CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync("/api/admin/models-catalog/check-updates", content: null, ct);
        var body = await resp.Content.ReadFromJsonAsync<CatalogUpdateScanClientEnvelope>(JsonOpts, ct)
            ?? new CatalogUpdateScanClientEnvelope();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(body.Error ?? "Catalog update scan failed");
        return body.Result ?? new CatalogUpdateScanClientResult();
    }


    public async Task<bool> HasAcceptedTermsAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        try
        {
            var res = await _http.GetFromJsonAsync<JsonElement>($"/api/users/{Uri.EscapeDataString(userId.Trim())}/terms", ct);
            if (res.TryGetProperty("hasAccepted", out var ha) && ha.ValueKind is JsonValueKind.True)
                return true;
            if (res.TryGetProperty("accepted", out var a) && a.ValueKind is JsonValueKind.True)
                return true;
            return false;
        }
        catch { return false; }
    }

    private void OnSessionChanged()
    {
        SyncIdentityHeaders();
        // Drop cached media token when session changes; refresh in background when signed in.
        _mediaToken = null;
        _mediaTokenExpires = DateTimeOffset.MinValue;
        if (_session?.IsLoggedIn == true)
            _ = EnsureMediaAccessAsync();
    }

    /// <summary>Push X-User-Id / Bearer onto the shared HttpClient defaults (scoped client).</summary>
    public void SyncIdentityHeaders()
    {
        try
        {
            _http.DefaultRequestHeaders.Remove("Authorization");
            _http.DefaultRequestHeaders.Remove(AuthHeaders.UserId);
            if (_session is null) return;
            // Only send identity when signed in — anonymous must not spoof X-User-Id=local for gated APIs.
            if (string.IsNullOrWhiteSpace(_session.Token))
                return;
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

    /// <summary>
    /// Ensure a short-lived media token is cached for element src URLs.
    /// Call after login / page load so &lt;video&gt;/&lt;img&gt; do not embed the session JWT.
    /// </summary>
    public async Task EnsureMediaAccessAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_session?.Token))
        {
            _mediaToken = null;
            _mediaTokenExpires = DateTimeOffset.MinValue;
            return;
        }

        if (HasFreshMediaToken())
            return;

        await _mediaTokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (HasFreshMediaToken())
                return;

            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/media-token");
            ApplyAuth(req);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return;
            var dto = await resp.Content.ReadFromJsonAsync<MediaTokenDto>(JsonOpts, ct)
                .ConfigureAwait(false);
            if (dto is null || string.IsNullOrWhiteSpace(dto.Token))
                return;
            _mediaToken = dto.Token.Trim();
            _mediaTokenExpires = dto.ExpiresAt
                ?? DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(dto.Minutes ?? 30, 5, 120));
        }
        catch
        {
            // media may 401 until next retry
        }
        finally
        {
            _mediaTokenLock.Release();
        }
    }

    private bool HasFreshMediaToken() =>
        !string.IsNullOrWhiteSpace(_mediaToken)
        && _mediaTokenExpires > DateTimeOffset.UtcNow.AddMinutes(2);

    private void QueueMediaTokenRefreshIfNeeded()
    {
        if (HasFreshMediaToken() || string.IsNullOrWhiteSpace(_session?.Token))
            return;
        if (Interlocked.CompareExchange(ref _mediaRefreshQueued, 1, 0) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try { await EnsureMediaAccessAsync().ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref _mediaRefreshQueued, 0); }
        });
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

    /// <summary>Shared send/parse for the auth endpoints returning a <see cref="LoginResponse"/>
    /// (signup / login / operator-override): posts the prepared request, treats an empty body as a
    /// failure, and forces Ok=false on any non-success status.</summary>
    private async Task<LoginResponse?> SendLoginRequestAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadFromJsonAsync<LoginResponse>(JsonOpts, ct);
        if (body is null)
            return new LoginResponse { Ok = false, Error = "Empty response" };
        if (!resp.IsSuccessStatusCode && body.Ok)
            body.Ok = false;
        return body;
    }

    public async Task<LoginResponse?> SignupAsync(
        string username,
        string password,
        string? email = null,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/signup")
        {
            Content = JsonContent.Create(
                new LoginRequest { Username = username, Password = password, Email = email },
                options: JsonOpts),
        };
        return await SendLoginRequestAsync(req, ct);
    }

    /// <summary>
    /// Request password reset. Emails a link when the account has an address;
    /// also flags the account for admin-assisted reset. Always ok if accepted.
    /// </summary>
    public async Task<string> ForgotPasswordAsync(string usernameOrEmail, CancellationToken ct = default)
        => await PostUsernameRequestAsync(
            "/api/auth/forgot-password", usernameOrEmail,
            "If that account exists and has a confirmed email, a reset link was sent to your inbox.", ct);

    /// <summary>Shared POST for the username-only auth endpoints (forgot-password / resend-confirmation):
    /// sends a <see cref="ForgotPasswordRequest"/>, throws a best-effort error on failure, otherwise
    /// returns the server message or the caller-supplied default.</summary>
    private async Task<string> PostUsernameRequestAsync(
        string endpoint, string usernameOrEmail, string defaultMessage, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(
                new ForgotPasswordRequest { Username = usernameOrEmail },
                options: JsonOpts),
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadFromJsonAsync<ForgotPasswordResponse>(JsonOpts, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(body?.Error ?? body?.Message ?? "Request failed");
        return body?.Message ?? defaultMessage;
    }

    public async Task<(bool Ok, string Message)> ConfirmEmailAsync(string token, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/confirm-email")
        {
            Content = JsonContent.Create(new ConfirmEmailRequest { Token = token }, options: JsonOpts),
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadFromJsonAsync<ForgotPasswordResponse>(JsonOpts, ct);
        var msg = body?.Message ?? body?.Error ?? (resp.IsSuccessStatusCode ? "Email confirmed." : "Confirmation failed.");
        return (resp.IsSuccessStatusCode && body?.Ok != false, msg);
    }

    public async Task<string> ResendConfirmationAsync(string usernameOrEmail, CancellationToken ct = default)
        => await PostUsernameRequestAsync(
            "/api/auth/resend-confirmation", usernameOrEmail,
            "If that account needs confirmation, a new email was sent.", ct);

    public async Task<(bool Ok, string Message)> ResetPasswordWithTokenAsync(
        string token,
        string newPassword,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/reset-password")
        {
            Content = JsonContent.Create(
                new ResetPasswordWithTokenRequest { Token = token, NewPassword = newPassword },
                options: JsonOpts),
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadFromJsonAsync<ForgotPasswordResponse>(JsonOpts, ct);
        var msg = body?.Message ?? body?.Error ?? (resp.IsSuccessStatusCode ? "Password updated." : "Reset failed.");
        return (resp.IsSuccessStatusCode && body?.Ok != false, msg);
    }

    public async Task<TestEmailResponse> TestEmailAsync(string toEmail, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/test-email")
        {
            Content = JsonContent.Create(new TestEmailRequest { ToEmail = toEmail }, options: JsonOpts),
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadFromJsonAsync<TestEmailResponse>(JsonOpts, ct);
        return body ?? new TestEmailResponse
        {
            Ok = resp.IsSuccessStatusCode,
            Message = resp.IsSuccessStatusCode ? "Test email sent." : null,
            Error = resp.IsSuccessStatusCode ? null : $"HTTP {(int)resp.StatusCode}",
        };
    }

    public async Task AdminSetUserPasswordAsync(
        string userId,
        string newPassword,
        string adminPassword,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users/set-password")
        {
            Content = JsonContent.Create(new AdminSetUserPasswordRequest
            {
                UserId = userId,
                NewPassword = newPassword,
                AdminPassword = adminPassword,
            }, options: JsonOpts),
        };
        await SendJsonAsync<object>(req, ct);
    }

    public async Task<LoginResponse?> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest { Username = username, Password = password }, options: JsonOpts),
        };
        return await SendLoginRequestAsync(req, ct);
    }

    /// <summary>Operator override via PageToMovie_LOGIN_OVERRIDE (Railway-friendly).</summary>
    public async Task<LoginResponse?> LoginWithOperatorOverrideAsync(string secret, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/operator-override")
        {
            Content = JsonContent.Create(new OperatorOverrideRequest { Secret = secret }, options: JsonOpts),
        };
        return await SendLoginRequestAsync(req, ct);
    }

    /// <summary>
    /// DEV ONLY: fakes-mode login bypass. Returns a deterministic dev-user session when the server
    /// runs with fakes enabled (the endpoint exists only then); returns null in any real deployment
    /// (endpoint 404s), so the caller falls through to the normal login gate.
    /// </summary>
    public async Task<LoginResponse?> TryDevLoginAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/dev-login");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return null;
            return await resp.Content.ReadFromJsonAsync<LoginResponse>(JsonOpts, ct);
        }
        catch
        {
            return null;
        }
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
            if (_session is not null)
                await _session.ClearAsync().ConfigureAwait(false);
        }
    }

    public async Task<MeResponse?> GetMeAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        return await SendJsonAsync<MeResponse>(req, ct);
    }

    /// <summary>JIT capability availability (any provider configured for each capability, or fakes).</summary>
    public async Task<CapabilitiesResponse?> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/capabilities");
        return await SendJsonAsync<CapabilitiesResponse>(req, ct);
    }

    public async Task<AdminStateDto?> GetAdminStateAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/state");
        return await SendJsonAsync<AdminStateDto>(req, ct);
    }

    public async Task<BookCacheAdminDto?> GetAdminBookCacheAsync(int take = 100, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/book-cache?take={take}");
        return await SendJsonAsync<BookCacheAdminDto>(req, ct);
    }

    public async Task<RuntimeConfigDto?> GetAdminConfigAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/config");
        return await SendJsonAsync<RuntimeConfigDto>(req, ct);
    }

    /// <summary>Aggregated AI/model-call telemetry for the admin AI-Calls analytics page.</summary>
    public async Task<AiCallAnalyticsDto?> GetAiCallAnalyticsAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/ai-calls");
        var env = await SendJsonAsync<AiCallAnalyticsEnvelope>(req, ct);
        return env?.Data;
    }

    private sealed class AiCallAnalyticsEnvelope
    {
        public bool Ok { get; set; }
        public AiCallAnalyticsDto? Data { get; set; }
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

    /// <summary>
    /// Admin full-project zip. Returns open response stream + suggested filename.
    /// Caller must dispose the response/stream.
    /// </summary>
    /// <summary>Shared GET-a-zip helper for the export endpoints: streams headers first, throws a
    /// best-effort error on failure (disposing the response), then resolves the suggested filename
    /// from Content-Disposition with a caller-supplied fallback. Caller disposes the returned response.</summary>
    private async Task<(HttpResponseMessage Response, string FileName)> DownloadZipAsync(
        string url, string fallbackFileName, string failMessage, CancellationToken ct)
    {
        SyncIdentityHeaders();
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase ?? failMessage);
        }

        var fileName = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                       ?? resp.Content.Headers.ContentDisposition?.FileNameStar?.Trim('"')
                       ?? fallbackFileName;
        return (resp, fileName);
    }

    public async Task<(HttpResponseMessage Response, string FileName)> ExportProjectZipAsync(
        string projectId,
        CancellationToken ct = default)
        => await DownloadZipAsync(
            $"/api/admin/projects/{Uri.EscapeDataString(projectId)}/export",
            $"PageToMovie_{projectId}.zip", "export failed", ct);

    /// <summary>User-mode project export (no admin) — same server zip as the admin export, gated on
    /// login rather than the admin role. Backs a user-facing full backup: caller merges local media
    /// client-side. Returns the open response stream + suggested filename; caller disposes both.</summary>
    public async Task<(HttpResponseMessage Response, string FileName)> ExportProjectZipAsUserAsync(
        string projectId,
        CancellationToken ct = default)
        => await DownloadZipAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/export",
            $"PageToMovie_{projectId}.zip", "export failed", ct);

    /// <summary>
    /// Admin server diagnostic logs zip. Returns open response stream + suggested filename.
    /// Caller must dispose the response/stream.
    /// </summary>
    public async Task<(HttpResponseMessage Response, string FileName)> ExportServerLogsZipAsync(
        CancellationToken ct = default)
        => await DownloadZipAsync(
            "/api/admin/logs/export",
            $"pagetomovie-server-logs-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip",
            "Server logs export failed", ct);

    /// <summary>Admin import project zip (multipart field name: file).</summary>
    public async Task<AdminProjectImportResultDto?> ImportProjectZipAsync(
        Stream zipStream,
        string fileName,
        string? preferredId = null,
        bool overwrite = false,
        string? targetUserId = null,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(zipStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(streamContent, "file", string.IsNullOrWhiteSpace(fileName) ? "project.zip" : fileName);
        if (!string.IsNullOrWhiteSpace(preferredId))
            content.Add(new StringContent(preferredId.Trim()), "projectId");
        if (!string.IsNullOrWhiteSpace(targetUserId))
            content.Add(new StringContent(targetUserId.Trim()), "targetUserId");
        content.Add(new StringContent(overwrite ? "true" : "false"), "overwrite");

        using var resp = await _http.PostAsync("/api/admin/projects/import", content, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(raw) ?? resp.ReasonPhrase ?? "import failed");
        return JsonSerializer.Deserialize<AdminProjectImportResultDto>(raw, JsonOpts);
    }

    /// <summary>User-mode import: import a project zip into the current user's own namespace
    /// (no admin required). Multipart field name: file.</summary>
    public async Task<AdminProjectImportResultDto?> ImportProjectZipAsUserAsync(
        Stream zipStream,
        string fileName,
        string? name = null,
        bool overwrite = false,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(zipStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(streamContent, "file", string.IsNullOrWhiteSpace(fileName) ? "project.zip" : fileName);
        if (!string.IsNullOrWhiteSpace(name))
            content.Add(new StringContent(name.Trim()), "name");
        content.Add(new StringContent(overwrite ? "true" : "false"), "overwrite");

        using var resp = await _http.PostAsync("/api/projects/import", content, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(raw) ?? resp.ReasonPhrase ?? "import failed");
        return JsonSerializer.Deserialize<AdminProjectImportResultDto>(raw, JsonOpts);
    }

    public async Task<AdminCreditsOverviewDto?> GetAdminUsersCreditsAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/users");
        var wrap = await SendJsonAsync<AdminUsersCreditsResponse>(req, ct);
        return wrap?.Overview;
    }

    public async Task<UserCreditSummaryDto?> GrantAdminCreditsAsync(
        AdminGrantCreditsRequest body,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users/credits")
        {
            Content = JsonContent.Create(body, options: JsonOpts),
        };
        var wrap = await SendJsonAsync<AdminGrantCreditsResponse>(req, ct);
        return wrap?.User;
    }

    public async Task<CreatorProfileDto?> GetCreatorProfileAsync(
        string handle,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/creators/{Uri.EscapeDataString(handle)}");
        return await SendJsonAsync<CreatorProfileDto>(req, ct);
    }

    public async Task<ContributionDiffDto?> GetContributionDiffAsync(
        string projectId,
        string? originProjectId = null,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        var url = $"/api/projects/{Uri.EscapeDataString(projectId)}/contribution-diff";
        if (!string.IsNullOrWhiteSpace(originProjectId))
            url += $"?originProjectId={Uri.EscapeDataString(originProjectId)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendJsonAsync<ContributionDiffDto>(req, ct);
    }

    public async Task<MediaSyncResultDto?> SyncContributionMediaAsync(
        string projectId,
        string? parentProjectId = null,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{Uri.EscapeDataString(projectId)}/contribution-sync-media");
        req.Content = JsonContent.Create(new { ParentProjectId = parentProjectId }, options: JsonOpts);
        return await SendJsonAsync<MediaSyncResultDto>(req, ct);
    }

    public async Task<bool> SetProjectVisibilityModeAsync(
        string projectId,
        string visibilityMode,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{Uri.EscapeDataString(projectId)}/visibility");
        req.Content = JsonContent.Create(new { visibilityMode }, options: JsonOpts);
        var res = await _http.SendAsync(req, ct);
        return res.IsSuccessStatusCode;
    }

    public async Task<ProjectInfo?> ForkProjectAsync(
        string projectId,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{Uri.EscapeDataString(projectId)}/fork");
        return await SendJsonAsync<ProjectInfo>(req, ct);
    }

    /// <summary>Public forkable movies (visibility "Open") — the Easy Start "story in your voice" picker.</summary>
    public async Task<List<ForkableStoryDto>> ListForkableProjectsAsync(CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        try
        {
            var dto = await _http.GetFromJsonAsync<ForkableStoriesEnvelope>("/api/projects/forkable", JsonOpts, ct);
            return dto?.Projects ?? new List<ForkableStoryDto>();
        }
        catch
        {
            return new List<ForkableStoryDto>();
        }
    }

    private sealed class ForkableStoriesEnvelope
    {
        public bool Ok { get; set; }
        public List<ForkableStoryDto>? Projects { get; set; }
    }

    /// <summary>Persistently delete a whole scene from the shot plan (blueprint + on-disk media).</summary>
    public async Task<(bool Ok, string? Message, string? Error)> DeleteSceneAsync(
        string projectId, int scene, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var req = new HttpRequestMessage(
            HttpMethod.Delete, $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{scene}");
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return (false, null, TryError(body) ?? resp.ReasonPhrase);
        return (true, TrySceneMessage(body), null);
    }

    /// <summary>Add a scene to the shot plan — a blank scene, or a prefilled credits scene when
    /// <paramref name="credits"/> is true. Returns the new scene number.</summary>
    public async Task<(bool Ok, int Scene, string? Message, string? Error)> AddSceneAsync(
        string projectId, bool credits = false, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        var path = credits
            ? $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/credits"
            : $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes";
        using var req = new HttpRequestMessage(HttpMethod.Post, path);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return (false, 0, null, TryError(body) ?? resp.ReasonPhrase);
        var sceneNo = 0;
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("scene", out var sEl) && sEl.TryGetInt32(out var n)) sceneNo = n;
        }
        catch { /* ignore */ }
        return (true, sceneNo, TrySceneMessage(body), null);
    }

    private static string? TrySceneMessage(string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            return d.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch { return null; }
    }

    // ── Checkpoints (named project snapshots, backed by the per-project git repo; video is
    //     git-ignored, so reverting restores the plan/config without touching your clips) ──

    /// <summary>Create a named checkpoint (a commit of the project's current text/plan state).</summary>
    public async Task<(bool Ok, string? Error)> CreateCheckpointAsync(string projectId, string name, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"/api/projects/{Uri.EscapeDataString(projectId)}/git/commit")
        {
            // ForceCommit: a named checkpoint must always land with the user's chosen message, even
            // when nothing has changed since the last commit — unlike the auto-commit-after-save path
            // (CommitProjectChangesAsync below), which intentionally skips a clean tree.
            Content = JsonContent.Create(new { Message = name, ForceCommit = true }, options: JsonOpts),
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return resp.IsSuccessStatusCode ? (true, null) : (false, TryError(body) ?? resp.ReasonPhrase);
    }

    /// <summary>List checkpoints (newest first).</summary>
    public async Task<List<CheckpointDto>> ListCheckpointsAsync(string projectId, int limit = 30, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        try
        {
            var dto = await _http.GetFromJsonAsync<CheckpointHistoryEnvelope>(
                $"/api/projects/{Uri.EscapeDataString(projectId)}/git/history?limit={limit}", JsonOpts, ct);
            return dto?.History ?? new List<CheckpointDto>();
        }
        catch
        {
            return new List<CheckpointDto>();
        }
    }

    /// <summary>Revert the project to a checkpoint (restores plan/config; clips are untouched).</summary>
    public async Task<(bool Ok, string? Message, string? Error)> RevertToCheckpointAsync(string projectId, string commitHash, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"/api/projects/{Uri.EscapeDataString(projectId)}/git/revert/{Uri.EscapeDataString(commitHash)}");
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return (false, null, TryError(body) ?? resp.ReasonPhrase);
        return (true, TrySceneMessage(body), null);
    }

    private sealed class CheckpointHistoryEnvelope
    {
        public bool Ok { get; set; }
        public List<CheckpointDto>? History { get; set; }
    }

    public async Task<SyncOriginResultDto?> SyncOriginAsync(
        string projectId,
        string parentProjectId,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{Uri.EscapeDataString(projectId)}/sync-origin")
        {
            Content = JsonContent.Create(new { ParentProjectId = parentProjectId }, options: JsonOpts)
        };
        return await SendJsonAsync<SyncOriginResultDto>(req, ct);
    }

    /// <summary>
    /// Commit (optional) + push the text project package to the configured remote.
    /// Video is never included. Returns history URL when the host is GitHub.
    /// </summary>
    public async Task<ProjectPushResultDto?> PushProjectAsync(
        string projectId,
        bool commitFirst = true,
        string? message = null,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{Uri.EscapeDataString(projectId)}/push")
        {
            Content = JsonContent.Create(new
            {
                CommitFirst = commitFirst,
                Message = message,
            }, options: JsonOpts),
        };
        return await SendJsonAsync<ProjectPushResultDto>(req, ct);
    }

    public async Task<AdminUserActionResultDto?> SetAdminUserDisabledAsync(
        AdminSetUserDisabledRequest body,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users/disabled")
        {
            Content = JsonContent.Create(body, options: JsonOpts),
        };
        return await SendJsonAsync<AdminUserActionResultDto>(req, ct);
    }

    public async Task<AdminDeleteUserResultDto?> DeleteAdminUserAsync(
        AdminDeleteUserRequest body,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users/delete")
        {
            Content = JsonContent.Create(body, options: JsonOpts),
        };
        return await SendJsonAsync<AdminDeleteUserResultDto>(req, ct);
    }

    // ---- Admin Learning (P0–P4) ----
    public async Task<LearningInsightsDto?> GetLearningInsightsAsync(
        string? projectId = null,
        int take = 40,
        CancellationToken ct = default)
    {
        var q = $"/api/admin/learning/insights?take={take}";
        if (!string.IsNullOrWhiteSpace(projectId))
            q += "&projectId=" + Uri.EscapeDataString(projectId);
        using var req = new HttpRequestMessage(HttpMethod.Get, q);
        var env = await SendJsonAsync<LearningInsightsEnvelope>(req, ct);
        return env?.Insights;
    }

    public async Task<IReadOnlyList<ReviewLearningEvent>> GetLearningEventsAsync(
        string? projectId = null,
        string? type = null,
        int take = 100,
        CancellationToken ct = default)
    {
        var parts = new List<string> { $"take={take}" };
        if (!string.IsNullOrWhiteSpace(projectId))
            parts.Add("projectId=" + Uri.EscapeDataString(projectId));
        if (!string.IsNullOrWhiteSpace(type))
            parts.Add("type=" + Uri.EscapeDataString(type));
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/learning/events?" + string.Join("&", parts));
        var env = await SendJsonAsync<LearningEventsEnvelope>(req, ct);
        return env?.Events ?? (IReadOnlyList<ReviewLearningEvent>)Array.Empty<ReviewLearningEvent>();
    }

    public sealed record UserSettingsEnvelope(bool Ok, UserSettingsDto? Settings, string? Message, string? Error);

    public async Task<UserSettingsDto?> GetUserSettingsAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/user/settings");
        var env = await SendJsonAsync<UserSettingsEnvelope>(req, ct);
        return env?.Settings;
    }

    /// <summary>
    /// Save/clear one or more personal provider keys. Provider ids are whatever
    /// SupportedModelCatalog + GetUserSettingsDtoAsync surface (grok/gemini/anthropic/fal/suno/
    /// aimusicapi/…) — this stays a plain dictionary pass-through rather than a named param per
    /// provider so a new catalog provider never needs a client-side signature change again.
    /// </summary>
    public async Task<UserSettingsDto?> UpdateUserSettingsAsync(
        IReadOnlyDictionary<string, string?> providerApiKeys,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/user/settings")
        {
            Content = JsonContent.Create(new UpdateUserSettingsRequest
            {
                ProviderApiKeys = new Dictionary<string, string?>(providerApiKeys, StringComparer.OrdinalIgnoreCase),
            }, options: JsonOpts),
        };
        var env = await SendJsonAsync<UserSettingsEnvelope>(req, ct);
        return env?.Settings;
    }

    public async Task<byte[]?> GetWipMovieBytesAsync(string projectId, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/projects/{Uri.EscapeDataString(projectId)}/movie/wip");
        var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Public demo gallery — films on YouTube only (no auth). sort=top|new.</summary>
    public async Task<List<DemoListItem>> ListDemosAsync(
        int take = 50,
        string sort = "top",
        CancellationToken ct = default)
    {
        var (demos, _) = await ListDemosDetailedAsync(take, sort, ct);
        return demos;
    }

    /// <summary>Gallery list plus YouTube channel sync diagnostics.</summary>
    public async Task<(List<DemoListItem> Demos, DemoYoutubeSyncInfo? YoutubeSync)> ListDemosDetailedAsync(
        int take = 50,
        string sort = "top",
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        var q = $"take={take}&sort={Uri.EscapeDataString(sort ?? "top")}";
        var dto = await _http.GetFromJsonAsync<DemoListEnvelope>(
            $"/api/demos?{q}", JsonOpts, ct);
        return (dto?.Demos ?? new List<DemoListItem>(), dto?.YoutubeSync);
    }

    /// <summary>Star a public demo (signed-in). Returns updated count.</summary>
    public async Task<(int Count, bool UpvotedByMe)> UpvoteDemoAsync(
        string demoId, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/demos/{Uri.EscapeDataString(demoId)}/upvote");
        var dto = await SendJsonAsync<DemoUpvoteResult>(req, ct)
                  ?? throw new InvalidOperationException("Upvote failed");
        if (!dto.Ok)
            throw new InvalidOperationException(dto.Error ?? "Upvote failed");
        return (dto.UpvoteCount, dto.UpvotedByMe);
    }

    /// <summary>Remove star from a public demo (signed-in).</summary>
    public async Task<(int Count, bool UpvotedByMe)> RemoveDemoUpvoteAsync(
        string demoId, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var req = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/demos/{Uri.EscapeDataString(demoId)}/upvote");
        var dto = await SendJsonAsync<DemoUpvoteResult>(req, ct)
                  ?? throw new InvalidOperationException("Remove star failed");
        if (!dto.Ok)
            throw new InvalidOperationException(dto.Error ?? "Remove star failed");
        return (dto.UpvoteCount, dto.UpvotedByMe);
    }

    /// <summary>
    /// Feature 11: fork the studio project behind a public demo film (signed-in).
    /// Returns the new project id under the current user.
    /// </summary>
    public async Task<DemoForkResult> ForkDemoProjectAsync(string demoId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/demos/{Uri.EscapeDataString(demoId)}/fork");
        var dto = await SendJsonAsync<DemoForkResult>(req, ct)
                  ?? throw new InvalidOperationException("Fork failed");
        if (!dto.Ok)
            throw new InvalidOperationException(dto.Error ?? "Fork failed");
        return dto;
    }

    public async Task<List<RankedBookCandidateDto>> GetRankedBookCandidatesAsync(
        string projectId, string charKey, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        var url = $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/book-candidates";
        var dto = await _http.GetFromJsonAsync<BookCandidateEnvelopeDto>(url, JsonOpts, ct);
        return dto?.Candidates ?? new List<RankedBookCandidateDto>();
    }

    public async Task<bool> SetCharacterBookRefsAsync(
        string projectId, string charKey, List<string> imagePaths, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        var url = $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/set-book-refs";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { ImagePaths = imagePaths }, options: JsonOpts),
        };
        using var resp = await _http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Admin: pull all uploads from the connected YouTube channel into the gallery.</summary>
    public async Task<(bool Ok, string? Message, string? Error, int Added, int Updated, int Total)> SyncYouTubeChannelDemosAsync(
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/demos/sync-youtube");
        var dto = await SendJsonAsync<DemoChannelSyncResult>(req, ct);
        return (
            dto?.Ok == true,
            dto?.Message,
            dto?.Error,
            dto?.Added ?? 0,
            dto?.Updated ?? 0,
            dto?.Total ?? 0);
    }

    /// <summary>Admin: put an existing YouTube video on the public gallery.</summary>
    public async Task<(bool Ok, string? Message, string? Error)> RegisterDemoFromYouTubeAsync(
        string youtubeIdOrUrl,
        string title,
        string? description = null,
        string? projectId = null,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/demos/from-youtube")
        {
            Content = JsonContent.Create(new
            {
                youtubeIdOrUrl,
                title,
                description,
                projectId,
            }, options: JsonOpts),
        };
        var dto = await SendJsonAsync<DemoFromYouTubeResult>(req, ct);
        return (dto?.Ok == true, dto?.Message, dto?.Error);
    }

    /// <summary>Admin list (any status). YouTube is the public gallery gate.</summary>
    public async Task<DemoAdminListEnvelope?> ListAdminDemosAsync(
        string? status = null,
        int take = 100,
        CancellationToken ct = default)
    {
        var q = $"/api/admin/demos?take={take}";
        if (!string.IsNullOrWhiteSpace(status))
            q += "&status=" + Uri.EscapeDataString(status.Trim());
        using var req = new HttpRequestMessage(HttpMethod.Get, q);
        return await SendJsonAsync<DemoAdminListEnvelope>(req, ct);
    }

    public async Task ReportDemoAsync(string demoId, string? note = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/demos/{Uri.EscapeDataString(demoId)}/report")
        {
            Content = JsonContent.Create(new { note }, options: JsonOpts),
        };
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, ct);
    }

    public async Task ReviewDemoAsync(
        string demoId,
        string status,
        string? note = null,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/admin/demos/{Uri.EscapeDataString(demoId)}/review")
        {
            Content = JsonContent.Create(new { status, note }, options: JsonOpts),
        };
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, ct);
    }

    /// <summary>Publish demo → YouTube upload; gallery lists once YoutubeId is set.</summary>
    public async Task<DemoPublishResult?> PublishDemoFromWipAsync(
        string projectId,
        string title,
        string? description = null,
        bool acceptedGuidelines = true,
        bool madeForKids = false,
        bool isAiSynthetic = true,
        string privacyStatus = "public",
        string? tags = null,
        bool replaceExisting = true,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/demos")
        {
            Content = JsonContent.Create(new
            {
                projectId,
                title,
                description,
                acceptedGuidelines,
                madeForKids,
                isAiSynthetic,
                privacyStatus,
                tags,
                replaceExisting,
            }, options: JsonOpts),
        };
        return await SendJsonAsync<DemoPublishResult>(req, ct);
    }

    /// <summary>Privacy-safe handle search (never returns emails). Signed-in only.</summary>
    public async Task<IReadOnlyList<string>> SearchUserHandlesAsync(string query, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        var q = (query ?? "").Trim();
        if (q.Length == 0) return Array.Empty<string>();
        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/users/search?q={Uri.EscapeDataString(q)}");
        ApplyAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            return Array.Empty<string>();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;
        // Support both { handles: [...] } and legacy bare array
        JsonElement arr;
        if (root.ValueKind == JsonValueKind.Array)
            arr = root;
        else if (root.TryGetProperty("handles", out var h) && h.ValueKind == JsonValueKind.Array)
            arr = h;
        else
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var el in arr.EnumerateArray())
        {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                list.Add(s.Trim());
        }
        return list;
    }

    /// <summary>Invite a collaborator to fork a project (by @handle or email). Owner/admin only.</summary>
    public async Task<SendInviteResult?> SendProjectInviteAsync(
        string projectId, string? targetHandle, string? targetEmail, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{Uri.EscapeDataString(projectId)}/invites")
        {
            Content = JsonContent.Create(new { ProjectId = projectId, TargetHandle = targetHandle, TargetEmail = targetEmail }, options: JsonOpts),
        };
        return await SendJsonAsync<SendInviteResult>(req, ct);
    }

    /// <summary>Accept an invite token (must be signed in) — forks the project under the caller.</summary>
    public async Task<AcceptInviteResult?> AcceptInviteAsync(string token, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/invites/accept")
        {
            Content = JsonContent.Create(new { Token = token }, options: JsonOpts),
        };
        return await SendJsonAsync<AcceptInviteResult>(req, ct);
    }

    public async Task DeleteDemoAsync(string demoId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/demos/{Uri.EscapeDataString(demoId)}");
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOkAsync(resp, ct);
    }

    /// <summary>Public demo stream URL (only works for approved demos unless admin/owner Bearer).</summary>
    public string DemoVideoUrl(string demoId)
        // Admin reviewing pending demos needs media token or session — attach short media token when available.
        => WithMediaTokenAndOrigin($"/api/demos/{Uri.EscapeDataString(demoId)}/video");

    public async Task<ProposeLearningRulesResult?> ProposeLearningRulesAsync(
        ProposeLearningRulesRequest body,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/learning/propose")
        {
            Content = JsonContent.Create(body, options: JsonOpts),
        };
        return await SendJsonAsync<ProposeLearningRulesResult>(req, ct);
    }

    public async Task<ReviewComparisonInsightsDto?> GetReviewComparisonAsync(string? projectId = null, CancellationToken ct = default)
    {
        var q = string.IsNullOrWhiteSpace(projectId) ? "" : $"?projectId={Uri.EscapeDataString(projectId)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/learning/review-comparison{q}");
        return await SendJsonAsync<ReviewComparisonInsightsDto>(req, ct);
    }

    public async Task<ReviewComparisonInsightsDto?> SynthesizePromptImprovementsAsync(string? projectId = null, CancellationToken ct = default)
    {
        var q = string.IsNullOrWhiteSpace(projectId) ? "" : $"?projectId={Uri.EscapeDataString(projectId)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/learning/synthesize-prompt-improvements{q}");
        return await SendJsonAsync<ReviewComparisonInsightsDto>(req, ct);
    }

    public async Task<ProposalChecklistDocument?> GetProposalChecklistAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/admin/learning/proposal-checklist");
        var env = await SendJsonAsync<ProposalChecklistEnvelope>(req, ct);
        return env?.Checklist;
    }

    public async Task<ProposalChecklistDocument?> ToggleProposalChecklistItemAsync(
        string id,
        bool reviewed,
        string? disposition = null,
        string? note = null,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/learning/proposal-checklist/toggle")
        {
            Content = JsonContent.Create(new ProposalChecklistToggleRequest
            {
                Id = id,
                Reviewed = reviewed,
                Disposition = disposition,
                Note = note,
            }, options: JsonOpts),
        };
        var env = await SendJsonAsync<ProposalChecklistEnvelope>(req, ct);
        return env?.Checklist;
    }

    public async Task<ProposalChecklistDocument?> SaveProposalChecklistAsync(
        ProposalChecklistUpsertRequest body,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/learning/proposal-checklist")
        {
            Content = JsonContent.Create(body, options: JsonOpts),
        };
        var env = await SendJsonAsync<ProposalChecklistEnvelope>(req, ct);
        return env?.Checklist;
    }

    public async Task<ProjectRulesDocument?> GetProjectRulesAsync(string projectId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/admin/learning/project-rules/{Uri.EscapeDataString(projectId)}");
        var env = await SendJsonAsync<ProjectRulesEnvelope>(req, ct);
        return env?.Rules;
    }

    public async Task<ProjectRulesDocument?> SuggestProjectRulesAsync(
        string projectId, int minFails = 3, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/admin/learning/project-rules/{Uri.EscapeDataString(projectId)}/suggest?minFails={minFails}");
        var env = await SendJsonAsync<ProjectRulesEnvelope>(req, ct);
        return env?.Rules;
    }

    public async Task<ProjectRulesDocument?> ApproveProjectRuleAsync(
        string projectId, string suggestionId, string? text = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/admin/learning/project-rules/{Uri.EscapeDataString(projectId)}/approve")
        {
            Content = JsonContent.Create(new ApproveProjectRuleRequest
            {
                SuggestionId = suggestionId,
                Text = text,
            }, options: JsonOpts),
        };
        var env = await SendJsonAsync<ProjectRulesEnvelope>(req, ct);
        return env?.Rules;
    }

    public async Task<ProjectRulesDocument?> RejectProjectRuleAsync(
        string projectId, string suggestionId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/admin/learning/project-rules/{Uri.EscapeDataString(projectId)}/reject")
        {
            Content = JsonContent.Create(new RejectProjectRuleRequest { SuggestionId = suggestionId }, options: JsonOpts),
        };
        var env = await SendJsonAsync<ProjectRulesEnvelope>(req, ct);
        return env?.Rules;
    }

    private sealed class LearningInsightsEnvelope
    {
        public bool Ok { get; set; }
        public LearningInsightsDto? Insights { get; set; }
    }
    private sealed class LearningEventsEnvelope
    {
        public bool Ok { get; set; }
        public List<ReviewLearningEvent>? Events { get; set; }
    }
    private sealed class ProposalChecklistEnvelope
    {
        public bool Ok { get; set; }
        public ProposalChecklistDocument? Checklist { get; set; }
    }
    private sealed class ProjectRulesEnvelope
    {
        public bool Ok { get; set; }
        public ProjectRulesDocument? Rules { get; set; }
    }

    public async Task AdminReleaseLockAsync(string resource, bool force = true, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/locks/release")
        {
            Content = JsonContent.Create(new AdminReleaseLockRequest { Resource = resource, Force = force }, options: JsonOpts),
        };
        await SendJsonAsync<object>(req, ct);
    }

    public async Task<TimingSeedResult?> PostAdminTimingTelemetrySeedAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/timing-telemetry/seed");
        return await SendJsonAsync<TimingSeedResult>(req, ct);
    }

    public async Task<TimingTelemetryTrendDto?> GetAdminTimingTelemetryTrendAsync(CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        return await _http.GetFromJsonAsync<TimingTelemetryTrendDto>("/api/admin/timing-telemetry/trend", JsonOpts, ct);
    }

    public sealed class TimingSeedResult
    {
        public bool Ok { get; set; }
        public string? Message { get; set; }
        public int Count { get; set; }
    }

    public sealed class TimingTelemetryTrendDto
    {
        public bool Ok { get; set; }
        public TimingCacheStatsDto? Stats { get; set; }
        public List<TimingTrendPointDto>? Trend { get; set; }
    }

    public sealed class TimingCacheStatsDto
    {
        public int TotalHits { get; set; }
        public int TotalMisses { get; set; }
        public double HitRatePercent { get; set; }
        public double MeanAbsoluteErrorSec { get; set; }
    }

    public sealed class TimingTrendPointDto
    {
        public string Timestamp { get; set; } = "";
        public int Hits { get; set; }
        public int Misses { get; set; }
        public double HitRatePercent { get; set; }
        public double MeanAbsoluteErrorSec { get; set; }
    }

    public async Task<GenerationErrorsDto?> GetAdminGenerationErrorsAsync(
        string? errorType = null,
        string? projectId = null,
        int take = 100,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        var qs = new List<string> { $"take={take}" };
        if (!string.IsNullOrWhiteSpace(errorType)) qs.Add($"errorType={Uri.EscapeDataString(errorType)}");
        if (!string.IsNullOrWhiteSpace(projectId)) qs.Add($"projectId={Uri.EscapeDataString(projectId)}");
        var url = "/api/admin/generation-errors?" + string.Join("&", qs);
        return await _http.GetFromJsonAsync<GenerationErrorsDto>(url, JsonOpts, ct);
    }

    public sealed class GenerationErrorsDto
    {
        public bool Ok { get; set; }
        public List<GenerationErrorRowDto>? Rows { get; set; }
    }

    public sealed class GenerationErrorRowDto
    {
        public long Id { get; set; }
        public string Ts { get; set; } = "";
        public string? UserId { get; set; }
        public string? ProjectId { get; set; }
        public string? JobId { get; set; }
        public int? Scene { get; set; }
        public int? Clip { get; set; }
        public string Stage { get; set; } = "";
        public string? Provider { get; set; }
        public string? Model { get; set; }
        public string ErrorType { get; set; } = "";
        public string? ErrorMessage { get; set; }
        public int? HttpStatus { get; set; }
        public int? RequestedCount { get; set; }
        public int? ReturnedCount { get; set; }
        public string? MissingIdsJson { get; set; }
        public int Attempt { get; set; }
        public bool Resolved { get; set; }
        public string? RequestSummary { get; set; }
        public string? ResponseSummary { get; set; }
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

    public async Task<ProjectsDto?> GetProjectsAsync(CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        return await _http.GetFromJsonAsync<ProjectsDto>("/api/projects", JsonOpts, ct);
    }

    public async Task ActivateProjectAsync(string projectId, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{Uri.EscapeDataString(projectId)}/activate")
        {
            Content = JsonContent.Create(new { }, options: JsonOpts)
        };
        await SendJsonAsync<object>(req, ct);
    }

    public async Task<ProjectsDto?> CreateProjectAsync(
        string name,
        string? title = null,
        string? studioPath = null,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/projects")
        {
            Content = JsonContent.Create(new { name, title, studioPath }, options: JsonOpts)
        };
        return await SendJsonAsync<ProjectsDto>(req, ct);
    }

    public async Task SetStudioPathAsync(
        string projectId,
        string studioPath,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/studio-path",
            new SetStudioPathRequest { StudioPath = studioPath },
            JsonOpts,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase ?? "Studio path update failed");
        }
    }

    
    public async Task<ProjectInfo?> RenameProjectAsync(
        string projectId,
        string newTitle,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/rename",
            new RenameProjectRequest { Title = newTitle, Name = newTitle },
            JsonOpts,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase ?? "Rename failed");
        }
        var body = await resp.Content.ReadFromJsonAsync<RenameProjectResponse>(JsonOpts, ct);
        if (body is null) return null;
        return new ProjectInfo
        {
            Id = body.ProjectId ?? projectId,
            Title = body.Title,
            Label = body.Label ?? body.Title,
        };
    }

    private sealed class RenameProjectResponse
    {
        public bool Ok { get; set; }
        public string? ProjectId { get; set; }
        public string? Title { get; set; }
        public string? Label { get; set; }
        public string? Message { get; set; }
        public string? Error { get; set; }
    }

public async Task<ProjectsDto?> DeleteProjectAsync(
        string projectId,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/projects/{Uri.EscapeDataString(projectId)}");
        return await SendJsonAsync<ProjectsDto>(req, ct);
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

    /// <summary>
    /// Prompt-based edit of an already-generated clip (xAI /v1/videos/edits) — its own job kind,
    /// same job-queue + live-progress pattern as <see cref="StartSceneGenAsync"/> (caller follows
    /// up with <see cref="GetJobAsync"/> to drive the progress card), not a blocking request.
    /// </summary>
    public async Task StartVideoEditAsync(
        string projectId,
        int scene,
        int clip,
        string prompt,
        string? model = null,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsJsonAsync(
            "/api/jobs/video-edit",
            new StartVideoEditRequest
            {
                ProjectId = projectId,
                Scene = scene,
                Clip = clip,
                Prompt = prompt,
                Model = model,
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
        string? videoModel = null,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsJsonAsync(
            "/api/jobs/gen-batch",
            new StartBatchGenRequest
            {
                ProjectId = projectId,
                Resolution = resolution,
                VideoModel = videoModel,
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

    /// <summary>Explicit multi-select regen of specific (scene, clip) pairs — always force-regens, ignoring on-disk state.</summary>
    public async Task<JobSnapshot?> StartClipBatchGenAsync(
        string projectId,
        IReadOnlyList<(int Scene, int Clip)> clips,
        string? resolution = null,
        CancellationToken ct = default)
        => await StartGenBatchJobAsync(
            "/api/jobs/gen-batch",
            new StartBatchGenRequest
            {
                ProjectId = projectId,
                Resolution = resolution,
                Clips = clips.Select(c => new ClipTarget { Scene = c.Scene, Clip = c.Clip }).ToList(),
            },
            ct);

    /// <summary>Shared POST for the job-starting endpoints that return a
    /// <see cref="GenBatchJobResponseDto"/>: sends the request, throws a best-effort error on
    /// failure, otherwise returns the started job snapshot.</summary>
    private async Task<JobSnapshot?> StartGenBatchJobAsync<TRequest>(
        string endpoint, TRequest request, CancellationToken ct)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsJsonAsync(endpoint, request, JsonOpts, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(raw) ?? $"{(int)resp.StatusCode}");
        var res = JsonSerializer.Deserialize<GenBatchJobResponseDto>(raw, JsonOpts);
        return res?.Job;
    }

    /// <summary>
    /// Server-side batch TTS for re-voice. Progress over SignalR (kind <c>speak-batch</c>);
    /// each finished line sets <see cref="JobSnapshot.ClientMediaUrl"/> for client media save.
    /// </summary>
    public async Task<JobSnapshot?> StartSpeakBatchAsync(
        StartSpeakBatchRequest request,
        CancellationToken ct = default)
        => await StartGenBatchJobAsync("/api/jobs/speak-batch", request, ct);

    /// <summary>
    /// Movie-wide "substitute my cloned voice" — walks every clip, synthesizes each line in the
    /// character's cloned voice, and maintains the persisted speech alignment. Tracked job
    /// (kind <c>voice-substitution</c>); per-line audio over <see cref="JobSnapshot.ClientMediaUrl"/>.
    /// </summary>
    public async Task<JobSnapshot?> StartVoiceSubstitutionAsync(
        StartVoiceSubstitutionRequest request,
        CancellationToken ct = default)
        => await StartGenBatchJobAsync("/api/jobs/voice-substitution", request, ct);

    /// <summary>Load the persisted per-clip speech alignment (null when never built).</summary>
    public async Task<ProjectVoiceAlignment?> GetVoiceAlignmentAsync(
        string projectId, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.GetAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/voice-alignment", ct);
        if (!resp.IsSuccessStatusCode) return null;
        var res = await resp.Content.ReadFromJsonAsync<VoiceAlignmentResponseDto>(JsonOpts, ct);
        return res?.Alignment;
    }

    /// <summary>Persist client-detected speech windows so a future substitution skips detection.</summary>
    public async Task PostVoiceAlignmentTimestampsAsync(
        string projectId, IReadOnlyList<ClipTimestampUpdate> updates, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/voice-alignment/timestamps",
            updates, JsonOpts, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? $"{(int)resp.StatusCode}");
        }
    }

    /// <summary>Transcribe an extracted audio segment via server-side Scribe (STT). Used to verify a
    /// detected window contains the expected narrator line (confident line↔window mapping); returns
    /// null on any failure.</summary>
    public async Task<TranscriptDto?> TranscribeSegmentAsync(
        byte[] audio, string fileName = "segment.wav", CancellationToken ct = default)
    {
        if (audio is null || audio.Length < 128) return null;
        SyncIdentityHeaders();
        try
        {
            using var form = new MultipartFormDataContent();
            var content = new ByteArrayContent(audio);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            form.Add(content, "file", string.IsNullOrWhiteSpace(fileName) ? "segment.wav" : fileName);
            using var resp = await _http.PostAsync("/api/transcribe", form, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<TranscriptDto>(JsonOpts, ct);
        }
        catch { return null; }
    }

    public sealed class TranscriptDto
    {
        public bool Ok { get; set; }
        public string? Text { get; set; }
        public string? LanguageCode { get; set; }
        public List<TranscriptWordDto>? Words { get; set; }
        public string? Error { get; set; }
    }

    public sealed class TranscriptWordDto
    {
        public string Text { get; set; } = "";
        public double Start { get; set; }
        public double End { get; set; }
        public string? Type { get; set; }
    }

    /// <summary>Load the cached voice-capture phrase set for a project (null if not built yet).</summary>
    public async Task<VoiceCapturePhrases?> GetVoiceCapturePhrasesAsync(string projectId, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        try
        {
            using var resp = await _http.GetAsync(
                $"/api/projects/{Uri.EscapeDataString(projectId)}/voice-capture/phrases", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var dto = await resp.Content.ReadFromJsonAsync<VoiceCapturePhrasesResponseDto>(JsonOpts, ct);
            return dto?.Phrases;
        }
        catch { return null; }
    }

    /// <summary>Persist the computed voice-capture phrase set (the once-per-book cache).</summary>
    public async Task<bool> SaveVoiceCapturePhrasesAsync(
        string projectId, VoiceCapturePhrases phrases, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        try
        {
            using var resp = await _http.PostAsJsonAsync(
                $"/api/projects/{Uri.EscapeDataString(projectId)}/voice-capture/phrases", phrases, JsonOpts, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private class VoiceCapturePhrasesResponseDto
    {
        public bool Ok { get; set; }
        public VoiceCapturePhrases? Phrases { get; set; }
    }

    /// <summary>Per-scene narrator lines from the blueprint (no dub needed) — lets the capture page
    /// build its phrase cache standalone.</summary>
    /// <param name="charKey">Which character's solo lines to collect. Defaults to the narrator
    /// (server-side "Character_Narrator" / name-contains-"narrator" heuristic) when omitted — pass
    /// an explicit character key to capture read-along material for a different speaking character.</param>
    public async Task<List<NarratorSceneLinesDto>> GetNarratorLinesAsync(string projectId, string? charKey = null, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        try
        {
            var url = $"/api/projects/{Uri.EscapeDataString(projectId)}/voice-capture/narrator-lines";
            if (!string.IsNullOrWhiteSpace(charKey))
                url += $"?charKey={Uri.EscapeDataString(charKey.Trim())}";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return new();
            var dto = await resp.Content.ReadFromJsonAsync<NarratorLinesResponseDto>(JsonOpts, ct);
            return dto?.Scenes ?? new();
        }
        catch { return new(); }
    }

    public sealed class NarratorSceneLinesDto
    {
        public int Scene { get; set; }
        public bool HasOtherSpeakers { get; set; }
        public List<string> Lines { get; set; } = new();
    }

    private class NarratorLinesResponseDto
    {
        public bool Ok { get; set; }
        public List<NarratorSceneLinesDto>? Scenes { get; set; }
    }

    // ── Dialogue-timing review (all speakers): script lines, cached STT comparison ──────────────

    /// <summary>All dialogue lines (every speaker) per scene from the blueprint — the "script" side.</summary>
    public async Task<List<DialogueSceneLinesDto>> GetDialogueLinesAsync(string projectId, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        try
        {
            using var resp = await _http.GetAsync(
                $"/api/projects/{Uri.EscapeDataString(projectId)}/dialogue/lines", ct);
            if (!resp.IsSuccessStatusCode) return new();
            var dto = await resp.Content.ReadFromJsonAsync<DialogueLinesResponseDto>(JsonOpts, ct);
            return dto?.Scenes ?? new();
        }
        catch { return new(); }
    }

    /// <summary>Load the cached dialogue-timing review (null if not built yet).</summary>
    public async Task<DialogueTimingDoc?> GetDialogueTimingAsync(string projectId, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        try
        {
            using var resp = await _http.GetAsync(
                $"/api/projects/{Uri.EscapeDataString(projectId)}/dialogue/timing", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var dto = await resp.Content.ReadFromJsonAsync<DialogueTimingResponseDto>(JsonOpts, ct);
            return dto?.Timing;
        }
        catch { return null; }
    }

    /// <summary>Persist one analyzed/edited scene into the dialogue-timing cache.</summary>
    public async Task<bool> SaveDialogueTimingSceneAsync(string projectId, DialogueTimingScene scene, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        try
        {
            using var resp = await _http.PostAsJsonAsync(
                $"/api/projects/{Uri.EscapeDataString(projectId)}/dialogue/timing/scene", scene, JsonOpts, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public sealed class DialogueSceneLinesDto
    {
        public int Scene { get; set; }
        public List<DialogueLineDto> Lines { get; set; } = new();
    }

    public sealed class DialogueLineDto
    {
        public int Clip { get; set; }
        public string Speaker { get; set; } = "";
        public string Text { get; set; } = "";
    }

    private class DialogueLinesResponseDto
    {
        public bool Ok { get; set; }
        public List<DialogueSceneLinesDto>? Scenes { get; set; }
    }

    private class DialogueTimingResponseDto
    {
        public bool Ok { get; set; }
        public DialogueTimingDoc? Timing { get; set; }
    }

    private class VoiceAlignmentResponseDto
    {
        public bool Ok { get; set; }
        public ProjectVoiceAlignment? Alignment { get; set; }
    }

    private class GenBatchJobResponseDto
    {
        public bool Ok { get; set; }
        public JobSnapshot? Job { get; set; }
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
        await EnsureOkAsync(resp, ct);
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

    public string ClipVideoUrl(string projectId, int sceneNumber, int clipNumber) =>
        BrowserMediaPath(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/clips/{clipNumber}/video");

    /// <summary>Structured end-credits card content (title/author/software/site) for deterministic
    /// client-side rendering of the credits clip.</summary>
    public async Task<CreditsContentDto?> GetCreditsContentAsync(string projectId, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<CreditsContentDto>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/credits-content", JsonOpts, ct);

    /// <summary>Archived prompt versions for one clip (for ClipPromptCompareViewer).</summary>
    public async Task<ClipPromptHistoryEnvelope?> GetClipPromptHistoryAsync(
        string projectId, int sceneNumber, int clipNumber, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<ClipPromptHistoryEnvelope>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/clips/{clipNumber}/prompt-history",
            JsonOpts,
            ct);

    public sealed class SceneGitHistoryEnvelope
    {
        public bool Ok { get; set; }
        public List<SceneCommitHistoryItem>? History { get; set; }
        public string? Error { get; set; }
    }

    public async Task<SceneGitHistoryEnvelope?> GetSceneGitHistoryAsync(
        string projectId, int sceneNumber, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        return await _http.GetFromJsonAsync<SceneGitHistoryEnvelope>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/history",
            JsonOpts,
            ct);
    }

    public sealed class SceneRevertEnvelope : IStatusEnvelope
    {
        public bool Ok { get; set; }
        public string? Message { get; set; }
        public string? Error { get; set; }
    }

    public async Task<SceneRevertEnvelope> RevertSceneToCommitAsync(
        string projectId, int sceneNumber, string commitHash, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/revert/{Uri.EscapeDataString(commitHash)}",
            null,
            ct);
        return await ReadEnvelopeAsync<SceneRevertEnvelope>(resp, ct);
    }

    public sealed class UncommittedStatusEnvelope
    {
        public bool Ok { get; set; }
        public UncommittedStatusDto? Status { get; set; }
        public string? Error { get; set; }
    }

    public async Task<UncommittedStatusEnvelope?> GetProjectUncommittedStatusAsync(
        string projectId, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        return await _http.GetFromJsonAsync<UncommittedStatusEnvelope>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/git/status",
            JsonOpts,
            ct);
    }

    public async Task<SceneRevertEnvelope> CommitProjectChangesAsync(
        string projectId, string message, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/git/commit",
            new { message },
            JsonOpts,
            ct);
        return await ReadEnvelopeAsync<SceneRevertEnvelope>(resp, ct);
    }

    public sealed class ClipVersionsEnvelope
    {
        public bool Ok { get; set; }
        public List<ClipVersionItem>? Versions { get; set; }
        public string? Error { get; set; }
    }

    public async Task<ClipVersionsEnvelope?> GetClipVersionsAsync(
        string projectId, int sceneNumber, int clipNumber, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        return await _http.GetFromJsonAsync<ClipVersionsEnvelope>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/clips/{clipNumber}/versions",
            JsonOpts,
            ct);
    }

    public sealed class ClipMediaStatusEnvelope
    {
        public bool Ok { get; set; }
        public bool OnServer { get; set; }
        public bool OnClient { get; set; }
        public string? Sha256 { get; set; }
        public long ClientSizeBytes { get; set; }
        public long ServerSizeBytes { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>Where the *active* clip's bytes currently live (server/client/both) and their
    /// registered size/hash — used to check a local blob is still current before playing it.</summary>
    public async Task<ClipMediaStatusEnvelope?> GetClipMediaStatusAsync(
        string projectId, int sceneNumber, int clipNumber, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        return await _http.GetFromJsonAsync<ClipMediaStatusEnvelope>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/clips/{clipNumber}/media-status",
            JsonOpts,
            ct);
    }

    public async Task<SceneRevertEnvelope> PromoteClipVersionAsync(
        string projectId, int sceneNumber, int clipNumber, string versionId, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/clips/{clipNumber}/versions/{Uri.EscapeDataString(versionId)}/promote",
            null,
            ct);
        return await ReadEnvelopeAsync<SceneRevertEnvelope>(resp, ct);
    }

    public async Task<SceneRevertEnvelope> SoftDeleteClipVersionAsync(
        string projectId, int sceneNumber, int clipNumber, string versionId, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.DeleteAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/clips/{clipNumber}/versions/{Uri.EscapeDataString(versionId)}",
            ct);
        return await ReadEnvelopeAsync<SceneRevertEnvelope>(resp, ct);
    }

    public async Task<ClipVersionsEnvelope?> GetTrashClipVersionsAsync(
        string projectId, int sceneNumber, int clipNumber, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        return await _http.GetFromJsonAsync<ClipVersionsEnvelope>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/clips/{clipNumber}/versions/trash",
            JsonOpts,
            ct);
    }

    public async Task<SceneRevertEnvelope> RestoreClipVersionAsync(
        string projectId, int sceneNumber, int clipNumber, string versionId, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/clips/{clipNumber}/versions/{Uri.EscapeDataString(versionId)}/restore",
            null,
            ct);
        return await ReadEnvelopeAsync<SceneRevertEnvelope>(resp, ct);
    }

    public async Task<SceneRevertEnvelope> EmptyClipTrashAsync(
        string projectId, int sceneNumber, int clipNumber, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/clips/{clipNumber}/versions/trash/empty",
            null,
            ct);
        return await ReadEnvelopeAsync<SceneRevertEnvelope>(resp, ct);
    }

    public sealed class MusicVersionsEnvelope
    {
        public bool Ok { get; set; }
        public List<MusicVersionItem>? Versions { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>Audio take history for a scene — the audio equivalent of GetClipVersionsAsync.</summary>
    public async Task<MusicVersionsEnvelope?> GetMusicVersionsAsync(
        string projectId, int sceneNumber, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        return await _http.GetFromJsonAsync<MusicVersionsEnvelope>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/music-versions",
            JsonOpts,
            ct);
    }

    public async Task<SceneRevertEnvelope> PromoteMusicVersionAsync(
        string projectId, int sceneNumber, string takeId, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/music-versions/{Uri.EscapeDataString(takeId)}/promote",
            null,
            ct);
        return await ReadEnvelopeAsync<SceneRevertEnvelope>(resp, ct);
    }

    public async Task<SceneRevertEnvelope> SoftDeleteMusicVersionAsync(
        string projectId, int sceneNumber, string takeId, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.DeleteAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/music-versions/{Uri.EscapeDataString(takeId)}",
            ct);
        return await ReadEnvelopeAsync<SceneRevertEnvelope>(resp, ct);
    }

    public async Task<MusicVersionsEnvelope?> GetTrashMusicVersionsAsync(
        string projectId, int sceneNumber, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        return await _http.GetFromJsonAsync<MusicVersionsEnvelope>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/music-versions/trash",
            JsonOpts,
            ct);
    }

    public async Task<SceneRevertEnvelope> RestoreMusicVersionAsync(
        string projectId, int sceneNumber, string takeId, CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/music-versions/{Uri.EscapeDataString(takeId)}/restore",
            null,
            ct);
        return await ReadEnvelopeAsync<SceneRevertEnvelope>(resp, ct);
    }

    public string CompositeVideoUrl(string projectId, int sceneNumber) =>
        BrowserMediaPath(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{sceneNumber}/composite");

    /// <summary>Stream URL for the WIP full movie (range requests enabled; login via access_token).</summary>
    public string WipMovieUrl(string projectId) =>
        BrowserMediaPath($"/api/projects/{Uri.EscapeDataString(projectId)}/movie/wip");

    /// <summary>Public share URL path for WIP (no login). Creates or reuses an active token.</summary>
    public async Task<WipShareLinkDto?> CreateWipShareLinkAsync(
        string projectId,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{Uri.EscapeDataString(projectId)}/movie/wip/share");
        return await SendJsonAsync<WipShareLinkDto>(req, ct);
    }

    public sealed class WipShareLinkDto
    {
        public bool Ok { get; set; }
        public string? Token { get; set; }
        public string? Path { get; set; }
        public string? Url { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public string? Error { get; set; }
    }

    public async Task<WipMovieMetaDto?> GetWipMovieMetaAsync(
        string projectId,
        CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<WipMovieMetaDto>(
                $"/api/projects/{Uri.EscapeDataString(projectId)}/movie/wip/meta",
                JsonOpts,
                ct);
        }
        catch (JsonException)
        {
            // Tolerate older servers that returned url as bool, etc.
            using var resp = await _http.GetAsync(
                $"/api/projects/{Uri.EscapeDataString(projectId)}/movie/wip/meta", ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            return new WipMovieMetaDto
            {
                Ok = root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                Exists = root.TryGetProperty("exists", out var ex) && ex.ValueKind == JsonValueKind.True,
                Stale = root.TryGetProperty("stale", out var st) && st.ValueKind == JsonValueKind.True,
                CanBuild = root.TryGetProperty("canBuild", out var cb) && cb.ValueKind == JsonValueKind.True,
                Reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null,
                ProjectId = root.TryGetProperty("projectId", out var p) ? p.GetString() : projectId,
                Path = root.TryGetProperty("path", out var path) ? path.GetString() : null,
                Bytes = root.TryGetProperty("bytes", out var b) && b.TryGetInt64(out var bv) ? bv : 0,
                UpdatedAt = root.TryGetProperty("updatedAt", out var u) ? u.GetString() : null,
                Url = root.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
                    ? urlEl.GetString()
                    : null,
                StaleScenes = root.TryGetProperty("staleScenes", out var ss) && ss.ValueKind == JsonValueKind.Array
                    ? ss.EnumerateArray().Where(x => x.TryGetInt32(out _)).Select(x => x.GetInt32()).ToList()
                    : new List<int>(),
            };
        }
    }

    /// <summary>
    /// Register film_build.v1 after client stitch (EDL + studio.sha256). Non-throwing wrapper returns ok/filmId.
    /// </summary>
    public async Task<(bool Ok, string? FilmId, string? Error)> RegisterFilmBuildAsync(
        string projectId,
        object body,
        CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync(
                $"/api/projects/{Uri.EscapeDataString(projectId)}/film-build",
                body,
                JsonOpts,
                ct);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct);
            var ok = json.ValueKind == JsonValueKind.Object
                     && json.TryGetProperty("ok", out var okEl)
                     && okEl.ValueKind == JsonValueKind.True;
            string? filmId = null;
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("filmId", out var fid)
                && fid.ValueKind == JsonValueKind.String)
                filmId = fid.GetString();
            string? error = null;
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.String)
                error = err.GetString();
            if (!resp.IsSuccessStatusCode && error is null)
                error = resp.ReasonPhrase ?? $"HTTP {(int)resp.StatusCode}";
            return (ok, filmId, error);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public async Task<YouTubeStatusDto?> GetYouTubeStatusAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<YouTubeStatusDto>("/api/youtube/status", JsonOpts, ct);

    /// <summary>Admin-only. Returns the Google consent URL to navigate the browser to.</summary>
    /// <param name="returnTo">Where OAuth should land (e.g. /admin/demos).</param>
    public async Task<string> GetYouTubeConnectUrlAsync(string? returnTo = null, CancellationToken ct = default)
    {
        var path = "/api/youtube/connect-url";
        if (!string.IsNullOrWhiteSpace(returnTo))
            path += "?returnTo=" + Uri.EscapeDataString(returnTo.Trim());
        using var resp = await _http.GetAsync(path, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(body) ?? resp.ReasonPhrase);
        var dto = JsonSerializer.Deserialize<YouTubeConnectUrlDto>(body, JsonOpts);
        return dto?.Url ?? "";
    }

    public async Task DisconnectYouTubeAsync(CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync("/api/youtube/disconnect", null, ct);
        await EnsureOkAsync(resp, ct);
    }

    public async Task StartYouTubeUploadAsync(StartYouTubeUploadRequest req, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/api/jobs/youtube-upload", req, JsonOpts, ct);
        await EnsureOkAsync(resp, ct);
    }

    public async Task<YouTubeUploadInfo?> GetYouTubeUploadInfoAsync(string projectId, CancellationToken ct = default)
    {
        var dto = await _http.GetFromJsonAsync<YouTubeUploadInfoDto>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/movie/youtube",
            JsonOpts,
            ct);
        return dto?.Upload;
    }

    public async Task<AdaptationDto?> GetAdaptationAsync(string projectId, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<AdaptationDto>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/adaptation",
            JsonOpts,
            ct);

    public async Task StartStage1Async(StartStage1Request req, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/api/jobs/stage1", req, JsonOpts, ct);
        await EnsureOkAsync(resp, ct);
    }

    public async Task StartStage2Async(StartStage2Request req, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/api/jobs/stage2", req, JsonOpts, ct);
        await EnsureOkAsync(resp, ct);
    }

    public async Task RegisterMediaAsync(string projectId, MediaRegisterRequest body, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/projects/{Uri.EscapeDataString(projectId)}/media/register")
        {
            Content = JsonContent.Create(body, options: JsonOpts),
        };
        await SendJsonAsync<object>(req, ct);
    }

    /// <returns>Queued job snapshot (includes JobId for polling).</returns>
    public async Task<JobSnapshot?> StartClipAutoReviewAsync(
        string projectId,
        int scene,
        int clip,
        IReadOnlyList<ClipAutoReviewClientFrame>? frames = null,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsJsonAsync(
            "/api/jobs/clip-auto-review",
            new StartClipAutoReviewRequest
            {
                ProjectId = projectId,
                Scene = scene,
                Clip = clip,
                Frames = frames?.ToList(),
            },
            JsonOpts,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase ?? "Auto-review failed");
        }

        try
        {
            var env = await resp.Content.ReadFromJsonAsync<JobStartEnvelope>(JsonOpts, ct);
            return env?.Job;
        }
        catch
        {
            return null;
        }
    }

    private sealed class JobStartEnvelope
    {
        public bool Ok { get; set; }
        public JobSnapshot? Job { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>
    /// Server batch no longer samples frames. Prefer client loop: sample + StartClipAutoReviewAsync.
    /// </summary>
    public async Task StartClipAutoReviewBatchAsync(
        string projectId,
        int? scene = null,
        bool onlyMissing = true,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsJsonAsync(
            "/api/jobs/clip-auto-review-batch",
            new StartClipAutoReviewBatchRequest
            {
                ProjectId = projectId,
                Scene = scene,
                OnlyMissing = onlyMissing,
            },
            JsonOpts,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase ?? "Batch auto-review failed");
        }
    }

    /// <summary>
    /// Poll until a specific job reaches a terminal status (sequential client batch).
    /// When <paramref name="jobId"/> is null, waits for the primary mine job to leave running/queued.
    /// </summary>
    public async Task<JobSnapshot?> WaitForJobTerminalAsync(
        string? jobId = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default,
        TimeSpan? pollInterval = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(8));
        var delay = pollInterval ?? TimeSpan.FromMilliseconds(600);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            JobSnapshot? snap = null;
            if (!string.IsNullOrWhiteSpace(jobId))
            {
                try
                {
                    var detail = await GetJobByIdAsync(jobId, ct);
                    snap = detail?.Job;
                }
                catch
                {
                    snap = null;
                }
            }
            else
            {
                var jobs = await GetJobAsync(ct);
                snap = jobs?.Job;
            }

            if (snap is not null && snap.IsFinished && snap.Status != "idle")
                return snap;
            await Task.Delay(delay, ct);
        }
        throw new TimeoutException("Timed out waiting for job to finish.");
    }

    public async Task<ReviewIndexDocument?> GetReviewIndexAsync(
        string projectId,
        bool rebuild = false,
        CancellationToken ct = default)
    {
        var q = rebuild ? "?rebuild=true" : "";
        using var resp = await _http.GetAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/review/index{q}",
            ct);
        await EnsureOkAsync(resp, ct);
        var dto = await resp.Content.ReadFromJsonAsync<ReviewIndexEnvelope>(JsonOpts, ct);
        return dto?.Index;
    }

    private sealed class ReviewIndexEnvelope
    {
        public bool Ok { get; set; }
        public ReviewIndexDocument? Index { get; set; }
    }

    public async Task<ClipAutoReviewDraft?> GetClipAutoReviewDraftAsync(
        string projectId,
        int scene,
        int clip,
        CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{scene}/clips/{clip}/auto-review",
            ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        await EnsureOkAsync(resp, ct);
        var dto = await resp.Content.ReadFromJsonAsync<ClipAutoReviewDraftEnvelope>(JsonOpts, ct);
        return dto?.Draft;
    }

    public async Task<MovieAutoReviewEnvelope?> GetMovieReviewReportAsync(string projectId, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"/api/projects/{Uri.EscapeDataString(projectId)}/review/movie", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<MovieAutoReviewEnvelope>(JsonOpts, ct);
    }

    public async Task<MovieAutoReviewEnvelope?> ReviewMovieAsync(
        string projectId,
        IReadOnlyList<MovieAutoReviewKeyframe> keyframes,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/review/movie",
            new { Keyframes = keyframes.ToList() },
            JsonOpts,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase ?? "Movie review failed");
        }
        return await resp.Content.ReadFromJsonAsync<MovieAutoReviewEnvelope>(JsonOpts, ct);
    }

    public async Task<ClipDialogueVerificationResult?> VerifyClipDialogueAsync(
        string projectId,
        int scene,
        int clip,
        byte[]? videoBytes = null,
        bool force = false,
        CancellationToken ct = default)
    {
        HttpContent? content = null;
        if (videoBytes is { Length: > 0 })
        {
            var form = new MultipartFormDataContent();
            var byteContent = new ByteArrayContent(videoBytes);
            byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
            form.Add(byteContent, "video", $"scene_{scene:D2}_clip_{clip:D2}.mp4");
            content = form;
        }

        var url = $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{scene}/clips/{clip}/verify-dialogue";
        if (force) url += "?force=true";

        using var resp = await _http.PostAsync(
            url,
            content,
            ct);
        if (!resp.IsSuccessStatusCode) return null;
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct);
        if (doc.TryGetProperty("result", out var rEl))
            return JsonSerializer.Deserialize<ClipDialogueVerificationResult>(rEl.GetRawText(), JsonOpts);
        return null;
    }

    public async Task<bool> UploadClipAsync(
        string projectId,
        int scene,
        int clip,
        byte[] videoBytes,
        CancellationToken ct = default)
    {
        var (ok, _) = await UploadClipWithResultAsync(projectId, scene, clip, videoBytes, ct).ConfigureAwait(false);
        return ok;
    }

    /// <summary>Same upload, but surfaces WHY it failed (status code + response body) instead of
    /// collapsing every failure to a bare false — a caller that treats "uploaded" as "safe to report
    /// success" (e.g. the credits-card render) needs to know when that's not true.</summary>
    public async Task<(bool Ok, string? Error)> UploadClipWithResultAsync(
        string projectId,
        int scene,
        int clip,
        byte[] videoBytes,
        CancellationToken ct = default)
    {
        if (videoBytes is not { Length: > 0 }) return (false, "No video bytes to upload");
        try
        {
            using var form = new MultipartFormDataContent();
            using var byteContent = new ByteArrayContent(videoBytes);
            byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
            form.Add(byteContent, "video", $"scene_{scene:D2}_clip_{clip:D2}.mp4");

            var url = ClipUploadUrl(projectId, scene, clip);
            using var resp = await _http.PostAsync(url, form, ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (false, $"HTTP {(int)resp.StatusCode}: {(string.IsNullOrWhiteSpace(body) ? resp.ReasonPhrase : body)}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Relative upload URL for a clip slot. Pass <paramref name="kind"/>="extend-source"
    /// for the video-extend continuation-source upload (see
    /// ClientMediaFolderService.PrepareExtendSourceAsync) — the server writes that to a distinct,
    /// single-use path instead of replacing the clip's own official video.</summary>
    public string ClipUploadUrl(string projectId, int scene, int clip, string? kind = null) =>
        $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{scene}/clips/{clip}/upload" +
        (string.IsNullOrEmpty(kind) ? "" : $"?kind={Uri.EscapeDataString(kind)}");

    /// <summary>Queues background-music generation for a scene (job-tracked, client saves the
    /// resulting audio segment(s) — same pattern as clip/credits generation). Returns the queued
    /// job snapshot (JobId) so a caller doing several of these in a row can wait for each to
    /// finish via <see cref="WaitForJobTerminalAsync"/> instead of flooding the per-user job
    /// queue (see MaxQueuePerUser in FilmJobService.EnsureCanStart).</summary>
    public async Task<JobSnapshot?> StartSceneMusicGenAsync(
        string projectId,
        int scene,
        string? model = null,
        bool isVocal = false,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/jobs/scene-music")
        {
            Content = JsonContent.Create(new StartSceneMusicGenRequest
            {
                ProjectId = projectId,
                Scene = scene,
                Model = model,
                IsVocal = isVocal,
            }, options: JsonOpts),
        };
        return await SendJsonAsync<JobStartEnvelope>(req, ct) is { } env ? env.Job : null;
    }

    public async Task ApplyClipAutoReviewAsync(
        string projectId,
        int scene,
        int clip,
        IReadOnlyList<ClipAutoReviewApplyItem> items,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{scene}/clips/{clip}/auto-review/apply",
            new ApplyClipAutoReviewRequest
            {
                ProjectId = projectId,
                Scene = scene,
                Clip = clip,
                Items = items.ToList(),
            },
            JsonOpts,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase ?? "Apply failed");
        }
    }

    /// <summary>
    /// Rebuild project-local ARTIFACTS.md + artifact_index.json + telemetry snapshots for manual Claude review.
    /// </summary>
    public async Task<ArtifactIndexResult> RebuildArtifactIndexAsync(
        string projectId,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/artifacts/index",
            content: null,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase ?? "Artifact index failed");
        }

        var dto = await resp.Content.ReadFromJsonAsync<ArtifactIndexResponse>(JsonOpts, ct)
                  ?? new ArtifactIndexResponse();
        return new ArtifactIndexResult
        {
            ReadyForManualFinalReview = dto.ReadyForManualFinalReview,
            MissingRequired = dto.MissingRequired ?? new List<string>(),
            Stats = dto.Index?.Stats,
        };
    }

    private sealed class ArtifactIndexResponse
    {
        public bool Ok { get; set; }
        public bool ReadyForManualFinalReview { get; set; }
        public List<string>? MissingRequired { get; set; }
        public ArtifactIndexDto? Index { get; set; }
    }

    private sealed class ArtifactIndexDto
    {
        public Dictionary<string, object?>? Stats { get; set; }
    }

    public sealed class ArtifactIndexResult
    {
        public bool ReadyForManualFinalReview { get; set; }
        public List<string> MissingRequired { get; set; } = new();
        public Dictionary<string, object?>? Stats { get; set; }
    }

    private sealed class ClipAutoReviewDraftEnvelope
    {
        public bool Ok { get; set; }
        public ClipAutoReviewDraft? Draft { get; set; }
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
        await EnsureOkAsync(resp, ct);
    }

    public async Task AddClipAsync(
        string projectId,
        int scene,
        ClipEditRequest fields,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{scene}/clips",
            fields,
            JsonOpts,
            ct);
        await EnsureOkAsync(resp, ct);
    }

    public async Task UpdateClipAsync(
        string projectId,
        int scene,
        int clip,
        ClipEditRequest fields,
        CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{scene}/clips/{clip}",
            fields,
            JsonOpts,
            ct);
        await EnsureOkAsync(resp, ct);
    }

    public async Task DeleteClipAsync(
        string projectId,
        int scene,
        int clip,
        CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{scene}/clips/{clip}",
            ct);
        await EnsureOkAsync(resp, ct);
    }

    public async Task ApproveSceneAsync(
        string projectId,
        int scene,
        string note = "",
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/scenes/{scene}/approve",
            new SceneApproveRequest
            {
                ProjectId = projectId,
                Scene = scene,
                Note = note,
            },
            JsonOpts,
            ct);
        await EnsureOkAsync(resp, ct);
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
        await EnsureOkAsync(resp, ct);
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

    public async Task<VisualMediumDto?> GetVisualMediumAsync(string projectId, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/visual-medium", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(body) ?? resp.ReasonPhrase ?? "visual-medium failed");
        return JsonSerializer.Deserialize<VisualMediumDto>(body, JsonOpts);
    }

    public async Task<VisualMediumDto?> SetVisualMediumAsync(
        string projectId, string visualMedium, CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/visual-medium",
            new { visualMedium },
            JsonOpts,
            ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(body) ?? resp.ReasonPhrase ?? "set visual-medium failed");
        return JsonSerializer.Deserialize<VisualMediumDto>(body, JsonOpts);
    }

    /// <summary>
    /// Re-skin the current screenplay draft to a visual medium (descriptive layer only).
    /// Pass a medium to override the stored preference, or null to use it.
    /// </summary>
    public async Task<DraftEditResultDto?> ReskinScreenplayAsync(
        string projectId, string? visualMedium = null, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/adaptation/reskin",
            new { visualMedium },
            JsonOpts,
            ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(body) ?? resp.ReasonPhrase ?? "re-skin failed");
        return JsonSerializer.Deserialize<DraftEditResultDto>(body, JsonOpts);
    }

    /// <summary>
    /// Enrich the current screenplay draft's descriptive layer (Scene Embellishment) for the stored medium.
    /// </summary>
    public async Task<DraftEditResultDto?> EmbellishScreenplayAsync(
        string projectId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/adaptation/embellish", content: null, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(body) ?? resp.ReasonPhrase ?? "enrichment failed");
        return JsonSerializer.Deserialize<DraftEditResultDto>(body, JsonOpts);
    }

    /// <summary>
    /// Trim the screenplay toward the project's current target runtime. Set the target first via
    /// <see cref="SetFilmRuntimeAsync"/> (or the FilmLengthCard).
    /// </summary>
    public async Task<DraftEditResultDto?> TrimScreenplayAsync(
        string projectId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/adaptation/trim", content: null, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(body) ?? resp.ReasonPhrase ?? "trim failed");
        return JsonSerializer.Deserialize<DraftEditResultDto>(body, JsonOpts);
    }

    public async Task<FilmRuntimeDto?> GetFilmRuntimeAsync(string projectId, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/film-runtime", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(body) ?? resp.ReasonPhrase);
        return JsonSerializer.Deserialize<FilmRuntimeDto>(body, JsonOpts);
    }

    public async Task<FilmRuntimeDto?> SetFilmRuntimeAsync(
        string projectId, int targetMinutes, CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/film-runtime",
            new { targetMinutes },
            JsonOpts,
            ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(body) ?? resp.ReasonPhrase);
        return JsonSerializer.Deserialize<FilmRuntimeDto>(body, JsonOpts);
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

    /// <summary>
    /// Spend by provider for a project (default: signed-in user only).
    /// Pass <paramref name="allUsers"/> true only as admin for full project totals.
    /// </summary>
    public async Task<CostByProviderDto?> GetCostByProviderAsync(
        string projectId,
        bool allUsers = false,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        var qs = allUsers ? "?all=true" : "";
        return await _http.GetFromJsonAsync<CostByProviderDto>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/cost/by-provider{qs}",
            JsonOpts,
            ct);
    }

    /// <summary>Signed-in user's total / by-project / by-vendor spend.</summary>
    public async Task<UserSpendSummaryDto?> GetMySpendAsync(
        string? projectId = null,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        var qs = string.IsNullOrWhiteSpace(projectId)
            ? ""
            : $"?projectId={Uri.EscapeDataString(projectId)}";
        var dto = await _http.GetFromJsonAsync<MySpendDto>($"/api/me/spend{qs}", JsonOpts, ct);
        return dto?.Summary;
    }

    public sealed class MySpendDto
    {
        public bool Ok { get; set; }
        public UserSpendSummaryDto? Summary { get; set; }
    }

    public sealed class UserSpendSummaryDto
    {
        public string UserId { get; set; } = "";
        public int TotalCalls { get; set; }
        public double TotalListUsd { get; set; }
        public double TotalChargeUsd { get; set; }
        public List<ProjectSpendRowDto> ByProject { get; set; } = new();
        public Dictionary<string, ProviderCostStatsDto> ByProvider { get; set; } = new();
        public Dictionary<string, CategoryCostStatsDto> ByCategory { get; set; } = new();
    }

    public sealed class ProjectSpendRowDto
    {
        public string ProjectId { get; set; } = "";
        public int Calls { get; set; }
        public double ListUsd { get; set; }
        public double ChargeUsd { get; set; }
    }

    public sealed class CostByProviderDto
    {
        public bool Ok { get; set; }
        public string? ProjectId { get; set; }
        public string? UserId { get; set; }
        public string? Scope { get; set; }
        public ApiCostByProviderStatsDto? Stats { get; set; }
    }

    public sealed class ApiCostByProviderStatsDto
    {
        public int TotalCalls { get; set; }
        public double TotalUsd { get; set; }
        public double TotalListUsd { get; set; }
        public double TotalChargeUsd { get; set; }
        public Dictionary<string, ProviderCostStatsDto> ByProvider { get; set; } = new();
    }

    public sealed class ProviderCostStatsDto
    {
        public string Provider { get; set; } = "unknown";
        public int Count { get; set; }
        public double TotalUsd { get; set; }
        public double TotalListUsd { get; set; }
        public double TotalChargeUsd { get; set; }
        public Dictionary<string, CategoryCostStatsDto> ByCategory { get; set; } = new();
    }

    public sealed class CategoryCostStatsDto
    {
        public string Category { get; set; } = "other";
        public int Count { get; set; }
        public double TotalUsd { get; set; }
        public double TotalListUsd { get; set; }
        public double TotalChargeUsd { get; set; }
        public double AvgUsd { get; set; }
    }

    /// <summary>Resolution already used by this project's on-disk clips, or null if none yet.</summary>
    public async Task<string?> GetResolutionLockAsync(string projectId, CancellationToken ct = default)
    {
        var dto = await _http.GetFromJsonAsync<ResolutionLockDto>(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/resolution-lock",
            JsonOpts,
            ct);
        return dto?.Locked;
    }

    public async Task<CostBackfillDto?> BackfillCostAsync(string projectId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/cost/backfill",
            new { },
            ct);
        await EnsureOkAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<CostBackfillDto>(JsonOpts, ct);
    }

    public async Task<ConfigDto?> GetConfigAsync(string projectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("project required");
        using var resp = await _http.GetAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/config",
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? $"{(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        return await resp.Content.ReadFromJsonAsync<ConfigDto>(JsonOpts, ct);
    }

    public async Task<(bool Ok, string? Opened, string? Error)> OpenFolderAsync(
        string? path = null,
        string? projectId = null,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/system/open-folder")
        {
            Content = JsonContent.Create(new OpenFolderRequest { Path = path, ProjectId = projectId }, options: JsonOpts),
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadFromJsonAsync<OpenFolderResponse>(JsonOpts, ct);
        if (!resp.IsSuccessStatusCode || body is null || !body.Ok)
            return (false, null, body?.Error ?? "Could not open folder on server.");
        return (true, body.Opened ?? path, null);
    }

    public async Task<(bool Ok, bool IsRemote, string? Opened, string? Editor, string? VideoUrl, string? Error)> OpenInExternalEditorAsync(
        string projectId,
        int? sceneNumber = null,
        int? clipNumber = null,
        string? editorName = null,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/system/open-editor")
        {
            Content = JsonContent.Create(new OpenEditorRequest
            {
                ProjectId = projectId,
                SceneNumber = sceneNumber,
                ClipNumber = clipNumber,
                EditorName = editorName
            }, options: JsonOpts),
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadFromJsonAsync<OpenEditorResponse>(JsonOpts, ct);
        if (!resp.IsSuccessStatusCode || body is null || !body.Ok)
            return (false, body?.IsRemote ?? false, null, null, body?.VideoUrl, body?.Error ?? "Could not open external video editor.");
        return (true, false, body.Opened, body.Editor, body.VideoUrl, null);
    }

    /// <summary>Master model catalog (id → endpoint + required keys).</summary>
    public async Task<IReadOnlyList<SupportedModelDto>> GetSupportedModelsAsync(
        string? capability = null,
        CancellationToken ct = default)
    {
        var path = string.IsNullOrWhiteSpace(capability)
            ? "/api/models"
            : $"/api/models?capability={Uri.EscapeDataString(capability)}";
        using var resp = await _http.GetAsync(path, ct);
        if (!resp.IsSuccessStatusCode) return Array.Empty<SupportedModelDto>();
        var dto = await resp.Content.ReadFromJsonAsync<SupportedModelsResponse>(JsonOpts, ct);
        return dto?.Models ?? (IReadOnlyList<SupportedModelDto>)Array.Empty<SupportedModelDto>();
    }

    private sealed class SupportedModelsResponse
    {
        public bool Ok { get; set; }
        public List<SupportedModelDto>? Models { get; set; }
    }

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
        await EnsureOkAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<ConfigDto>(JsonOpts, ct);
    }

    public async Task<ExtractCastResultDto?> ExtractCastFromScreenplayAsync(
        string projectId,
        bool force = true,
        string model = "",
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

    /// <summary>Server-side HttpClient origin (often loopback). Do not use for browser &lt;img&gt; src.</summary>
    public string ApiBaseUrl =>
        (_http.BaseAddress?.ToString() ?? "").TrimEnd('/');

    /// <summary>
    /// Origin (or empty for same-origin) that the browser should use for media.
    /// Empty → root-relative paths so Railway/public host works; not 127.0.0.1.
    /// </summary>
    public string BrowserMediaOrigin
    {
        get
        {
            var configured = (_opts.BrowserMediaBaseUrl ?? "").Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(configured))
                return configured;
            // Unified host (Docker/Railway): leave empty → /api/... hits the public page origin.
            // Explicit non-loopback BaseUrl (unusual) can still prefix media.
            if (!IsLoopbackOrigin(ApiBaseUrl))
                return ApiBaseUrl;
            return "";
        }
    }

    /// <summary>
    /// Turn API media paths into browser-safe URLs for &lt;img&gt;/&lt;video src&gt;.
    /// Prefer root-relative on the unified host so images are not pointed at 127.0.0.1.
    /// </summary>
    public string? AbsolutizeMediaUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
        {
            // Rewrite accidental loopback absolutes from older server responses.
            if (TryRewriteLoopbackToBrowser(url, out var fixedUrl))
                return fixedUrl;
            return url;
        }

        var path = url.StartsWith('/') ? url : "/" + url.TrimStart('/');
        return BrowserMediaPath(path);
    }

    /// <summary>
    /// Browser URL for a root-relative API media path.
    /// Uses a short-lived media token (?mt=), never the full session JWT.
    /// </summary>
    public string BrowserMediaPath(string rootRelativePath)
    {
        if (string.IsNullOrWhiteSpace(rootRelativePath))
            return BrowserMediaOrigin;
        var path = rootRelativePath.StartsWith('/')
            ? rootRelativePath
            : "/" + rootRelativePath.TrimStart('/');
        return WithMediaTokenAndOrigin(path);
    }

    /// <summary>Attaches the short-lived media token to a root-relative path (queuing a background
    /// refresh when stale — never the full session JWT, which would leak via access logs/history)
    /// and prefixes the browser media origin. Shared by the media-URL builders.</summary>
    private string WithMediaTokenAndOrigin(string path)
    {
        // Prefer short-lived media token. Never put the session JWT in the query string
        // (access logs, browser history, Referer).
        if (HasFreshMediaToken())
        {
            path += (path.Contains('?', StringComparison.Ordinal) ? "&" : "?")
                    + "mt=" + Uri.EscapeDataString(_mediaToken!);
        }
        else
        {
            QueueMediaTokenRefreshIfNeeded();
        }
        var origin = BrowserMediaOrigin;
        return string.IsNullOrEmpty(origin) ? path : origin + path;
    }

    private static bool IsLoopbackOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return true;
        try
        {
            var u = new Uri(origin.Contains("://", StringComparison.Ordinal) ? origin : "http://" + origin);
            return u.IsLoopback
                   || string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(u.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(u.Host, "::1", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private bool TryRewriteLoopbackToBrowser(string absoluteUrl, out string fixedUrl)
    {
        fixedUrl = absoluteUrl;
        try
        {
            var u = new Uri(absoluteUrl);
            if (!u.IsLoopback &&
                !string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(u.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
                return false;
            fixedUrl = BrowserMediaPath(u.PathAndQuery);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string CharacterRefUrl(string projectId, string charKey) =>
        BrowserMediaPath(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/ref");

    public string CharacterVariantUrl(string projectId, string charKey, int index) =>
        BrowserMediaPath(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/variants/{index}");

    public string CharacterBookRefUrl(string projectId, string charKey, int index) =>
        BrowserMediaPath(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/bookrefs/{index}");

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
        await EnsureOkAsync(resp, ct);
    }

    /// <summary>
    /// Film-pipeline voice sample: short video with VOICE LOCK → cached MP3 (audio only).
    /// </summary>
    public async Task StartVoicePreviewAsync(
        StartVoicePreviewRequest req,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            "/api/jobs/voice-preview",
            req,
            JsonOpts,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase ?? "Voice preview failed");
        }
    }

    public async Task<VoicePreviewStatusDto?> GetVoicePreviewStatusAsync(
        string projectId,
        string charKey,
        string? voiceProfile = null,
        string? voiceLabel = null,
        string? sampleText = null,
        CancellationToken ct = default)
    {
        var q = new List<string>();
        if (!string.IsNullOrWhiteSpace(voiceProfile))
            q.Add("voiceProfile=" + Uri.EscapeDataString(voiceProfile));
        if (!string.IsNullOrWhiteSpace(voiceLabel))
            q.Add("voiceLabel=" + Uri.EscapeDataString(voiceLabel));
        if (!string.IsNullOrWhiteSpace(sampleText))
            q.Add("sampleText=" + Uri.EscapeDataString(sampleText));
        var qs = q.Count > 0 ? "?" + string.Join("&", q) : "";
        using var resp = await _http.GetAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/voice/audio/status{qs}",
            ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<VoicePreviewStatusDto>(JsonOpts, ct);
    }

    /// <summary>URL for cached film voice MP3 (audio player only).</summary>
    public string CharacterVoiceAudioUrl(string projectId, string charKey, long cacheBust = 0)
    {
        var url = BrowserMediaPath(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/voice/audio");
        if (cacheBust > 0)
            url += (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "t=" + cacheBust;
        return url;
    }

    /// <summary>Upload mic/file audio as voice-clone template for a character.</summary>
    public async Task UploadVoiceCloneSampleAsync(
        string projectId,
        string charKey,
        Stream content,
        string fileName,
        CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var contentType = ext switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" or ".aac" => "audio/mp4",
            ".ogg" => "audio/ogg",
            _ => "audio/webm",
        };
        await UploadFileFormAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/voice/clone-sample",
            content, fileName, contentType, ct);
    }

    /// <summary>Shared multipart file upload (single "file" part): posts the stream with the given
    /// content type and throws a best-effort error on failure. Callers resolve the content type.</summary>
    private async Task UploadFileFormAsync(
        string endpoint, Stream content, string fileName, string contentType, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(streamContent, "file", fileName);

        using var resp = await _http.PostAsync(endpoint, form, ct);
        await EnsureOkAsync(resp, ct);
    }

    public string CharacterVoiceCloneSampleUrl(string projectId, string charKey, long cacheBust = 0)
    {
        var url = BrowserMediaPath(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/voice/clone-sample");
        if (cacheBust > 0)
            url += (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "t=" + cacheBust;
        return url;
    }

    public async Task DeleteVoiceCloneSampleAsync(
        string projectId,
        string charKey,
        CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/voice/clone-sample",
            ct);
        await EnsureOkAsync(resp, ct);
    }

    /// <summary>
    /// Create provider clone from saved sample (or seed a demo sample), store voice id on the character, optional TTS preview.
    /// </summary>
    public async Task<VoiceApplyDto> ApplyVoiceCloneAsync(
        string projectId,
        string charKey,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/voice/apply-clone",
            new { },
            JsonOpts,
            ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        var body = JsonSerializer.Deserialize<VoiceApplyDto>(raw, JsonOpts)
                   ?? new VoiceApplyDto { Ok = false, Error = "Empty response" };
        if (!resp.IsSuccessStatusCode)
        {
            body.Ok = false;
            if (string.IsNullOrWhiteSpace(body.Error))
                body.Error = TryError(raw) ?? resp.ReasonPhrase ?? "Apply clone failed";
        }
        return body;
    }

    /// <summary>
    /// TTS with the character's stored clone (or explicit voice id). Returns base64 audio and/or proxy URL.
    /// </summary>
    public async Task<SpeakVoiceDto> SpeakVoiceAsync(
        string projectId,
        string charKey,
        string text,
        string? voiceId = null,
        string? model = null,
        CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/voice/speak",
            new { text, voiceId, model },
            JsonOpts,
            ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        var body = JsonSerializer.Deserialize<SpeakVoiceDto>(raw, JsonOpts)
                   ?? new SpeakVoiceDto { Ok = false, Error = "Empty response" };
        if (!resp.IsSuccessStatusCode)
        {
            body.Ok = false;
            if (string.IsNullOrWhiteSpace(body.Error))
                body.Error = TryError(raw) ?? resp.ReasonPhrase ?? "Speech synthesis failed";
        }
        return body;
    }

    public async Task<VoiceCatalogDto?> ListProviderVoicesAsync(CancellationToken ct = default)
    {
        SyncIdentityHeaders();
        return await _http.GetFromJsonAsync<VoiceCatalogDto>("/api/voices", JsonOpts, ct);
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
        await EnsureOkAsync(resp, ct);
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
        await EnsureOkAsync(resp, ct);
    }

    public async Task<bool> AugmentProjectMusicAsync(string projectId, string? model = null, CancellationToken ct = default)
    {
        var url = $"/api/projects/{Uri.EscapeDataString(projectId)}/augment-music";
        if (!string.IsNullOrWhiteSpace(model))
        {
            url += $"?model={Uri.EscapeDataString(model)}";
        }
        using var resp = await _http.PostAsync(url, null, ct).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;
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
                return att.Deserialize<AttachCharacterPlatesResult>(JsonOpts);
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
        await EnsureOkAsync(resp, ct);
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
        await EnsureOkAsync(resp, ct);
    }

    /// <summary>Background prepare + book→Fountain (or adapt-only when <paramref name="skipPrepare"/>).</summary>
    public async Task StartBookImportAsync(
        string projectId,
        bool skipPrepare = false,
        bool forceExtract = true,
        bool forceVision = false,
        bool autoVision = true,
        string model = "",
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            "/api/jobs/book-import",
            new StartBookImportRequest
            {
                ProjectId = projectId,
                SkipPrepare = skipPrepare,
                ForceExtract = forceExtract,
                ForceVision = forceVision,
                AutoVision = autoVision,
                Model = model,
            },
            JsonOpts,
            ct);
        await EnsureOkAsync(resp, ct);
    }

    /// <param name="overrideStyle">When true, lock the portrait even if the style classifier says its
    /// medium doesn't match the project (intentional mixed-media — the user's creative choice wins).</param>
    /// <param name="overrideReason">Why the user overrode the classifier: ai_wrong | user_preference |
    /// other. Recorded in AI-call telemetry — distinguishes a classifier defect from a creative choice.</param>
    public async Task LockCharacterVariantAsync(
        string projectId,
        string charKey,
        int index,
        bool overrideStyle = false,
        string? overrideReason = null,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/lock-variant",
            new { index, overrideStyle, overrideReason },
            ct);
        await EnsureOkAsync(resp, ct);
    }

    public async Task LockCharacterBookRefAsync(
        string projectId,
        string charKey,
        int index,
        bool overrideStyle = false,
        string? overrideReason = null,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/lock-bookref",
            new { index, overrideStyle, overrideReason },
            ct);
        await EnsureOkAsync(resp, ct);
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
        await EnsureOkAsync(resp, ct);
    }

    /// <summary>Upload and lock an operator-provided character reference image.</summary>
    public async Task UploadCharacterRefAsync(
        string projectId,
        string charKey,
        Stream content,
        string fileName,
        CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var contentType = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/png",
        };
        await UploadFileFormAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(charKey)}/upload-ref",
            content, fileName, contentType, ct);
    }

    /// <summary>Common shape of the API's small mutation envelopes: a success flag and an
    /// optional error message. Lets <see cref="ReadEnvelopeAsync{T}"/> populate both generically.</summary>
    public interface IStatusEnvelope
    {
        bool Ok { get; set; }
        string? Error { get; set; }
    }

    /// <summary>Standard mutation-response handling: on success deserialize the envelope (defaulting
    /// to Ok=true when the body is empty); on failure read the error body and surface it as Ok=false
    /// with a best-effort message. Shared by the version/commit/revert endpoints.</summary>
    private async Task<T> ReadEnvelopeAsync<T>(HttpResponseMessage resp, CancellationToken ct)
        where T : class, IStatusEnvelope, new()
    {
        if (resp.IsSuccessStatusCode)
        {
            var res = await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct);
            return res ?? new T { Ok = true };
        }
        var err = await resp.Content.ReadAsStringAsync(ct);
        return new T { Ok = false, Error = TryError(err) ?? resp.ReasonPhrase };
    }

    /// <summary>Standard mutation failure handling: if the response is not success, read the error
    /// body and throw an <see cref="InvalidOperationException"/> with a best-effort message (falling
    /// back to the reason phrase). Shared by the void-returning mutation endpoints.</summary>
    private async Task EnsureOkAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var err = await resp.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(TryError(err) ?? resp.ReasonPhrase);
    }

    private static string? TryError(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                    return e.GetString();
                if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    var msg = m.GetString();
                    if (msg?.Contains("Application failed to respond", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return "The server request timed out (502 Bad Gateway). The AI generation task took longer than 60 seconds on Railway. Please try again.";
                    }
                    return msg;
                }
            }
        }
        catch { /* ignore */ }
        return json.Length > 200 ? json[..200] : json;
    }

    public async Task<ProjectMediaSyncResult?> GetProjectMediaSyncListAsync(string projectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return null;
        try
        {
            return await _http.GetFromJsonAsync<ProjectMediaSyncResult>($"/api/projects/{Uri.EscapeDataString(projectId.Trim())}/media/sync", JsonOpts, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }


    public async Task<byte[]?> GetCharacterImageAsync(string projectId, string characterKey, string? imageKind = null, CancellationToken ct = default)
    {
        var kind = string.IsNullOrWhiteSpace(imageKind) ? "portrait" : imageKind.Trim();
        var url = $"/api/projects/{Uri.EscapeDataString(projectId)}/characters/{Uri.EscapeDataString(characterKey)}/image?kind={Uri.EscapeDataString(kind)}";
        try { return await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false); }
        catch { return null; }
    }
}

public sealed class ProjectsDto
{
    public bool Ok { get; set; }
    public ProjectInfo? Active { get; set; }
    public List<ProjectInfo> Projects { get; set; } = new();
}

public sealed class VoiceApplyDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public string? ProviderId { get; set; }
    public string? ProviderVoiceId { get; set; }
    public string? ModelId { get; set; }
    public bool UsedMock { get; set; }
    public string? PreviewUrl { get; set; }
    public string? VoiceLabel { get; set; }
    public double? EstimatedUsd { get; set; }
}

public sealed class SpeakVoiceDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public string? ClientUrl { get; set; }
    public string? AudioBase64 { get; set; }
    public string? ContentType { get; set; }
    public string? FileExtension { get; set; }
    public string? VoiceId { get; set; }
    public int CharacterCount { get; set; }
    public double? EstimatedUsd { get; set; }
    public bool UsedMock { get; set; }
}

public sealed class VoiceCatalogDto
{
    public bool Ok { get; set; }
    public string? Provider { get; set; }
    public bool Configured { get; set; }
    public List<VoiceCatalogItemDto> Voices { get; set; } = new();
}

public sealed class VoiceCatalogItemDto
{
    public string ProviderVoiceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Category { get; set; }
    public string? PreviewUrl { get; set; }
    public bool IsCloned { get; set; }
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
    public VolumeDiskStatusDto? Disk { get; set; }
    public List<VolumeDiskSnapshotDto>? DiskHistory { get; set; }
    public List<AdminLockDto> Locks { get; set; } = new();
    public int ApiInFlight { get; set; }
    public int CapacityRejects { get; set; }
    public int LockConflicts { get; set; }
    public AdminHttpTrafficDto? Http { get; set; }
    public PageToMovie.Core.Models.LoadSimLiveStateDto? LoadSim { get; set; }
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

public sealed class MovieAutoReviewEnvelope
{
    public bool Ok { get; set; }
    public MovieAutoReviewReport? Report { get; set; }
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

public sealed class AdminUsersCreditsResponse
{
    public bool Ok { get; set; }
    public AdminCreditsOverviewDto? Overview { get; set; }
}

public sealed class ForgotPasswordResponse
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}

public sealed class TestEmailResponse
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public string? SenderType { get; set; }
    public bool ResendKeyResolved { get; set; }
    public Dictionary<string, bool>? CheckedEnvs { get; set; }
}

public sealed class AdminGrantCreditsResponse
{
    public bool Ok { get; set; }
    public UserCreditSummaryDto? User { get; set; }
}

public sealed class AdminUserActionResultDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public UserCreditSummaryDto? User { get; set; }
}

public sealed class AdminDeleteUserResultDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public string? UserId { get; set; }
    public string? Username { get; set; }
    public int DeletedProjects { get; set; }
    public int DeletedDemos { get; set; }
    public List<string>? ProjectErrors { get; set; }
}

public sealed class AdminProjectImportResultDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public ProjectInfo? Active { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
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
    public string? ProjectDir { get; set; }
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

public sealed class YouTubeStatusDto
{
    public bool Ok { get; set; }
    public bool Configured { get; set; }
    public bool Connected { get; set; }
}

public sealed class DemoFromYouTubeResult
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}

public sealed class DemoChannelSyncResult
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Total { get; set; }
    public bool Skipped { get; set; }
}

public sealed class YouTubeConnectUrlDto
{
    public bool Ok { get; set; }
    public string? Url { get; set; }
}

public sealed class YouTubeUploadInfoDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public YouTubeUploadInfo? Upload { get; set; }
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

public sealed class VisualMediumDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public string? VisualMedium { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public List<VisualMediumOptionDto>? Options { get; set; }
}

public sealed class VisualMediumOptionDto
{
    public string? Id { get; set; }
    public string? Label { get; set; }
}

public sealed class DraftEditResultDto
{
    public bool Ok { get; set; }
    /// <summary>True when the edit was applied and saved (false = kept original / no-op).</summary>
    public bool Applied { get; set; }
    public string? ProjectId { get; set; }
    public string? Message { get; set; }
    public int SceneCountBefore { get; set; }
    public int SceneCountAfter { get; set; }
    public ScreenplayStatus? Screenplay { get; set; }
    public string? Error { get; set; }
}

public sealed class FilmRuntimeDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public bool HasBookText { get; set; }
    public int NaturalMinutes { get; set; }
    public int TargetMinutes { get; set; }
    public string? Mode { get; set; }
    public int? TextWords { get; set; }
    public string? BookKind { get; set; }
    public string? Source { get; set; }
    public string? Message { get; set; }
    public AdaptationStatus? Adaptation { get; set; }
}

public sealed class ResolutionLockDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public string? Locked { get; set; }
}

public sealed class CostBackfillDto
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public CostBackfillResult? Backfill { get; set; }
}

public sealed class MediaTokenDto
{
    public bool Ok { get; set; }
    public string? Token { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? TokenUse { get; set; }
    public int? Minutes { get; set; }
    public string? Error { get; set; }
}

public sealed class DemoYoutubeSyncInfo
{
    public DateTimeOffset? LastSuccessUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class DemoListEnvelope
{
    [System.Text.Json.Serialization.JsonPropertyName("youtubeSync")]
    public DemoYoutubeSyncInfo? YoutubeSync { get; set; }
    public bool Ok { get; set; }
    public List<DemoListItem> Demos { get; set; } = new();
}

public sealed class DemoAdminListEnvelope
{
    public bool Ok { get; set; }
    public string? Status { get; set; }
    public int PendingCount { get; set; }
    public List<DemoListItem> Demos { get; set; } = new();
}

public sealed class DemoListItem
{
    public string? Category { get; set; }
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? ProjectId { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long SizeBytes { get; set; }
    public string? Status { get; set; }
    public int ReportCount { get; set; }
    public List<string>? ReportNotes { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
    public string? VideoPath { get; set; }
    public int UpvoteCount { get; set; }
    public bool UpvotedByMe { get; set; }
    /// <summary>Studio project still exists — gallery can offer Fork.</summary>
    public bool CanFork { get; set; }
    public string? YoutubeId { get; set; }
    public string? YoutubeUrl { get; set; }
    public ulong? YoutubeLikeCount { get; set; }
    public ulong? YoutubeViewCount { get; set; }
    public string? VisibilityMode { get; set; } = "Private";
    public bool IsForkable => string.Equals(VisibilityMode, "Open", StringComparison.OrdinalIgnoreCase);
    public string? YoutubeUploadStatus { get; set; }
    public string? YoutubeUploadError { get; set; }
    public ulong TotalStars => (ulong)Math.Max(0, UpvoteCount) + (YoutubeLikeCount ?? 0);
}

public sealed class ForkableStoryDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? OwnerUserId { get; set; }
}

public sealed class CheckpointDto
{
    public string CommitHash { get; set; } = "";
    public string? Author { get; set; }
    public string? Message { get; set; }
    public DateTime CommittedAt { get; set; }
}

public sealed class DemoForkResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? ProjectId { get; set; }
    public string? Title { get; set; }
    public string? ParentProjectId { get; set; }
    public string? DemoId { get; set; }
    public string? Message { get; set; }
}

public sealed class DemoUpvoteResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public int UpvoteCount { get; set; }
    public bool UpvotedByMe { get; set; }
}

public sealed class DemoPublishResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>Legacy; always false — admin content queue is retired.</summary>
    public bool PendingReview { get; set; }
    /// <summary>True until YouTube id is set; gallery lists only after upload finishes.</summary>
    public bool AwaitingYouTube { get; set; }
    /// <summary>True when an existing public demo for the project was updated (YouTube V2 replace).</summary>
    public bool ReplacedExisting { get; set; }
    public string? Message { get; set; }
    public DemoPublishItem? Demo { get; set; }
}

public sealed class DemoPublishItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? ProjectId { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long SizeBytes { get; set; }
    public string? Status { get; set; }
    public string? VideoPath { get; set; }
    public string? PagePath { get; set; }
}

public sealed class ClipPromptHistoryEnvelope
{
    public bool Ok { get; set; }
    public ClipPromptVersionDto? Current { get; set; }
    public List<ClipPromptVersionDto> History { get; set; } = new();
}

public sealed class ClipPromptVersionDto
{
    public DateTimeOffset? TimestampUtc { get; set; }
    public string? Prompt { get; set; }
    public string? VideoRelativePath { get; set; }
}

public sealed class SendInviteResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? InviteUrl { get; set; }
    public bool Delivered { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class AcceptInviteResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? ProjectId { get; set; }
    public string? Title { get; set; }
}

public sealed class RankedBookCandidateDto
{
    public string Name { get; set; } = "";
    public string PathRel { get; set; } = "";
    public string Url { get; set; } = "";
    public int Page { get; set; }
    public double Score { get; set; }
    public string Description { get; set; } = "";
    public bool IsSelected { get; set; }
}

public sealed class BookCandidateEnvelopeDto
{
    public bool Ok { get; set; }
    public List<RankedBookCandidateDto>? Candidates { get; set; }
}

public sealed class SyncOriginResultDto
{
    public bool Ok { get; set; }
    public bool Success { get; set; }
    public bool HasConflicts { get; set; }
    public string? CommitHash { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}

public sealed class ProjectPushResultDto
{
    public bool Ok { get; set; }
    public string? Branch { get; set; }
    public string? CommitHash { get; set; }
    public string? HistoryUrl { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}

public sealed class ProjectMediaSyncResult
{
    public bool Ok { get; set; }
    public string? ProjectId { get; set; }
    public List<ProjectMediaSyncFile>? Files { get; set; }
}

public sealed class ProjectMediaSyncFile
{
    public string RelativePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public bool IsMp4 { get; set; }
    public string? StreamUrl { get; set; }
}

public sealed class ModelsCatalogResponse
{
    public bool Ok { get; set; }
    public string CatalogPath { get; set; } = "";
    public string RawJson { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class ModelsCatalogSaveResponse
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string Error { get; set; } = "";
    public int ModelsCount { get; set; }
}

public sealed class ModelsCatalogValidateResponse
{
    public bool Ok { get; set; }
    public int ErrorCount { get; set; }
    public List<string>? Errors { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}

public sealed class CatalogUpdateScanClientEnvelope
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public CatalogUpdateScanClientResult? Result { get; set; }
}

public sealed class CatalogUpdateScanClientResult
{
    public string CheckedAtUtc { get; set; } = "";
    public CatalogUpdateSummaryDto Summary { get; set; } = new();
    public List<CatalogModelProbeDto> Models { get; set; } = new();
    public List<CatalogNewModelHintDto> NewModels { get; set; } = new();
    public List<string> DiscoveryNotes { get; set; } = new();
}

public sealed class CatalogUpdateSummaryDto
{
    public int ModelsScanned { get; set; }
    public int UnchangedFields { get; set; }
    public int ChangedFields { get; set; }
    public int NotFoundFields { get; set; }
    public int NewModels { get; set; }
}

public sealed class CatalogModelProbeDto
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Capability { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public bool LabMode { get; set; }
    public List<CatalogFieldProbeDto> Fields { get; set; } = new();
}

public sealed class CatalogFieldProbeDto
{
    public string Status { get; set; } = "";
    public string Field { get; set; } = "";
    public string? CatalogValue { get; set; }
    public string? LiveValue { get; set; }
    public string? Message { get; set; }
    public string? SourceUrl { get; set; }
}

public sealed class CatalogNewModelHintDto
{
    public string Id { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string SuggestedCapability { get; set; } = "Chat";
    public string Source { get; set; } = "";
    public bool LabMode { get; set; } = true;
    public string? LabNotes { get; set; }
}


public sealed class BookCacheAdminDto
{
    public bool Ok { get; set; }
    public long BookCount { get; set; }
    public long ArtifactCount { get; set; }
    public long ProviderFileCount { get; set; }
    public long TotalBookBytes { get; set; }
    public List<BookCacheAdminBookDto>? Books { get; set; }
    public List<BookCacheAdminArtifactDto>? RecentArtifacts { get; set; }
}

public sealed class BookCacheAdminBookDto
{
    public string BookId { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string BookTitle { get; set; } = "Untitled Book";
    public string Projects { get; set; } = "";
    public int ByteCount { get; set; }
    public string CreatedAt { get; set; } = "";
    public int ArtifactCount { get; set; }
    public int AccessLinkCount { get; set; }
    public string? Provider { get; set; }
    public string? ProviderFileId { get; set; }
    public long? FileExpiresAtUnix { get; set; }
    public string? LastResponseId { get; set; }
    public string? ProviderFileUpdatedAt { get; set; }
}

public sealed class BookCacheAdminArtifactDto
{
    public string ArtifactId { get; set; } = "";
    public string BookId { get; set; } = "";
    public string ArtifactKind { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string PromptVersion { get; set; } = "";
    public double Temperature { get; set; }
    public string CreatedAt { get; set; } = "";
    public int ContentBytes { get; set; }
}
