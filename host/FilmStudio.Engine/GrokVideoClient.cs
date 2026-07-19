using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FilmStudio.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using FilmStudio.Engine.Abstractions;

namespace FilmStudio.Engine;

/// <summary>
/// xAI Grok video generate / poll / download client.
/// </summary>
public sealed class GrokVideoClient : IGrokVideoClient
{
    public const string ApiBase = "https://api.x.ai/v1";

    private readonly HttpClient _http;
    private readonly FilmStudioOptions _opts;
    private readonly ILogger<GrokVideoClient> _log;

    public GrokVideoClient(
        HttpClient http,
        IOptions<FilmStudioOptions> opts,
        ILogger<GrokVideoClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(ApiBase + "/");
    }

    public bool IsConfigured
    {
        get
        {
            EnsureAuthHeader();
            return _http.DefaultRequestHeaders.Authorization is not null;
        }
    }

    /// <param name="referenceImagePaths">Character/style refs → API <c>reference_images</c> + prompt <c>&lt;IMAGE_n&gt;</c> tags.</param>
    /// <param name="startFrameImagePath">Optional first-frame still (image-to-video). Prefer video continue when possible.</param>
    /// <param name="continueFromVideoPath">Previous clip MP4 — uses <c>/videos/extensions</c> (official continue).</param>
    public async Task<string> SubmitGenerationAsync(
        string prompt,
        int durationSeconds,
        string resolution,
        string model,
        CancellationToken ct,
        IReadOnlyList<string>? referenceImagePaths = null,
        string? startFrameImagePath = null,
        string? continueFromVideoPath = null)
    {
        EnsureAuthHeader();
        var refs = (referenceImagePaths ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Take(7)
            .ToList();
        var hasContinue = !string.IsNullOrWhiteSpace(continueFromVideoPath) &&
                          File.Exists(continueFromVideoPath);
        var hasStart = !string.IsNullOrWhiteSpace(startFrameImagePath) && File.Exists(startFrameImagePath);

        // Priority: video-continue > start-frame > reference images > text
        if (hasContinue)
        {
            refs.Clear();
            hasStart = false;
        }
        else if (hasStart && refs.Count > 0)
        {
            _log.LogWarning(
                "Grok video: start frame + reference_images not allowed together — using start frame only ({Start})",
                Path.GetFileName(startFrameImagePath));
            refs.Clear();
        }

        // Image / reference-to-video / extension max duration is typically 10s for the new portion
        if (hasStart || refs.Count > 0 || hasContinue)
            durationSeconds = Math.Min(Math.Max(1, durationSeconds), 10);

        // ── Official continue: POST /v1/videos/extensions ─────────────────
        if (hasContinue)
        {
            var videoUri = await FileToDataUriAsync(continueFromVideoPath!, ct);
            var extPayload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["prompt"] = prompt,
                // duration = length of NEW extension only (not total)
                ["duration"] = durationSeconds,
                ["video"] = new Dictionary<string, object?> { ["url"] = videoUri },
            };
            // resolution/aspect may be ignored on extensions; still send when API allows
            if (!string.IsNullOrWhiteSpace(resolution))
                extPayload["resolution"] = resolution;

            _log.LogInformation(
                "Grok video EXTEND from={Prev} extensionDur={Dur}s promptLen={Len}",
                Path.GetFileName(continueFromVideoPath), durationSeconds, prompt.Length);

            using var extResp = await _http.PostAsJsonAsync("videos/extensions", extPayload, ct);
            var extBody = await extResp.Content.ReadAsStringAsync(ct);
            if (!extResp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Grok video extend HTTP {(int)extResp.StatusCode}: {Trim(extBody, 500)}");

            using var extDoc = JsonDocument.Parse(extBody);
            if (!extDoc.RootElement.TryGetProperty("request_id", out var extRid) ||
                extRid.GetString() is not { Length: > 0 } extId)
            {
                throw new InvalidOperationException(
                    $"Grok extend response missing request_id: {Trim(extBody, 300)}");
            }
            return extId;
        }

        // ── Fresh generation: POST /v1/videos/generations ────────────────
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["duration"] = durationSeconds,
            ["aspect_ratio"] = "16:9",
            ["resolution"] = resolution,
        };

        if (hasStart)
        {
            payload["image"] = new Dictionary<string, object?>
            {
                ["url"] = await FileToDataUriAsync(startFrameImagePath!, ct),
            };
            _log.LogInformation(
                "Grok video image-to-video startFrame={Frame} promptLen={Len} duration={Dur}s",
                Path.GetFileName(startFrameImagePath), prompt.Length, durationSeconds);
        }
        else if (refs.Count > 0)
        {
            var refObjs = new List<object?>();
            foreach (var path in refs)
                refObjs.Add(new Dictionary<string, object?> { ["url"] = await FileToDataUriAsync(path, ct) });
            payload["reference_images"] = refObjs;
            _log.LogInformation(
                "Grok video reference-to-video refs={N} promptLen={Len} duration={Dur}s",
                refs.Count, prompt.Length, durationSeconds);
        }
        else
        {
            _log.LogInformation(
                "Grok video text-to-video promptLen={Len} duration={Dur}s",
                prompt.Length, durationSeconds);
        }

        using var resp = await _http.PostAsJsonAsync("videos/generations", payload, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Grok submit HTTP {(int)resp.StatusCode}: {Trim(body, 400)}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("request_id", out var rid) ||
            rid.GetString() is not { Length: > 0 } id)
        {
            throw new InvalidOperationException($"Grok response missing request_id: {Trim(body, 300)}");
        }
        return id;
    }

