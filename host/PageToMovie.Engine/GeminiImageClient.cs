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
/// Google Gemini image generate + edit client for character portraits, via
/// <c>generateContent</c> with an image response modality (the "Gemini 3 image" / Imagen
/// family — see <see cref="ImageApiLimits.GeminiMaxReferenceImages"/>). Response-shape notes
/// below are built from Gemini's public documented format; verify against a live call before
/// relying on this in production.
/// </summary>
public sealed class GeminiImageClient : IImageClient
{
    public const string ApiBase = SupportedModelCatalog.GoogleApiBase;

    private readonly HttpClient _http;
    private readonly PageToMovieOptions _opts;
    private readonly ProjectTelemetryService _telemetry;
    private readonly ILogger<GeminiImageClient> _log;

    public GeminiImageClient(
        HttpClient http,
        IOptions<PageToMovieOptions> opts,
        ProjectTelemetryService telemetry,
        ILogger<GeminiImageClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _telemetry = telemetry;
        _log = log;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(ApiBase + "/");
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    /// <summary>Text-only portrait generation → n image blobs (one call per variant).</summary>
    public async Task<IReadOnlyList<byte[]>> GenerateVariantsAsync(
        string prompt,
        int n = 3,
        string aspectRatio = "1:1",
        string? model = null,
        CancellationToken ct = default)
    {
        var modelName = string.IsNullOrWhiteSpace(model) ? "gemini-3-pro-image" : model;
        var images = new List<byte[]>();
        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var one = await GenerateOneAsync(modelName, prompt, aspectRatio, null, ct).ConfigureAwait(false);
            if (one is not null)
                images.Add(one);
        }
        if (images.Count == 0)
            throw new InvalidOperationException("Gemini image API returned 0 usable images");
        return images;
    }

