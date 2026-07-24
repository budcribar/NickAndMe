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
/// Google Gemini <c>generateContent</c> client — chat completion (planning / cast scrub / QA
/// reasoning) and multimodal completion (clip / frame review). Response-shape notes below are
/// built from Gemini's public documented format; verify against a live call before relying on
/// this in production, same as any provider added without an account to test against.
/// </summary>
public sealed class GeminiChatClient : IChatClient, IVisionClient
{
    public const string ApiBase = SupportedModelCatalog.GoogleApiBase;

    private readonly HttpClient _http;
    private readonly ProjectTelemetryService _telemetry;
    private readonly ILogger<GeminiChatClient> _log;

    public GeminiChatClient(
        HttpClient http,
        IOptions<PageToMovieOptions> opts,
        ProjectTelemetryService telemetry,
        ILogger<GeminiChatClient> log)
    {
        _ = opts; // reserved — no Gemini-specific options today
        _http = http;
        _telemetry = telemetry;
        _log = log;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(ApiBase + "/");
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string model = "gemini-3-pro",
        double temperature = 0.2,
        CancellationToken ct = default,
        string? mode = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["system_instruction"] = new Dictionary<string, object?>
            {
                ["parts"] = new object[] { new Dictionary<string, object?> { ["text"] = systemPrompt } },
            },
            ["contents"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["parts"] = new object[] { new Dictionary<string, object?> { ["text"] = userPrompt } },
                },
            },
            ["generationConfig"] = new Dictionary<string, object?> { ["temperature"] = temperature },
        };
        return await SendAsync(
            payload, model, "chat", mode,
            systemPrompt, userPrompt,
            (systemPrompt?.Length ?? 0) + (userPrompt?.Length ?? 0),
            ct).ConfigureAwait(false);
    }

    /// <summary>Multi-image completion for clip auto-review (prev tail + current frames).</summary>
    public async Task<string> CompleteWithImagesAsync(
        string prompt,
        IReadOnlyList<string> imagePaths,
        string model = "gemini-3-pro",
        string detail = "low",
        CancellationToken ct = default)
    {
        var parts = new List<object?>();
        foreach (var path in imagePaths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)))
        {
            var (mime, b64) = await FileToBase64Async(path, ct).ConfigureAwait(false);
            parts.Add(new Dictionary<string, object?>
            {
                ["inline_data"] = new Dictionary<string, object?> { ["mime_type"] = mime, ["data"] = b64 },
            });
        }
        parts.Add(new Dictionary<string, object?> { ["text"] = prompt });

        var payload = new Dictionary<string, object?>
        {
            ["contents"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["parts"] = parts },
            },
        };
        return await SendAsync(
            payload, model, "vision", "clip_auto_review",
            prompt, string.Join(", ", imagePaths.Select(Path.GetFileName)),
            prompt.Length, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Not implemented for Gemini — book-page OCR / cast classification stay on
    /// <see cref="GrokVisionClient"/> for now. Fails loudly rather than silently
    /// returning a wrong answer if ever routed here.
    /// </summary>
    public Task<string> TranscribePageAsync(
        string imagePath, int page, string model = "gemini-3-pro", CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Book-page transcription is not implemented for Gemini yet — route this call to Grok.");

    /// <inheritdoc cref="TranscribePageAsync"/>
    public Task<CharacterPageClassification> ClassifyCharactersOnImageAsync(
        string imagePath, int page, IReadOnlyList<CharacterClassifyHint> cast,
        string model = "gemini-3-pro", CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Character-page classification is not implemented for Gemini yet — route this call to Grok.");

    private async Task<string> SendAsync(
        Dictionary<string, object?> payload,
        string model,
        string kind,
        string? mode,
        string? promptForLog,
        string? userPromptForLog,
        int promptChars,
        CancellationToken ct)
    {
        var key = ResolveApiKey();
        var modeTag = string.IsNullOrWhiteSpace(mode) ? null : mode.Trim();
        var endpoint = $"models/{Uri.EscapeDataString(model)}:generateContent";
        var sw = Stopwatch.StartNew();
        try
        {
            // Auth on a per-request message, not _http.DefaultRequestHeaders: this client is a
            // singleton shared by every concurrent classifier call, and mutating shared headers
            // per-call is a race (one call's key can leak into or clobber another's in flight).
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload),
            };
            if (!string.IsNullOrWhiteSpace(key))
                req.Headers.Add("x-goog-api-key", key.Trim());
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _telemetry.LogApiCall(new ApiCallTelemetry
                {
                    Kind = kind,
                    Mode = modeTag,
                    Endpoint = endpoint,
                    Model = model,
                    HttpStatus = (int)resp.StatusCode,
                    DurationMs = sw.ElapsedMilliseconds,
                    SystemPrompt = promptForLog,
                    UserPrompt = userPromptForLog,
                    PromptChars = promptChars,
                    Error = Trim(body, 800),
                    Ok = false,
                });
                throw new InvalidOperationException(
                    $"Gemini {endpoint} HTTP {(int)resp.StatusCode}: {Trim(body, 800)}");
            }

            using var doc = JsonDocument.Parse(body);
            var text = ExtractMessageText(doc.RootElement);
            _telemetry.LogApiCall(new ApiCallTelemetry
            {
                Kind = kind,
                Mode = modeTag,
                Endpoint = endpoint,
                Model = model,
                HttpStatus = (int)resp.StatusCode,
                DurationMs = sw.ElapsedMilliseconds,
                SystemPrompt = promptForLog,
                UserPrompt = userPromptForLog,
                PromptChars = promptChars,
                ResponsePreview = text.Length > 2000 ? text[..2000] : text,
                ResponseChars = text.Length,
                Ok = true,
            });
            return text;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _telemetry.LogApiCall(new ApiCallTelemetry
            {
                Kind = kind,
                Mode = modeTag,
                Endpoint = endpoint,
                Model = model,
                DurationMs = sw.ElapsedMilliseconds,
                SystemPrompt = promptForLog,
                UserPrompt = userPromptForLog,
                Error = ex.Message,
                Ok = false,
            });
            throw;
        }
    }

    private static string? ResolveApiKey() =>
        Abstractions.ApiKeyScope.CurrentGemini
        ?? Environment.GetEnvironmentVariable(SupportedModelCatalog.GoogleApiKeyEnv);

    /// <summary>Test helper for extracting model text from a generateContent response.</summary>
    public static string ExtractMessageTextForTests(JsonElement result) => ExtractMessageText(result);

    /// <summary>
    /// Gemini response shape: <c>{ candidates: [{ content: { parts: [{ text: "..." }] } }] }</c>.
    /// Concatenates all text parts of the first candidate.
    /// </summary>
    private static string ExtractMessageText(JsonElement result)
    {
        if (result.TryGetProperty("candidates", out var candidates) &&
            candidates.ValueKind == JsonValueKind.Array &&
            candidates.GetArrayLength() > 0)
        {
            var c0 = candidates[0];
            if (c0.ValueKind == JsonValueKind.Object &&
                c0.TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts) &&
                parts.ValueKind == JsonValueKind.Array)
            {
                var texts = new List<string>();
                foreach (var p in parts.EnumerateArray())
                {
                    if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("text", out var t))
                        texts.Add(t.GetString() ?? "");
                }
                if (texts.Count > 0)
                    return string.Join("\n", texts);
            }
        }
        var raw = result.GetRawText();
        return raw.Length <= 2000 ? raw : raw[..2000];
    }

    private static async Task<(string Mime, string Base64)> FileToBase64Async(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var mime = ext switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "image/jpeg",
        };
        return (mime, Convert.ToBase64String(bytes));
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];
}
