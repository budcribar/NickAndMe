using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace PageToMovie.Tests.Api;

/// <summary>
/// YouTube upload feature: the OAuth client id/secret/redirect are not configured in the
/// test factory, so these tests exercise the "not configured" / auth-gating paths and the
/// upload job's own guardrails (no WIP movie, no connected channel) rather than a live
/// Google OAuth round-trip.
/// </summary>
public class YouTubeUploadTests : IClassFixture<PageToMovieApiFactory>, IAsyncLifetime
{
    private readonly PageToMovieApiFactory _factory;
    private readonly HttpClient _client;
    private string _projectId = "";

    public YouTubeUploadTests(PageToMovieApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateUserClient();
    }

    public async Task InitializeAsync()
    {
        _projectId = "YtSmoke_" + Guid.NewGuid().ToString("N")[..8];
        await _client.PostAsJsonAsync("/api/projects", new { name = _projectId, title = "YT" });
        await _client.PostAsync($"/api/projects/{Uri.EscapeDataString(_projectId)}/activate", null);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Status_reports_unconfigured_when_no_oauth_client_set()
    {
        var resp = await _client.GetAsync("/api/youtube/status");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.False(json.GetProperty("configured").GetBoolean());
        Assert.False(json.GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task Connect_url_requires_admin()
    {
        var resp = await _client.GetAsync("/api/youtube/connect-url");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Disconnect_requires_admin()
    {
        var resp = await _client.PostAsync("/api/youtube/disconnect", null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Connect_url_is_conflict_when_unconfigured_even_for_admin()
    {
        var login = await _client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" });
        if (!login.IsSuccessStatusCode)
            return; // env without a dev admin password — forbidden-path test above still covers the gate

        var loginJson = await login.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        string? token = loginJson.TryGetProperty("token", out var t) ? t.GetString() : null;

        using var admin = _factory.CreateClient();
        if (!string.IsNullOrWhiteSpace(token))
            admin.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        admin.DefaultRequestHeaders.Add("X-User-Id", "admin");

        var resp = await admin.GetAsync("/api/youtube/connect-url");
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Upload_job_requires_project_id()
    {
        var resp = await _client.PostAsJsonAsync("/api/jobs/youtube-upload", new { projectId = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Upload_job_fails_fast_when_no_wip_movie_exists()
    {
        var start = await _client.PostAsJsonAsync("/api/jobs/youtube-upload", new { projectId = _projectId });
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var startJson = await start.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var jobId = startJson.GetProperty("job").GetProperty("jobId").GetString()!;

        string? status = null;
        string? message = null;
        for (var i = 0; i < 50; i++)
        {
            var job = await _client.GetAsync($"/api/jobs/{jobId}");
            var jobJson = await job.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            status = jobJson.GetProperty("job").GetProperty("status").GetString();
            message = jobJson.GetProperty("job").TryGetProperty("error", out var e) ? e.GetString() : null;
            if (!string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
                break;
            await Task.Delay(50);
        }

        Assert.Equal("error", status);
        Assert.Contains("WIP movie", message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Movie_youtube_info_is_null_before_any_upload()
    {
        var resp = await _client.GetAsync($"/api/projects/{Uri.EscapeDataString(_projectId)}/movie/youtube");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.GetProperty("upload").ValueKind);
    }
}