    /// <summary>Reference-guided edits (character continuity). One call per variant.</summary>
    /// <param name="costumeRefPath">
    /// Optional shared wardrobe-only reference (see <see cref="CharacterDesignService"/>
    /// uniform-lock flow) — attached last with an instruction to copy wardrobe only and
    /// ignore its face/identity.
    /// </param>
    public async Task<IReadOnlyList<byte[]>> EditVariantsAsync(
        string prompt,
        IReadOnlyList<string> referenceImagePaths,
        int n = 3,
        string aspectRatio = "1:1",
        string? model = null,
        int maxRefs = 0,
        string? costumeRefPath = null,
        bool illustratedMedium = true,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var modelName = string.IsNullOrWhiteSpace(model) ? "gemini-3-pro-image" : model;
        var cap = maxRefs > 0
            ? Math.Clamp(maxRefs, 1, ImageApiLimits.GeminiMaxReferenceImages)
            : ImageApiLimits.GeminiMaxReferenceImages;

        var hasCostumeRef = !string.IsNullOrWhiteSpace(costumeRefPath) && File.Exists(costumeRefPath);
        var identityCap = hasCostumeRef ? Math.Max(1, cap - 1) : cap;

        var refs = referenceImagePaths
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Take(identityCap)
            .ToList();
        if (refs.Count == 0 && !hasCostumeRef)
            throw new InvalidOperationException("No usable reference images for character edit.");

        var allRefs = hasCostumeRef ? refs.Append(costumeRefPath!).ToList() : refs;
        var costumeClause = hasCostumeRef
            ? " The LAST reference image is a COSTUME REFERENCE ONLY (shared wardrobe design) — " +
              "copy its coat, hat, and badge exactly; completely ignore any face or person in it; " +
              "this character's own face/identity comes from the other reference(s)/text, never from it." +
              (refs.Count > 0
                  ? " Conversely, ignore any hat/coat/badge visible in the OTHER reference(s) — " +
                    "wardrobe comes ONLY from this last costume image, even if the others show " +
                    "different or older wardrobe."
                  : "")
            : "";
        var mediumClause = illustratedMedium
            ? " Keep the illustrated/picture-book medium from the refs — not photoreal photography."
            : " Keep the photoreal live-action medium from the refs — not illustration, not cartoon.";

        var images = new List<byte[]>();
        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke($"edit variant {i + 1}/{n}");
            var variantPrompt = n > 1
                ? $"{prompt}{costumeClause}{mediumClause} Variation {i + 1} of {n}: tiny pose/expression change only; same identity."
                : $"{prompt}{costumeClause}{mediumClause}";
            var one = await GenerateOneAsync(modelName, variantPrompt, aspectRatio, allRefs, ct)
                .ConfigureAwait(false);
            if (one is not null)
                images.Add(one);
        }
        if (images.Count == 0)
            throw new InvalidOperationException("Gemini image edit returned 0 usable images");
        return images;
    }

    private async Task<byte[]?> GenerateOneAsync(
        string model,
        string prompt,
        string aspectRatio,
        IReadOnlyList<string>? referenceImagePaths,
        CancellationToken ct)
    {
        var parts = new List<object?>();
        if (referenceImagePaths is { Count: > 0 })
        {
            foreach (var path in referenceImagePaths)
            {
                var (mime, b64) = await FileToBase64Async(path, ct).ConfigureAwait(false);
                parts.Add(new Dictionary<string, object?>
                {
                    ["inline_data"] = new Dictionary<string, object?> { ["mime_type"] = mime, ["data"] = b64 },
                });
            }
        }
        parts.Add(new Dictionary<string, object?> { ["text"] = prompt });

        var payload = new Dictionary<string, object?>
        {
            ["contents"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["parts"] = parts },
            },
            ["generationConfig"] = new Dictionary<string, object?>
            {
                ["responseModalities"] = new[] { "IMAGE" },
                ["imageConfig"] = new Dictionary<string, object?> { ["aspectRatio"] = aspectRatio },
            },
        };

        var endpoint = $"models/{Uri.EscapeDataString(model)}:generateContent";
        var sw = Stopwatch.StartNew();
        var refNames = (referenceImagePaths ?? Array.Empty<string>())
            .Select(Path.GetFileName).Where(x => x is not null).Cast<string>().ToList();
        try
        {
            // Per-request API key — never mutate shared DefaultRequestHeaders (multi-user race).
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload),
            };
            var key = ResolveApiKey();
            if (!string.IsNullOrWhiteSpace(key))
                req.Headers.TryAddWithoutValidation("x-goog-api-key", key.Trim());
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _telemetry.LogApiCall(new ApiCallTelemetry
                {
                    Kind = referenceImagePaths is { Count: > 0 } ? "image_edit" : "image",
                    Endpoint = endpoint,
                    Model = model,
                    HttpStatus = (int)resp.StatusCode,
                    DurationMs = sw.ElapsedMilliseconds,
                    Prompt = prompt,
                    PromptChars = prompt.Length,
                    ReferenceImagePaths = refNames.Count > 0 ? refNames : null,
                    RefsAttached = refNames.Count > 0,
                    Error = Trim(body, 400),
                    Ok = false,
                });
                throw new InvalidOperationException(
                    $"Gemini {endpoint} HTTP {(int)resp.StatusCode}: {Trim(body, 400)}");
            }

            var image = ExtractInlineImage(body);
            _telemetry.LogApiCall(new ApiCallTelemetry
            {
                Kind = referenceImagePaths is { Count: > 0 } ? "image_edit" : "image",
                Endpoint = endpoint,
                Model = model,
                HttpStatus = (int)resp.StatusCode,
                DurationMs = sw.ElapsedMilliseconds,
                Prompt = prompt,
                PromptChars = prompt.Length,
                ReferenceImagePaths = refNames.Count > 0 ? refNames : null,
                RefsAttached = refNames.Count > 0,
                ImageCount = image is null ? 0 : 1,
                Ok = image is not null,
                Error = image is null ? "no inline image data in response" : null,
            });
            return image;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _telemetry.LogApiCall(new ApiCallTelemetry
            {
                Kind = referenceImagePaths is { Count: > 0 } ? "image_edit" : "image",
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

    /// <summary>
    /// Gemini image response: <c>candidates[0].content.parts[].inlineData.data</c> (base64).
    /// Returns the first inline image part found, or null if the response was text-only.
    /// Public so tests can exercise it against sample payloads without a live API call.
    /// </summary>
    public static byte[]? ExtractInlineImage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
            return null;

        var c0 = candidates[0];
        if (!c0.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("inlineData", out var inline) &&
                inline.TryGetProperty("data", out var dataEl) &&
                dataEl.GetString() is { Length: > 0 } b64)
            {
                return Convert.FromBase64String(b64);
            }
            // Some responses use snake_case for this field depending on API version.
            if (part.TryGetProperty("inline_data", out var inlineSnake) &&
                inlineSnake.TryGetProperty("data", out var dataElSnake) &&
                dataElSnake.GetString() is { Length: > 0 } b64Snake)
            {
                return Convert.FromBase64String(b64Snake);
            }
        }
        return null;
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

    private static string? ResolveApiKey() =>
        ApiKeyScope.CurrentGemini
        ?? Environment.GetEnvironmentVariable(SupportedModelCatalog.GoogleApiKeyEnv);

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];
}
