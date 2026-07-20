using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FilmStudio.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using FilmStudio.Engine.Abstractions;

namespace FilmStudio.Engine;

/// <summary>xAI /responses vision transcription for book page images.</summary>
public sealed class GrokVisionClient : IGrokVisionClient
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
    private readonly ProjectTelemetryService _telemetry;
    private readonly ILogger<GrokVisionClient> _log;

    public GrokVisionClient(
        HttpClient http,
        IOptions<FilmStudioOptions> opts,
        ProjectTelemetryService telemetry,
        ILogger<GrokVisionClient> log)
    {
        _http = http;
        _telemetry = telemetry;
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
        var payload = BuildVisionPayload(
            model,
            dataUri,
            detail: "high",
            text: $"Page {page} of the book.\n\n{TranscribePrompt}");

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

    /// <summary>
    /// Ask Grok which cast members are visibly illustrated on a book page.
    /// Text-only / no-figure pages return PageKind = text_heavy and empty matches.
    /// </summary>
    public async Task<CharacterPageClassification> ClassifyCharactersOnImageAsync(
        string imagePath,
        int page,
        IReadOnlyList<CharacterClassifyHint> cast,
        string model = "grok-4.5",
        CancellationToken ct = default)
    {
        EnsureAuth();
        if (cast.Count == 0)
            return new CharacterPageClassification { Page = page, PageKind = "unknown" };

        var castLines = cast.Select(c =>
        {
            var desc = string.IsNullOrWhiteSpace(c.Description)
                ? ""
                : $" — {Trim(c.Description.Replace('\n', ' '), 160)}";
            return $"- key={c.Key} | name={c.DisplayName}{desc}";
        });
        var prompt =
            "You are sorting illustrated children's book pages onto a film cast list.\n\n" +
            $"This is book page image #{page} (file may be a full page or crop).\n\n" +
            "Cast (use ONLY these keys):\n" +
            string.Join("\n", castLines) + "\n\n" +
            "Task: decide which cast members are VISIBLY ILLUSTRATED as figures in this image.\n" +
            "Rules:\n" +
            "- If the image is mostly printed story text, a word list, or blank with no character art: " +
            "page_kind=\"text_heavy\", characters=[].\n" +
            "- If it is a picture (cover, spot art, full-bleed scene) with figures: page_kind=\"illustration\".\n" +
            "- Mixed text+art with clear figures: page_kind=\"mixed\".\n" +
            "- Only match characters you can see drawn/painted in the art. Do not match from text names alone " +
            "if there is no figure for them.\n" +
            "- A hand, arm, foot, or silhouette alone is NOT enough to match an adult (Mom/Dad) — " +
            "require face and/or clear upper body of that person.\n" +
            "- If the only clear figure is the animal/hero dog, match only that cast key — do NOT invent " +
            "Mom/Dad just because a bed/home scene implies parents.\n" +
            "- Narrator / voice-only roles are never visual matches.\n" +
            "- confidence is 0..1 for how sure you are the figure is that cast member.\n" +
            "- primary_character_key = best single identity plate candidate (face-forward if possible), or null.\n\n" +
            "Respond with JSON ONLY (no markdown):\n" +
            "{\n" +
            "  \"page_kind\": \"illustration\"|\"text_heavy\"|\"mixed\"|\"unknown\",\n" +
            "  \"primary_character_key\": \"Character_...\"|null,\n" +
            "  \"characters\": [\n" +
            "    {\"key\":\"Character_...\",\"visible\":true,\"confidence\":0.0,\"notes\":\"short\"}\n" +
            "  ]\n" +
            "}\n";

        var dataUri = await FileToDataUriAsync(imagePath, ct);
        // low detail is enough for "who is on this page" and cheaper/faster for many pages
        var payload = BuildVisionPayload(model, dataUri, detail: "low", text: prompt);

        using var resp = await _http.PostAsJsonAsync("responses", payload, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Grok vision classify HTTP {(int)resp.StatusCode}: {Trim(body, 500)}");

        using var doc = JsonDocument.Parse(body);
        var text = ExtractResponseText(doc.RootElement);
        text = Regex.Replace(text.Trim(), @"^```(?:json)?\s*", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s*```$", "").Trim();

        return ParseClassification(text, page, cast);
    }

    private static Dictionary<string, object?> BuildVisionPayload(
        string model,
        string dataUri,
        string detail,
        string text) =>
        new()
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
                            ["detail"] = detail,
                        },
                        new Dictionary<string, object?>
                        {
                            ["type"] = "input_text",
                            ["text"] = text,
                        },
                    },
                },
            },
        };

    private static CharacterPageClassification ParseClassification(
        string text,
        int page,
        IReadOnlyList<CharacterClassifyHint> cast)
    {
        var result = new CharacterPageClassification { Page = page, PageKind = "unknown", Raw = text };
        var allowed = new HashSet<string>(cast.Select(c => c.Key), StringComparer.OrdinalIgnoreCase);

        try
        {
            // Extract first JSON object if model added preamble
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
                return result;
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.TryGetProperty("page_kind", out var pk))
                result.PageKind = (pk.GetString() ?? "unknown").Trim().ToLowerInvariant();
            if (root.TryGetProperty("primary_character_key", out var prim) &&
                prim.ValueKind == JsonValueKind.String &&
                prim.GetString() is { Length: > 0 } pkey &&
                allowed.Contains(pkey))
                result.PrimaryCharacterKey = pkey;

            if (root.TryGetProperty("characters", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var key = item.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(key) || !allowed.Contains(key)) continue;
                    var visible = true;
                    if (item.TryGetProperty("visible", out var v))
                    {
                        visible = v.ValueKind switch
                        {
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.String => !string.Equals(v.GetString(), "false", StringComparison.OrdinalIgnoreCase),
                            _ => true,
                        };
                    }
                    if (!visible) continue;
                    var conf = 0.5;
                    if (item.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var cd))
                        conf = Math.Clamp(cd, 0, 1);
                    var notes = item.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";
                    result.Matches.Add(new CharacterPageMatch
                    {
                        Key = key,
                        Confidence = conf,
                        Notes = notes,
                    });
                }
            }

            // Promote primary if listed with no matches
            if (result.Matches.Count == 0 &&
                result.PrimaryCharacterKey is { Length: > 0 } pk2 &&
                result.PageKind is not ("text_heavy" or "text"))
            {
                result.Matches.Add(new CharacterPageMatch
                {
                    Key = pk2,
                    Confidence = 0.55,
                    Notes = "primary_only",
                });
            }
        }
        catch (Exception)
        {
            result.PageKind = "parse_error";
        }

        // Never keep matches on hard text-only pages
        if (result.PageKind is "text_heavy" or "text")
        {
            result.Matches.Clear();
            result.PrimaryCharacterKey = null;
        }

        return result;
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

    /// <inheritdoc />
    public async Task<string> CompleteWithImagesAsync(
        string prompt,
        IReadOnlyList<string> imagePaths,
        string model = "grok-4.5",
        string detail = "low",
        CancellationToken ct = default)
    {
        EnsureAuth();
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("prompt required", nameof(prompt));
        var paths = (imagePaths ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Take(8)
            .ToList();
        if (paths.Count == 0)
            throw new InvalidOperationException("At least one image path required for vision review.");

        var content = new List<object?>();
        foreach (var path in paths)
        {
            content.Add(new Dictionary<string, object?>
            {
                ["type"] = "input_image",
                ["image_url"] = await FileToDataUriAsync(path, ct),
                ["detail"] = string.IsNullOrWhiteSpace(detail) ? "low" : detail,
            });
        }
        content.Add(new Dictionary<string, object?>
        {
            ["type"] = "input_text",
            ["text"] = prompt,
        });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = content,
                },
            },
        };

        var sw = Stopwatch.StartNew();
        var imageNames = paths.Select(Path.GetFileName).Where(n => n is not null).Cast<string>().ToList();
        try
        {
            using var resp = await _http.PostAsJsonAsync("responses", payload, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _telemetry.LogApiCall(new ApiCallTelemetry
                {
                    Kind = "vision",
                    Endpoint = "responses",
                    Model = model,
                    HttpStatus = (int)resp.StatusCode,
                    DurationMs = sw.ElapsedMilliseconds,
                    Prompt = prompt,
                    PromptChars = prompt.Length,
                    ReferenceImagePaths = imageNames,
                    ImageCount = paths.Count,
                    Error = Trim(body, 500),
                    Ok = false,
                });
                throw new InvalidOperationException(
                    $"Grok vision multi-image HTTP {(int)resp.StatusCode}: {Trim(body, 500)}");
            }

            using var doc = JsonDocument.Parse(body);
            var text = ExtractResponseText(doc.RootElement);
            text = Regex.Replace(text.Trim(), @"^```(?:\w+)?\s*", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s*```$", "").Trim();
            _telemetry.LogApiCall(new ApiCallTelemetry
            {
                Kind = "vision",
                Endpoint = "responses",
                Model = model,
                HttpStatus = (int)resp.StatusCode,
                DurationMs = sw.ElapsedMilliseconds,
                Prompt = prompt,
                PromptChars = prompt.Length,
                ReferenceImagePaths = imageNames,
                ImageCount = paths.Count,
                ResponsePreview = text.Length > 2000 ? text[..2000] : text,
                ResponseChars = text.Length,
                Ok = true,
            });
            return text;
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not ArgumentException)
        {
            _telemetry.LogApiCall(new ApiCallTelemetry
            {
                Kind = "vision",
                Endpoint = "responses",
                Model = model,
                DurationMs = sw.ElapsedMilliseconds,
                Prompt = prompt,
                ReferenceImagePaths = imageNames,
                Error = ex.Message,
                Ok = false,
            });
            throw;
        }
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

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];
}

public sealed class CharacterClassifyHint
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class CharacterPageClassification
{
    public int Page { get; set; }
    /// <summary>illustration | text_heavy | mixed | unknown | parse_error</summary>
    public string PageKind { get; set; } = "unknown";
    public string? PrimaryCharacterKey { get; set; }
    public List<CharacterPageMatch> Matches { get; set; } = new();
    public string? Raw { get; set; }
}

public sealed class CharacterPageMatch
{
    public string Key { get; set; } = "";
    public double Confidence { get; set; }
    public string Notes { get; set; } = "";
}