    private static async Task<string> FileToDataUriAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        // Guard huge uploads (short clips are fine; multi-MB is ok for 6–10s mp4)
        if (bytes.Length > 40 * 1024 * 1024)
            throw new InvalidOperationException(
                $"Video/image too large for data URI ({bytes.Length / (1024 * 1024)} MB). Max 40 MB.");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var mime = ext switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            _ => "image/jpeg",
        };
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    public async Task<string> PollForVideoUrlAsync(
        string requestId,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        EnsureAuthHeader();
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(60, _opts.GrokTimeoutSeconds));
        var poll = Math.Max(2, _opts.GrokPollSeconds);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var resp = await _http.GetAsync($"videos/{requestId}", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Grok poll HTTP {(int)resp.StatusCode}: {Trim(body, 400)}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;

            if (string.Equals(status, "done", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("video", out var video) &&
                    video.TryGetProperty("url", out var urlEl) &&
                    urlEl.GetString() is { Length: > 0 } url)
                {
                    return url;
                }
                throw new InvalidOperationException("Grok done with no video.url");
            }

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase))
            {
                var detail = root.TryGetProperty("error", out var err) ? err.ToString() : body;
                throw new InvalidOperationException($"Grok job {status}: {Trim(detail, 400)}");
            }

            var progress = root.TryGetProperty("progress", out var pr) ? pr.ToString() : null;
            onProgress?.Invoke(progress is null ? $"status={status}" : $"status={status} ({progress}%)");
            await Task.Delay(TimeSpan.FromSeconds(poll), ct);
        }

        throw new TimeoutException($"Grok job timed out after {_opts.GrokTimeoutSeconds}s");
    }

    public async Task DownloadToFileAsync(string url, string destPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(destPath);
        await resp.Content.CopyToAsync(fs, ct);
        _log.LogInformation("Downloaded {Bytes} bytes → {Path}", new FileInfo(destPath).Length, destPath);
    }

    private void EnsureAuthHeader()
    {
        // Prefer ambient job/request key (multi-user), else process env.
        var key = Abstractions.ApiKeyScope.Current
                  ?? Environment.GetEnvironmentVariable("XAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            _http.DefaultRequestHeaders.Authorization = null;
            return;
        }
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", key.Trim());
    }

    private static string Trim(string s, int n) =>
        s.Length <= n ? s : s[..n];
}
