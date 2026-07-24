using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// Anthropic Messages API client — chat completion (planning / cast scrub / QA reasoning)
/// and multimodal completion (clip / frame review). Mirrors <see cref="GrokChatClient"/>'s
/// telemetry and auth conventions so callers and the api_calls.jsonl log see the same shape
/// regardless of provider.
/// </summary>
public sealed class AnthropicChatClient : IChatClient, IVisionClient
{
    public const string ApiBase = SupportedModelCatalog.AnthropicApiBase;
    public const string ApiVersion = "2023-06-01";
    public const int DefaultMaxTokens = 4096;

    private readonly HttpClient _http;
    private readonly ProjectTelemetryService _telemetry;
    private readonly ILogger<AnthropicChatClient> _log;

    public AnthropicChatClient(
        HttpClient http,
        IOptions<PageToMovieOptions> opts,
        ProjectTelemetryService telemetry,
        ILogger<AnthropicChatClient> log)
    {
        _ = opts; // reserved — no Anthropic-specific options today
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
        string model = "claude-sonnet-5",
        double temperature = 0.2,
        CancellationToken ct = default,
        string? mode = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = DefaultMaxTokens,
            ["temperature"] = temperature,
            ["system"] = systemPrompt,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = userPrompt },
            },
        };
        return await SendAsync(
            payload, model, "chat", "messages", mode,
            systemPrompt, userPrompt,
            (systemPrompt?.Length ?? 0) + (userPrompt?.Length ?? 0),
            ct).ConfigureAwait(false);
    }

    /// <summary>Multi-image completion for clip auto-review (prev tail + current frames).</summary>
    public async Task<string> CompleteWithImagesAsync(
        string prompt,
        IReadOnlyList<string> imagePaths,
        string model = "claude-sonnet-5",
        string detail = "low",
        CancellationToken ct = default)
    {
        var content = new List<object?>();
        foreach (var path in imagePaths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)))
        {
            var (mime, b64) = await FileToBase64Async(path, ct).ConfigureAwait(false);
            content.Add(new Dictionary<string, object?>
            {
                ["type"] = "image",
                ["source"] = new Dictionary<string, object?>
                {
                    ["type"] = "base64",
                    ["media_type"] = mime,
                    ["data"] = b64,
                },
            });
        }
        content.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = prompt });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = DefaultMaxTokens,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = content },
            },
        };
        return await SendAsync(
            payload, model, "vision", "messages", "clip_auto_review",
            prompt, string.Join(", ", imagePaths.Select(Path.GetFileName)),
            prompt.Length, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Not implemented for Anthropic — book-page OCR / cast classification stay on
    /// <see cref="GrokVisionClient"/> for now. Fails loudly rather than silently
    /// returning a wrong answer if ever routed here.
    /// </summary>
    public Task<string> TranscribePageAsync(
        string imagePath, int page, string model = "claude-sonnet-5", CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Book-page transcription is not implemented for Anthropic yet — route this call to Grok.");

    /// <inheritdoc cref="TranscribePageAsync"/>
    public Task<CharacterPageClassification> ClassifyCharactersOnImageAsync(
        string imagePath, int page, IReadOnlyList<CharacterClassifyHint> cast,
        string model = "claude-sonnet-5", CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Character-page classification is not implemented for Anthropic yet — route this call to Grok.");

    private async Task<string> SendAsync(
        Dictionary<string, object?> payload,
        string model,
        string kind,
        string endpoint,
        string? mode,
        string? promptForLog,
        string? userPromptForLog,
        int promptChars,
        CancellationToken ct)
    {
        var key = ResolveApiKey();
        var modeTag = string.IsNullOrWhiteSpace(mode) ? null : mode.Trim();
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
            {
                req.Headers.Add("x-api-key", key.Trim());
                req.Headers.Add("anthropic-version", ApiVersion);
            }
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
                    $"Anthropic {endpoint} HTTP {(int)resp.StatusCode}: {Trim(body, 800)}");
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

    /// <summary>Test helper for extracting assistant text from a Messages API response.</summary>
    public static string ExtractMessageTextForTests(JsonElement result) => ExtractMessageText(result);

    /// <summary>
    /// Anthropic Messages API response shape: <c>{ content: [{ type: "text", text: "..." }, ...] }</c>.
    /// Concatenates all text blocks (tool_use / other block types are skipped).
    /// </summary>
    private static string? ResolveApiKey() =>
        Abstractions.ApiKeyScope.CurrentAnthropic
        ?? Environment.GetEnvironmentVariable(SupportedModelCatalog.AnthropicApiKeyEnv);

    private static string ExtractMessageText(JsonElement result)
    {
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object &&
                    block.TryGetProperty("type", out var t) &&
                    string.Equals(t.GetString(), "text", StringComparison.Ordinal) &&
                    block.TryGetProperty("text", out var txt))
                {
                    parts.Add(txt.GetString() ?? "");
                }
            }
            if (parts.Count > 0)
                return string.Join("\n", parts);
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
