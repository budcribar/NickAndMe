using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FilmStudio.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

/// <summary>
/// xAI Grok video generate / poll / download client.
/// </summary>
public sealed class GrokVideoClient
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

    /// <param name="referenceImagePaths">Optional local image paths for reference-to-video (max 7; API often caps duration at 10s).</param>
    public async Task<string> SubmitGenerationAsync(
        string prompt,
        int durationSeconds,
        string resolution,
        string model,
        CancellationToken ct,
        IReadOnlyList<string>? referenceImagePaths = null)
    {
        EnsureAuthHeader();
        var refs = (referenceImagePaths ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Take(7)
            .ToList();

        // Image / reference-to-video max duration is typically 10s
        if (refs.Count > 0)
            durationSeconds = Math.Min(durationSeconds, 10);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["duration"] = durationSeconds,
            ["aspect_ratio"] = "16:9",
            ["resolution"] = resolution,
        };

        if (refs.Count == 1)
        {
            payload["image"] = new Dictionary<string, object?>
            {
                ["url"] = await FileToDataUriAsync(refs[0], ct),
            };
        }
        else if (refs.Count > 1)
        {
            // Multi-ref: send as image_urls array when supported; also set primary image
            payload["image"] = new Dictionary<string, object?>
            {
                ["url"] = await FileToDataUriAsync(refs[0], ct),
            };
            var urls = new List<object?>();
            foreach (var path in refs)
                urls.Add(await FileToDataUriAsync(path, ct));
            payload["image_urls"] = urls;
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
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var mime = ext switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
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
        if (_http.DefaultRequestHeaders.Authorization is not null)
            return;
        var key = Environment.GetEnvironmentVariable("XAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
    }

    private static string Trim(string s, int n) =>
        s.Length <= n ? s : s[..n];
}
