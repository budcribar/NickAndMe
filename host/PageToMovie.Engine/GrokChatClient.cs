using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>xAI chat/completions client for Stage 1 scene bible generation.</summary>
public sealed class GrokChatClient : IChatClient
{
    public const string ApiBase = "https://api.x.ai/v1";

    private readonly HttpClient _http;
    private readonly ProjectTelemetryService _telemetry;
    private readonly ILogger<GrokChatClient> _log;

    public GrokChatClient(
        HttpClient http,
        IOptions<PageToMovieOptions> opts,
        ProjectTelemetryService telemetry,
        ILogger<GrokChatClient> log)
    {
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
        string model = "grok-4.5",
        double temperature = 0.2,
        CancellationToken ct = default,
        string? mode = null)
    {
        var key = ResolveApiKey();
        var modeTag = string.IsNullOrWhiteSpace(mode) ? null : mode.Trim();
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["temperature"] = temperature,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "system", ["content"] = systemPrompt },
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = userPrompt },
            },
        };

        var sw = Stopwatch.StartNew();
        try
        {
            // Auth on a per-request message, not _http.DefaultRequestHeaders: this client is a
            // singleton shared by every concurrent classifier call, and mutating shared headers
            // per-call is a race (one call's key can leak into or clobber another's in flight).
            using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = JsonContent.Create(payload),
            };
            if (!string.IsNullOrWhiteSpace(key))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _telemetry.LogApiCall(new ApiCallTelemetry
                {
                    Kind = "chat",
                    Mode = modeTag,
                    Endpoint = "chat/completions",
                    Model = model,
                    HttpStatus = (int)resp.StatusCode,
                    DurationMs = sw.ElapsedMilliseconds,
                    SystemPrompt = systemPrompt,
                    UserPrompt = userPrompt,
                    PromptChars = (systemPrompt?.Length ?? 0) + (userPrompt?.Length ?? 0),
                    Error = Trim(body, 800),
                    Ok = false,
                });
                throw new InvalidOperationException(
                    $"Grok chat HTTP {(int)resp.StatusCode}: {Trim(body, 800)}");
            }

            using var doc = JsonDocument.Parse(body);
            var text = ExtractMessageText(doc.RootElement);
            _telemetry.LogApiCall(new ApiCallTelemetry
            {
                Kind = "chat",
                Mode = modeTag,
                Endpoint = "chat/completions",
                Model = model,
                HttpStatus = (int)resp.StatusCode,
                DurationMs = sw.ElapsedMilliseconds,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt,
                PromptChars = (systemPrompt?.Length ?? 0) + (userPrompt?.Length ?? 0),
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
                Kind = "chat",
                Mode = modeTag,
                Endpoint = "chat/completions",
                Model = model,
                DurationMs = sw.ElapsedMilliseconds,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt,
                Error = ex.Message,
                Ok = false,
            });
            throw;
        }
    }

    public static Dictionary<string, object?> ParseJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("No JSON object in model output");
        text = text.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = Regex.Replace(text, @"^```(?:json)?\s*", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s*```\s*$", "");
        }
        // Prefer first balanced/parseable object — avoid matching braces in preamble like "{high}".
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '{') continue;
            for (var j = text.Length - 1; j > i; j--)
            {
                if (text[j] != '}') continue;
                var blob = text[i..(j + 1)];
                try
                {
                    using var doc = JsonDocument.Parse(blob);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        return JsonElementToDict(doc.RootElement);
                }
                catch
                {
                    /* try next span */
                }
            }
        }
        throw new InvalidOperationException("No JSON object in model output");
    }

    private static Dictionary<string, object?> JsonElementToDict(JsonElement el)
    {
        var d = new Dictionary<string, object?>();
        foreach (var p in el.EnumerateObject())
            d[p.Name] = JsonElementToObject(p.Value);
        return d;
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => JsonElementToDict(el),
        JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var i) ? i : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.GetRawText(),
    };

    /// <summary>Test/helper for extracting assistant text from chat completion JSON.</summary>
    public static string ExtractMessageTextForTests(JsonElement result) => ExtractMessageText(result);

    private static string ExtractMessageText(JsonElement result)
    {
        if (result.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var c0 = choices[0];
            if (c0.ValueKind == JsonValueKind.Object &&
                c0.TryGetProperty("message", out var msg) &&
                msg.ValueKind == JsonValueKind.Object &&
                msg.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                    return content.GetString() ?? "";
                if (content.ValueKind == JsonValueKind.Array)
                {
                    var parts = new List<string>();
                    foreach (var c in content.EnumerateArray())
                    {
                        if (c.ValueKind == JsonValueKind.String)
                            parts.Add(c.GetString() ?? "");
                        else if (c.TryGetProperty("text", out var t))
                            parts.Add(t.GetString() ?? "");
                    }
                    return string.Join("\n", parts);
                }
            }
        }
        if (result.TryGetProperty("output_text", out var ot) && ot.GetString() is { Length: > 0 } s)
            return s;
        var raw = result.GetRawText();
        return raw.Length <= 2000 ? raw : raw[..2000];
    }

    private static string? ResolveApiKey() =>
        Abstractions.ApiKeyScope.Current ?? Environment.GetEnvironmentVariable("XAI_API_KEY");

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];
}
