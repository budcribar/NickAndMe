using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FilmStudio.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

/// <summary>xAI /responses vision transcription for book page images.</summary>
public sealed class GrokVisionClient
{
    public const string ApiBase = "https://api.x.ai/v1";

    private const string TranscribePrompt =
        "You are transcribing a children's / illustrated book page.\n\n" +
        "Task: extract ALL readable printed text on this page (title, body, dialogue).\n" +
        "Rules:\n" +
        "- Preserve verse line breaks when it looks like rhyme/poetry.\n" +
        "- Fix obvious OCR-style noise only if the letters on the page are clear; otherwise write what you see.\n" +
        "- Do NOT invent story, paraphrase, or add scene descriptions.\n" +
        "- If the page is illustration-only with no readable words, output exactly: (illustration only)\n" +
        "- Output plain text only — no markdown, no JSON, no preamble.\n";

    private readonly HttpClient _http;
    private readonly ILogger<GrokVisionClient> _log;

    public GrokVisionClient(
        HttpClient http,
        IOptions<FilmStudioOptions> opts,
        ILogger<GrokVisionClient> log)
    {
        _http = http;
        _log = log;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(ApiBase + "/");
    }

    public bool IsConfigured
    {
        get
        {
            EnsureAuth();
            return _http.DefaultRequestHeaders.Authorization is not null;
        }
    }

    public async Task<string> TranscribePageAsync(
        string imagePath,
        int page,
        string model = "grok-4.5",
        CancellationToken ct = default)
    {
        EnsureAuth();
        var dataUri = await FileToDataUriAsync(imagePath, ct);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "input_image",
                            ["image_url"] = dataUri,
                            ["detail"] = "high",
                        },
                        new Dictionary<string, object?>
                        {
                            ["type"] = "input_text",
                            ["text"] = $"Page {page} of the book.\n\n{TranscribePrompt}",
                        },
                    },
                },
            },
        };

        using var resp = await _http.PostAsJsonAsync("responses", payload, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Grok vision HTTP {(int)resp.StatusCode}: {Trim(body, 500)}");

        using var doc = JsonDocument.Parse(body);
        var text = ExtractResponseText(doc.RootElement);
        text = Regex.Replace(text.Trim(), @"^```(?:\w+)?\s*", "");
        text = Regex.Replace(text, @"\s*```$", "").Trim();
        return text;
    }

    private static string ExtractResponseText(JsonElement result)
    {
        if (result.TryGetProperty("output_text", out var ot) &&
            ot.GetString() is { Length: > 0 } direct)
            return direct;

        if (result.TryGetProperty("output", out var output) &&
            output.ValueKind == JsonValueKind.Array)
        {
            var texts = new List<string>();
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("content", out var content)) continue;
                if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.Object) continue;
                        var type = part.TryGetProperty("type", out var t) ? t.GetString() : null;
                        if (type is "output_text" or "text" &&
                            part.TryGetProperty("text", out var tx) &&
                            tx.GetString() is { Length: > 0 } s)
                            texts.Add(s);
                    }
                }
                else if (content.ValueKind == JsonValueKind.String &&
                         content.GetString() is { Length: > 0 } cs)
                {
                    texts.Add(cs);
                }
            }
            if (texts.Count > 0)
                return string.Join("\n", texts);
        }

        if (result.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var c0 = choices[0];
            if (c0.TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var mc) &&
                mc.GetString() is { Length: > 0 } mcs)
                return mcs;
        }

        return result.GetRawText()[..Math.Min(500, result.GetRawText().Length)];
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

    private void EnsureAuth()
    {
        if (_http.DefaultRequestHeaders.Authorization is not null)
            return;
        var key = Environment.GetEnvironmentVariable("XAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return;
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", key.Trim());
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];
}
