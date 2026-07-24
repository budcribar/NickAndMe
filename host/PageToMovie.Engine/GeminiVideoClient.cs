using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// Google Veo video client via Gemini's long-running-operation pattern
/// (<c>predictLongRunning</c> → poll <c>operations.get</c> → download).
///
/// CONFIDENCE NOTE: unlike <see cref="AnthropicChatClient"/> / <see cref="GeminiChatClient"/>
/// (well-documented, stable request/response shapes), the exact field path for the finished
/// video inside the operation's <c>response</c> object is the part of this file most likely
/// to need adjustment against a real account — <see cref="ExtractVideoUri"/> tries several
/// plausible paths defensively, but treat this class as needing a live smoke test before
/// production use, same as any provider added without API access to verify against.
/// </summary>
public sealed class GeminiVideoClient : IVideoClient
{
    public const string ApiBase = SupportedModelCatalog.GoogleApiBase;

    private readonly HttpClient _http;
    private readonly PageToMovieOptions _opts;
    private readonly ProjectTelemetryService _telemetry;
    private readonly ILogger<GeminiVideoClient> _log;

    public GeminiVideoClient(
        HttpClient http,
        IOptions<PageToMovieOptions> opts,
        ProjectTelemetryService telemetry,
        ILogger<GeminiVideoClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _telemetry = telemetry;
        _log = log;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(ApiBase + "/");
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    /// <summary>
    /// Veo does not have a direct equivalent of Grok's reference_images / video-extend on the
    /// same endpoint family yet in this client — only text-to-video and image-to-video
    /// (first frame) are implemented. <paramref name="continueFromVideoPath"/> and multiple
    /// <paramref name="referenceImagePaths"/> are not supported; passing them throws rather
    /// than silently ignoring continuity, since a silently-wrong clip is worse than a loud stop.
    /// </summary>
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
        if (!string.IsNullOrWhiteSpace(continueFromVideoPath))
            throw new NotSupportedException(
                "GeminiVideoClient does not implement clip-to-clip continue yet — " +
                "use image-to-video (startFrameImagePath) as a fallback continuity strategy, " +
                "or route this clip to Grok.");
        if (referenceImagePaths is { Count: > 0 })
            throw new NotSupportedException(
                "GeminiVideoClient does not implement multi reference-image conditioning yet — " +
                "use a single startFrameImagePath, or route this clip to Grok.");

        var hasStart = !string.IsNullOrWhiteSpace(startFrameImagePath) && File.Exists(startFrameImagePath);
        durationSeconds = Math.Clamp(durationSeconds, 1, 10);

        var instance = new Dictionary<string, object?> { ["prompt"] = prompt };
        if (hasStart)
        {
            var (mime, b64) = await FileToBase64Async(startFrameImagePath!, ct).ConfigureAwait(false);
            instance["image"] = new Dictionary<string, object?>
            {
                ["bytesBase64Encoded"] = b64,
                ["mimeType"] = mime,
            };
        }

        var payload = new Dictionary<string, object?>
        {
            ["instances"] = new object[] { instance },
            ["parameters"] = new Dictionary<string, object?>
            {
                ["aspectRatio"] = "16:9",
                ["durationSeconds"] = durationSeconds,
                ["resolution"] = NormalizeResolution(resolution),
            },
        };

        var endpoint = $"models/{Uri.EscapeDataString(model)}:predictLongRunning";
        var mode = hasStart ? "image-to-video" : "text-to-video";
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await SendJsonAsync(HttpMethod.Post, endpoint, payload, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _telemetry.LogApiCall(new ApiCallTelemetry
                {
                    Kind = "video",
                    Mode = mode,
                    Endpoint = endpoint,
                    Model = model,
                    HttpStatus = (int)resp.StatusCode,
                    DurationMs = sw.ElapsedMilliseconds,
                    Prompt = prompt,
                    PromptChars = prompt.Length,
                    Resolution = resolution,
                    DurationSec = durationSeconds,
                    Error = Trim(body, 400),
                    Ok = false,
                });
                throw new InvalidOperationException(
                    $"Gemini {endpoint} HTTP {(int)resp.StatusCode}: {Trim(body, 400)}");
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("name", out var nameEl) ||
                nameEl.GetString() is not { Length: > 0 } opName)
            {
                throw new InvalidOperationException(
                    $"Gemini predictLongRunning response missing operation name: {Trim(body, 300)}");
            }

            _telemetry.LogApiCall(new ApiCallTelemetry
            {
                Kind = "video",
                Mode = mode,
                Endpoint = endpoint,
                Model = model,
                HttpStatus = (int)resp.StatusCode,
                RequestId = opName,
                DurationMs = sw.ElapsedMilliseconds,
                Prompt = prompt,
                PromptChars = prompt.Length,
                Resolution = resolution,
                DurationSec = durationSeconds,
                Ok = true,
            });
            return opName;
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not NotSupportedException)
        {
            _telemetry.LogApiCall(new ApiCallTelemetry
            {
                Kind = "video",
                Mode = mode,
                Endpoint = endpoint,
                Model = model,
                DurationMs = sw.ElapsedMilliseconds,
                Prompt = prompt,
                Error = ex.Message,
                Ok = false,
            });
            throw;
        }
    }

    public async Task<string> PollForVideoUrlAsync(
        string requestId,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(60, _opts.GrokTimeoutSeconds));
        var poll = Math.Max(2, _opts.GrokPollSeconds);
        var sw = Stopwatch.StartNew();
        var polls = 0;
        // requestId is the full operation name returned by SubmitGenerationAsync, e.g.
        // "models/veo-3.1/operations/abc123" — the operations.get path is that name directly.
        var opPath = requestId.TrimStart('/');

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            polls++;
            using var resp = await SendAsync(HttpMethod.Get, opPath, content: null, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _telemetry.LogApiCall(new ApiCallTelemetry
                {
                    Kind = "video_poll",
                    Endpoint = opPath,
                    RequestId = requestId,
                    HttpStatus = (int)resp.StatusCode,
                    DurationMs = sw.ElapsedMilliseconds,
                    Attempt = polls,
                    Error = Trim(body, 400),
                    Ok = false,
                });
                throw new InvalidOperationException(
                    $"Gemini operation poll HTTP {(int)resp.StatusCode}: {Trim(body, 400)}");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var done = root.TryGetProperty("done", out var doneEl) &&
                       doneEl.ValueKind == JsonValueKind.True;

            if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
            {
                var detail = errEl.ToString();
                _telemetry.LogApiCall(new ApiCallTelemetry
                {
                    Kind = "video_poll",
                    Endpoint = opPath,
                    RequestId = requestId,
                    HttpStatus = 200,
                    DurationMs = sw.ElapsedMilliseconds,
                    Attempt = polls,
                    Mode = "failed",
                    Error = Trim(detail, 500),
                    Ok = false,
                });
                throw new InvalidOperationException($"Gemini video operation failed: {Trim(detail, 400)}");
            }

            if (done)
            {
                var url = ExtractVideoUri(root);
                if (url is null)
                {
                    throw new InvalidOperationException(
                        $"Gemini operation done but no video URI found in response " +
                        $"(schema may differ from expected — see class-level CONFIDENCE NOTE): " +
                        $"{Trim(body, 500)}");
                }
                _telemetry.LogApiCall(new ApiCallTelemetry
                {
                    Kind = "video_poll",
                    Endpoint = opPath,
                    RequestId = requestId,
                    HttpStatus = 200,
                    DurationMs = sw.ElapsedMilliseconds,
                    Attempt = polls,
                    Mode = "done",
                    Ok = true,
                });
                return url;
            }

            onProgress?.Invoke($"operation not done (poll {polls})");
            await Task.Delay(TimeSpan.FromSeconds(poll), ct).ConfigureAwait(false);
        }

        throw new TimeoutException($"Gemini video operation timed out after {_opts.GrokTimeoutSeconds}s");
    }

    /// <summary>
    /// Tries several plausible paths for the generated video's URI inside a finished
    /// operation's <c>response</c> object, since this is the part of the Veo long-running-
    /// operation schema this class is least certain about (see class-level CONFIDENCE NOTE).
    /// Public so tests can exercise it against sample payloads without a live API call.
    /// </summary>
    public static string? ExtractVideoUri(JsonElement operationRoot)
    {
        if (!operationRoot.TryGetProperty("response", out var response) ||
            response.ValueKind != JsonValueKind.Object)
            return null;

        // Path A: response.generateVideoResponse.generatedSamples[0].video.uri
        if (response.TryGetProperty("generateVideoResponse", out var gvr) &&
            gvr.TryGetProperty("generatedSamples", out var samples) &&
            samples.ValueKind == JsonValueKind.Array &&
            samples.GetArrayLength() > 0)
        {
            var s0 = samples[0];
            if (s0.TryGetProperty("video", out var v0) &&
                v0.TryGetProperty("uri", out var u0) &&
                u0.GetString() is { Length: > 0 } uri0)
                return uri0;
        }

        // Path B: response.videos[0].uri
        if (response.TryGetProperty("videos", out var videos) &&
            videos.ValueKind == JsonValueKind.Array &&
            videos.GetArrayLength() > 0)
        {
            var v0 = videos[0];
            if (v0.TryGetProperty("uri", out var u0) && u0.GetString() is { Length: > 0 } uri0)
                return uri0;
        }

        // Path C: response.video.uri (single-sample shape)
        if (response.TryGetProperty("video", out var v1) &&
            v1.TryGetProperty("uri", out var u1) &&
            u1.GetString() is { Length: > 0 } uri1)
            return uri1;

        return null;
    }

    public async Task DownloadToFileAsync(string url, string destPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        // Google file/media download URLs generally need the same API key as the rest of the API.
        // Auth is on the request only (not shared DefaultRequestHeaders).
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyApiKey(req);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(destPath);
        await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
        _log.LogInformation("Downloaded {Bytes} bytes → {Path}", new FileInfo(destPath).Length, destPath);
    }

    private static string NormalizeResolution(string resolution) =>
        (resolution ?? "").Trim().ToLowerInvariant() switch
        {
            "1080p" => "1080p",
            "720p" => "720p",
            _ => "720p", // Veo's minimum documented resolution tier; 480p is Grok-specific
        };

    private static async Task<(string Mime, string Base64)> FileToBase64Async(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var mime = ext switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "image/jpeg",
        };
        return (mime, Convert.ToBase64String(bytes));
    }

    private static string? ResolveApiKey() =>
        ApiKeyScope.CurrentGemini
        ?? Environment.GetEnvironmentVariable(SupportedModelCatalog.GoogleApiKeyEnv);

    private static void ApplyApiKey(HttpRequestMessage req)
    {
        var key = ResolveApiKey();
        if (!string.IsNullOrWhiteSpace(key))
            req.Headers.TryAddWithoutValidation("x-goog-api-key", key.Trim());
    }

    private async Task<HttpResponseMessage> SendJsonAsync(
        HttpMethod method, string uri, object payload, CancellationToken ct) =>
        await SendAsync(method, uri, JsonContent.Create(payload), ct).ConfigureAwait(false);

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string uri, HttpContent? content, CancellationToken ct)
    {
        // Per-request API key — never mutate shared DefaultRequestHeaders (multi-user race).
        using var req = new HttpRequestMessage(method, uri) { Content = content };
        ApplyApiKey(req);
        return await _http.SendAsync(req, ct).ConfigureAwait(false);
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];
}
