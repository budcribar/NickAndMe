using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilmStudio.Web.Services;

/// <summary>
/// HTTP client for the Python engine API (host/python_engine_api.py).
/// Does not embed the film engine — only starts/cancels jobs and reads status.
/// </summary>
public sealed class EngineApiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public EngineApiClient(HttpClient http) => _http = http;

    public async Task<JsonElement?> GetHealthAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/health", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct);
    }

    public async Task<ProjectsResponse?> GetProjectsAsync(CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<ProjectsResponse>("/api/projects", JsonOpts, ct);
    }

    public async Task<JsonElement?> ActivateProjectAsync(string projectId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"/api/projects/{Uri.EscapeDataString(projectId)}/activate",
            new { },
            ct);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                body.ValueKind == JsonValueKind.Undefined
                    ? resp.ReasonPhrase
                    : body.ToString());
        return body;
    }

    public async Task<JobStatusResponse?> GetJobAsync(CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<JobStatusResponse>("/api/jobs", JsonOpts, ct);
    }

    public async Task<JsonElement?> StartSceneGenAsync(
        string projectId,
        int scene,
        bool onlyMissing = true,
        CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            "/api/jobs/gen-scene",
            new
            {
                project_id = projectId,
                scene,
                only_missing = onlyMissing,
                run_qa = true,
                remux = true,
            },
            ct);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string msg = $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
            if (body.ValueKind == JsonValueKind.Object &&
                body.TryGetProperty("error", out var err))
            {
                msg = err.GetString() ?? msg;
            }
            throw new InvalidOperationException(msg);
        }
        return body;
    }

    public async Task<JsonElement?> CancelJobAsync(CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/api/jobs/cancel", new { }, ct);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct);
    }

    public async Task<JsonElement?> GetStage2StatusAsync(CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<JsonElement>("/api/stage2-status", JsonOpts, ct);
    }
}

public sealed class ProjectsResponse
{
    public bool Ok { get; set; }
    public ProjectInfo? Active { get; set; }
    public List<ProjectInfo> Projects { get; set; } = new();
}

public sealed class ProjectInfo
{
    public string? Id { get; set; }
    public string? Label { get; set; }
    public string? Title { get; set; }
}

public sealed class JobStatusResponse
{
    public bool Ok { get; set; }
    public bool Running { get; set; }
    public Dictionary<string, JsonElement>? Job { get; set; }
}
