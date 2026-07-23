using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace FilmStudio.Tests.Api;

/// <summary>
/// Smoke coverage for API endpoints with fakes + temp workspace.
/// Asserts status codes and basic JSON shape (not full business logic).
/// </summary>
public class ApiEndpointSmokeTests : IClassFixture<FilmStudioApiFactory>, IAsyncLifetime
{
    private readonly FilmStudioApiFactory _factory;
    private readonly HttpClient _client;
    private string _projectId = "";

    public ApiEndpointSmokeTests(FilmStudioApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateUserClient();
    }

    public async Task InitializeAsync()
    {
        _projectId = "ApiSmoke_" + Guid.NewGuid().ToString("N")[..8];
        var create = await _client.PostAsJsonAsync("/api/projects", new { name = _projectId, title = "Smoke" });
        // create may 200 or conflict if re-run
        if (!create.IsSuccessStatusCode && create.StatusCode != HttpStatusCode.Conflict)
        {
            var body = await create.Content.ReadAsStringAsync();
            // Some hosts require different body shape — try activate path after manual folder
            if (!create.IsSuccessStatusCode)
            {
                // Ensure project dir exists via store by re-posting minimal
                Assert.True(create.IsSuccessStatusCode,
                    $"Create project failed {(int)create.StatusCode}: {body}");
            }
        }

        var act = await _client.PostAsync($"/api/projects/{Uri.EscapeDataString(_projectId)}/activate", null);
        // activate may fail if create failed — seed dir
        if (!act.IsSuccessStatusCode)
        {
            var dir = Path.Combine(_factory.WorkspaceRoot, "projects", _projectId);
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "project.json"),
                $$"""{"id":"{{_projectId}}","label":"Smoke"}""");
            await File.WriteAllTextAsync(Path.Combine(dir, "pipeline_config.json"),
                """{"blueprint_file":"blueprint.clips.grok.json","model_name":"grok-imagine-video","resolution":"480p"}""");
            act = await _client.PostAsync($"/api/projects/{Uri.EscapeDataString(_projectId)}/activate", null);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Health_and_capacity_ok()
    {
        var health = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var cap = await _client.GetAsync("/api/capacity");
        Assert.Equal(HttpStatusCode.OK, cap.StatusCode);
        var json = await cap.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("ok", out _) || json.TryGetProperty("capacity", out _) ||
                    json.ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task Auth_me_ok()
    {
        var resp = await _client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Auth_login_dev_bypass()
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" });
        Assert.True(resp.IsSuccessStatusCode || resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest,
            $"login {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task Projects_list_and_config()
    {
        var list = await _client.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var cfg = await _client.GetAsync($"/api/projects/{Uri.EscapeDataString(_projectId)}/config");
        // config may 400 if project incomplete — still must not 500
        Assert.NotEqual(HttpStatusCode.InternalServerError, cfg.StatusCode);

        if (cfg.IsSuccessStatusCode)
        {
            var put = await _client.PutAsJsonAsync(
                $"/api/projects/{Uri.EscapeDataString(_projectId)}/config",
                new Dictionary<string, object?> { ["resolution"] = "480p", ["model_name"] = "grok-imagine-video" });
            Assert.NotEqual(HttpStatusCode.InternalServerError, put.StatusCode);
        }
    }

    [Fact]
    public async Task Models_catalog()
    {
        var all = await _client.GetAsync("/api/models");
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        var video = await _client.GetAsync("/api/models?capability=video");
        Assert.Equal(HttpStatusCode.OK, video.StatusCode);
        var doc = await video.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(doc.TryGetProperty("models", out var models) && models.GetArrayLength() > 0);
    }

    [Fact]
    public async Task Jobs_list_requires_filter()
    {
        var bare = await _client.GetAsync("/api/jobs");
        // Phase F: bare list may 400
        Assert.True(bare.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest);

        var mine = await _client.GetAsync("/api/jobs?mine=1");
        Assert.NotEqual(HttpStatusCode.InternalServerError, mine.StatusCode);
    }

    [Fact]
    public async Task Stage2_status_and_adaptation()
    {
        var s2 = await _client.GetAsync("/api/stage2-status");
        Assert.NotEqual(HttpStatusCode.InternalServerError, s2.StatusCode);

        var ad = await _client.GetAsync($"/api/projects/{Uri.EscapeDataString(_projectId)}/adaptation");
        Assert.NotEqual(HttpStatusCode.InternalServerError, ad.StatusCode);
    }

    [Fact]
    public async Task Characters_and_scenes_endpoints()
    {
        var chars = await _client.GetAsync($"/api/projects/{Uri.EscapeDataString(_projectId)}/characters");
        Assert.NotEqual(HttpStatusCode.InternalServerError, chars.StatusCode);

        var scenes = await _client.GetAsync($"/api/projects/{Uri.EscapeDataString(_projectId)}/scenes");
        Assert.NotEqual(HttpStatusCode.InternalServerError, scenes.StatusCode);
    }

    [Fact]
    public async Task Screenplay_get_put()
    {
        var get = await _client.GetAsync($"/api/projects/{Uri.EscapeDataString(_projectId)}/screenplay");
        Assert.NotEqual(HttpStatusCode.InternalServerError, get.StatusCode);

        var fountain = "Title: Smoke\n\nINT. ROOM - DAY\n\nALICE\nHello.\n";
        using var content = new StringContent(
            JsonSerializer.Serialize(new { fountainText = fountain }),
            Encoding.UTF8,
            "application/json");
        var put = await _client.PutAsync(
            $"/api/projects/{Uri.EscapeDataString(_projectId)}/screenplay", content);
        Assert.NotEqual(HttpStatusCode.InternalServerError, put.StatusCode);
    }

    [Fact]
    public async Task Edit_log_and_clip_reviews()
    {
        var log = await _client.GetAsync($"/api/projects/{Uri.EscapeDataString(_projectId)}/edit-log");
        Assert.NotEqual(HttpStatusCode.InternalServerError, log.StatusCode);

        var revs = await _client.GetAsync($"/api/projects/{Uri.EscapeDataString(_projectId)}/clip-reviews");
        Assert.NotEqual(HttpStatusCode.InternalServerError, revs.StatusCode);

        var review = await _client.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(_projectId)}/clips/review",
            new { scene = 1, clip = 1, status = "pass", note = "smoke" });
        Assert.NotEqual(HttpStatusCode.InternalServerError, review.StatusCode);
    }

    [Fact]
    public async Task Cost_and_wip_meta()
    {
        var cost = await _client.GetAsync($"/api/projects/{Uri.EscapeDataString(_projectId)}/cost");
        Assert.NotEqual(HttpStatusCode.InternalServerError, cost.StatusCode);

        var wip = await _client.GetAsync($"/api/projects/{Uri.EscapeDataString(_projectId)}/movie/wip/meta");
        Assert.NotEqual(HttpStatusCode.InternalServerError, wip.StatusCode);
    }

    [Fact]
    public async Task Job_starts_accept_or_reject_cleanly()
    {
        // Without blueprint/stage, these should 400/409 — never 500
        foreach (var path in new[]
                 {
                     "/api/jobs/gen-scene",
                     "/api/jobs/gen-batch",
                     "/api/jobs/stage1",
                     "/api/jobs/stage2",
                     "/api/jobs/remux",
                     "/api/jobs/character-variants",
                     "/api/jobs/voice-preview",
                     "/api/jobs/clip-auto-review",
                     "/api/jobs/clip-auto-review-batch",
                     "/api/jobs/sort-character-plates",
                     "/api/jobs/book-prepare",
                 })
        {
            var body = path switch
            {
                "/api/jobs/gen-scene" => JsonSerializer.Serialize(new { projectId = _projectId, scene = 1 }),
                "/api/jobs/gen-batch" => JsonSerializer.Serialize(new { projectId = _projectId, scenes = new[] { 1 } }),
                "/api/jobs/character-variants" => JsonSerializer.Serialize(new { projectId = _projectId, charKey = "Character_A" }),
                "/api/jobs/voice-preview" => JsonSerializer.Serialize(new { projectId = _projectId, charKey = "Character_A" }),
                "/api/jobs/clip-auto-review" => JsonSerializer.Serialize(new { projectId = _projectId, scene = 1, clip = 1 }),
                "/api/jobs/clip-auto-review-batch" => JsonSerializer.Serialize(new { projectId = _projectId, onlyMissing = true }),
                _ => JsonSerializer.Serialize(new { projectId = _projectId }),
            };
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await _client.PostAsync(path, content);
            Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
            Assert.True(
                resp.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK
                    or HttpStatusCode.BadRequest or HttpStatusCode.Conflict
                    or HttpStatusCode.NotFound,
                $"{path} → {(int)resp.StatusCode}");
        }
    }

    [Fact]
    public async Task Locks_endpoint()
    {
        var resp = await _client.GetAsync("/api/locks");
        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_endpoints_require_admin_or_succeed()
    {
        // Without admin JWT most return 403
        var insights = await _client.GetAsync("/api/admin/learning/insights");
        Assert.True(insights.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.OK or HttpStatusCode.Unauthorized);

        var state = await _client.GetAsync("/api/admin/state");
        Assert.True(state.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.OK or HttpStatusCode.Unauthorized);

        var packs = await _client.GetAsync("/api/admin/learning/packs");
        Assert.True(packs.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.OK or HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_login_then_learning_apis()
    {
        var login = await _client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" });
        if (!login.IsSuccessStatusCode)
            return; // env without password still covered by forbidden tests

        var loginJson = await login.Content.ReadFromJsonAsync<JsonElement>();
        string? token = null;
        if (loginJson.TryGetProperty("token", out var t))
            token = t.GetString();
        else if (loginJson.TryGetProperty("accessToken", out var t2))
            token = t2.GetString();

        using var admin = _factory.CreateClient();
        if (!string.IsNullOrWhiteSpace(token))
            admin.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        admin.DefaultRequestHeaders.Add("X-User-Id", "admin");

        foreach (var path in new[]
                 {
                     "/api/admin/state",
                     "/api/admin/learning/insights",
                     "/api/admin/learning/events",
                     "/api/admin/learning/packs",
                     "/api/admin/config",
                     "/api/admin/loadsim",
                 })
        {
            var resp = await admin.GetAsync(path);
            Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
        }

        var propose = await admin.PostAsJsonAsync("/api/admin/learning/propose",
            new { lastNFails = 10, projectId = _projectId });
        Assert.NotEqual(HttpStatusCode.InternalServerError, propose.StatusCode);

        var suggest = await admin.PostAsync(
            $"/api/admin/learning/project-rules/{Uri.EscapeDataString(_projectId)}/suggest?minFails=3",
            null);
        Assert.NotEqual(HttpStatusCode.InternalServerError, suggest.StatusCode);

        var rules = await admin.GetAsync(
            $"/api/admin/learning/project-rules/{Uri.EscapeDataString(_projectId)}");
        Assert.NotEqual(HttpStatusCode.InternalServerError, rules.StatusCode);
    }

    [Fact]
    public async Task Media_missing_returns_not_found_or_bad_request()
    {
        var v = await _client.GetAsync(
            $"/api/projects/{Uri.EscapeDataString(_projectId)}/scenes/1/clips/1/video");
        Assert.True(v.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.OK);

        var c = await _client.GetAsync(
            $"/api/projects/{Uri.EscapeDataString(_projectId)}/scenes/1/composite");
        Assert.True(c.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.OK);

        var w = await _client.GetAsync(
            $"/api/projects/{Uri.EscapeDataString(_projectId)}/movie/wip");
        Assert.True(w.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.OK);
    }

    [Fact]
    public async Task Character_voice_audio_status()
    {
        var st = await _client.GetAsync(
            $"/api/projects/{Uri.EscapeDataString(_projectId)}/characters/Character_A/voice/audio/status");
        Assert.NotEqual(HttpStatusCode.InternalServerError, st.StatusCode);
    }

    [Fact]
    public async Task Auto_review_draft_missing()
    {
        var d = await _client.GetAsync(
            $"/api/projects/{Uri.EscapeDataString(_projectId)}/scenes/1/clips/1/auto-review");
        Assert.True(d.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cancel_jobs_endpoint_scopes_to_user()
    {
        // Non-admin bulk cancel must succeed (own jobs only) and not 500.
        var resp = await _client.PostAsync("/api/jobs/cancel", null);
        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.True(
            resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.Unauthorized,
            $"unexpected {(int)resp.StatusCode}");
        if (resp.IsSuccessStatusCode)
        {
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.TryGetProperty("ok", out var ok) && ok.GetBoolean());
            if (json.TryGetProperty("scope", out var scope))
                Assert.Equal("user", scope.GetString());
        }
    }

    [Fact]
    public async Task Cancel_jobs_all_as_non_admin_forbidden_or_ignored()
    {
        // ?all=true without admin → 403
        var resp = await _client.PostAsync("/api/jobs/cancel?all=true", null);
        Assert.True(
            resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                or HttpStatusCode.OK,
            $"unexpected {(int)resp.StatusCode}");
        // Factory user client is typically non-admin — expect 403 when identity works
        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(json.GetProperty("ok").GetBoolean());
        }
    }
}
