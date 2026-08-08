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
public sealed class GeminiChatClient : IChatClient, IVisionClient, IGeminiVideoAnalysisClient
{
    public const string ApiBase = SupportedModelCatalog.GoogleApiBase;

    private readonly HttpClient _http;
    private readonly ProjectTelemetryService _telemetry;
    private readonly ILogger<GeminiChatClient> _log;
    private readonly GenerationErrorLogger? _errorLogger;

    public GeminiChatClient(
        HttpClient http,
        IOptions<PageToMovieOptions> opts,
        ProjectTelemetryService telemetry,
        ILogger<GeminiChatClient> log,
        GenerationErrorLogger? errorLogger = null)
    {
        _ = opts; // reserved — no Gemini-specific options today
        _http = http;
        _telemetry = telemetry;
        _log = log;
        _errorLogger = errorLogger;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(ApiBase + "/");
        if (_http.Timeout < TimeSpan.FromSeconds(180))
            _http.Timeout = TimeSpan.FromSeconds(180);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    private static readonly AsyncLocal<string?> _lastResolvedModel = new();

    /// <summary>
    /// The model id that actually served the most recently completed call on this async flow.
    /// Callers that need to report or attribute results by model (benchmarks, telemetry) should
    /// check this rather than assume the requested id is what generated the response.
    /// </summary>
    public static string? LastResolvedModel => _lastResolvedModel.Value;

    /// <summary>Maps the provider-neutral <c>reasoningEffort</c> scale to Gemini's
    /// <c>thinkingConfig.thinkingLevel</c>. Confirmed live: "high" is Gemini's ceiling — "max"/
    /// "xhigh"/"maximum" all 400. Falls through to "high" for anything at or above it.</summary>
    private static string? MapThinkingLevel(string? effort) => effort?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "low" => "low",
        "medium" => "medium",
        _ => "high",
    };

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string model = "",
        double temperature = 0.2,
        CancellationToken ct = default,
        string? mode = null,
        string? reasoningEffort = null)
    {
        var generationConfig = new Dictionary<string, object?> { ["temperature"] = temperature };
        var thinkingLevel = MapThinkingLevel(reasoningEffort);
        if (thinkingLevel is not null)
            generationConfig["thinkingConfig"] = new Dictionary<string, object?> { ["thinkingLevel"] = thinkingLevel };

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
            ["generationConfig"] = generationConfig,
        };
        return await SendWithTransientRetryAsync(
            attemptNum => SendAsync(
                payload, model, "chat", mode,
                systemPrompt, userPrompt,
                (systemPrompt?.Length ?? 0) + (userPrompt?.Length ?? 0),
                attemptNum, ct),
            model, mode, ct).ConfigureAwait(false);
    }

    /// <summary>Multi-image completion for clip auto-review (prev tail + current frames).</summary>
    public async Task<string> CompleteWithImagesAsync(
        string prompt,
        IReadOnlyList<string> imagePaths,
        string model = "",
        string detail = "low",
        CancellationToken ct = default)
    {
        var parts = new List<object?>();
        foreach (var path in imagePaths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)))
        {
            var (mime, b64) = await ProviderMediaHelpers.FileToBase64Async(path, ct, allowVideo: true).ConfigureAwait(false);
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
        return await SendWithTransientRetryAsync(
            attemptNum => SendAsync(
                payload, model, "vision", "clip_auto_review",
                prompt, string.Join(", ", imagePaths.Select(Path.GetFileName)),
                prompt.Length, attemptNum, ct),
            model, "clip_auto_review", ct).ConfigureAwait(false);
    }

    private Task<string> SendWithTransientRetryAsync(
        Func<int, Task<string>> call,
        string model,
        string? mode,
        CancellationToken ct) =>
        AiRetryPolicy.ChatSendWithTransientRetryAsync(
            call, _errorLogger, "gemini_chat_completion", model, mode, ct);


    /// <summary>
    /// Not implemented for Gemini — book-page OCR / cast classification stay on
    /// <see cref="GrokVisionClient"/> for now. Fails loudly rather than silently
    /// returning a wrong answer if ever routed here.
    /// </summary>
    public Task<string> TranscribePageAsync(
        string imagePath, int page, string model = "", CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Book-page transcription is not implemented for Gemini yet — route this call to Grok.");

    /// <inheritdoc cref="TranscribePageAsync"/>
    public Task<CharacterPageClassification> ClassifyCharactersOnImageAsync(
        string imagePath, int page, IReadOnlyList<CharacterClassifyHint> cast,
        string model = "", CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Character-page classification is not implemented for Gemini yet — route this call to Grok.");

    private static string NormalizeModelName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException(
                "Gemini: model is required. Open Settings and choose a model (no silent default).");
        var trimmed = model.Trim();
        if (trimmed.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["models/".Length..];
        // Strip provider prefix only — never rewrite to a different model id.
        return trimmed;
    }

    private async Task<string> SendAsync(
        Dictionary<string, object?> payload,
        string model,
        string kind,
        string? mode,
        string? promptForLog,
        string? userPromptForLog,
        int promptChars,
        int attemptNum,
        CancellationToken ct)
    {
        var key = ResolveApiKey();
        var modeTag = string.IsNullOrWhiteSpace(mode) ? null : mode.Trim();
        var targetModel = NormalizeModelName(model);
        var endpoint = $"models/{Uri.EscapeDataString(targetModel)}:generateContent";
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
                // Not every Gemini model supports thinkingConfig (confirmed live: gemini-2.5-flash
                // 400s with "Thinking level is not supported for this model"). Self-heal by
                // stripping it and retrying rather than maintaining a model-capability list.
                if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest
                    && payload.TryGetValue("generationConfig", out var gc)
                    && gc is Dictionary<string, object?> genConfig
                    && genConfig.ContainsKey("thinkingConfig")
                    && body.Contains("hinking", StringComparison.OrdinalIgnoreCase))
                {
                    var retryPayload = new Dictionary<string, object?>(payload);
                    var retryGenConfig = new Dictionary<string, object?>(genConfig);
                    retryGenConfig.Remove("thinkingConfig");
                    retryPayload["generationConfig"] = retryGenConfig;
                    return await SendAsync(retryPayload, model, kind, mode, promptForLog, userPromptForLog, promptChars, attemptNum, ct).ConfigureAwait(false);
                }

                await _telemetry.LogApiCallAsync(new ApiCallTelemetry
                {
                    Kind = kind,
                    Mode = modeTag,
                    Endpoint = endpoint,
                    Model = targetModel,
                    HttpStatus = (int)resp.StatusCode,
                    DurationMs = sw.ElapsedMilliseconds,
                    SystemPrompt = promptForLog,
                    UserPrompt = userPromptForLog,
                    PromptChars = promptChars,
                    Attempt = attemptNum,
                    Error = Trim(body, 800),
                    Ok = false,
                });
                throw ChatHttpStatusException.FromResponse(resp,
                    $"Gemini {endpoint} HTTP {(int)resp.StatusCode}: {Trim(body, 800)}");
            }

            using var doc = JsonDocument.Parse(body);
            var text = ExtractMessageText(doc.RootElement);
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
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
                Attempt = attemptNum,
                ResponsePreview = text.Length > 2000 ? text[..2000] : text,
                ResponseChars = text.Length,
                Ok = true,
            });
            _lastResolvedModel.Value = targetModel;
            return text;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = kind,
                Mode = modeTag,
                Endpoint = endpoint,
                Model = model,
                DurationMs = sw.ElapsedMilliseconds,
                SystemPrompt = promptForLog,
                UserPrompt = userPromptForLog,
                Attempt = attemptNum,
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

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];
}
