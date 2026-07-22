using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FilmStudio.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

using FilmStudio.Engine.Abstractions;

namespace FilmStudio.Engine;

/// <summary>xAI Grok Imagine image generate + edit client for character portraits.</summary>
public sealed class GrokImageClient : IImageClient
{
    public const string ApiBase = "https://api.x.ai/v1";

    private readonly HttpClient _http;
    private readonly FilmStudioOptions _opts;
    private readonly ProjectTelemetryService _telemetry;
    private readonly ILogger<GrokImageClient> _log;

    public GrokImageClient(
        HttpClient http,
        IOptions<FilmStudioOptions> opts,
        ProjectTelemetryService telemetry,
        ILogger<GrokImageClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _telemetry = telemetry;
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

    /// <summary>Text-only portrait generation → n image blobs.</summary>
    public async Task<IReadOnlyList<byte[]>> GenerateVariantsAsync(
        string prompt,
        int n = 3,
        string aspectRatio = "1:1",
        string? model = null,
        CancellationToken ct = default)
    {
        EnsureAuthHeader();
        var modelName = string.IsNullOrWhiteSpace(model)
            ? _opts.DefaultImageModel
            : model;
        var payload = new Dictionary<string, object?>
        {
            ["model"] = modelName,
            ["prompt"] = prompt,
            ["n"] = n,
            ["aspect_ratio"] = aspectRatio,
            ["response_format"] = "b64_json",
        };

        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await _http.PostAsJsonAsync("images/generations", payload, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _telemetry.LogApiCall(new ApiCallTelemetry
                {
                    Kind = "image",
                    Endpoint = "images/generations",
                    Model = modelName,
                    HttpStatus = (int)resp.StatusCode,
                    DurationMs = sw.ElapsedMilliseconds,
                    Prompt = prompt,
                    PromptChars = prompt?.Length ?? 0,
                    ImageCount = n,
                    Error = Trim(body, 400),
                    Ok = false,
                });
                throw new InvalidOperationException(
                    $"Grok image generations HTTP {(int)resp.StatusCode}: {Trim(body, 400)}");
            }

            var images = ParseImageResponse(body, n, "generations");
            if (images.Count < n)
            {
                _telemetry.LogApiCall(new ApiCallTelemetry
                {
                    Kind = "image",
                    Endpoint = "images/generations",
                    Model = modelName,
                    HttpStatus = (int)resp.StatusCode,
                    DurationMs = sw.ElapsedMilliseconds,
                    Prompt = prompt,
                    PromptChars = prompt?.Length ?? 0,
                    ImageCount = images.Count,
                    Error = $"returned {images.Count}/{n} images",
                    Ok = false,
                });
                throw new InvalidOperationException(
                    $"Grok image API returned {images.Count}/{n} usable images");
            }

            _telemetry.LogApiCall(new ApiCallTelemetry
            {
                Kind = "image",
                Endpoint = "images/generations",
                Model = modelName,
                HttpStatus = (int)resp.StatusCode,
                DurationMs = sw.ElapsedMilliseconds,
                Prompt = prompt,
                PromptChars = prompt?.Length ?? 0,
                ImageCount = images.Count,
                Ok = true,
            });
            return images;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _telemetry.LogApiCall(new ApiCallTelemetry
            {
                Kind = "image",
                Endpoint = "images/generations",
                Model = modelName,
                DurationMs = sw.ElapsedMilliseconds,
                Prompt = prompt,
                Error = ex.Message,
                Ok = false,
            });
            throw;
        }
    }

    /// <summary>
    /// Reference-guided edits (book plates). One API call per variant for reliability.
    /// </summary>
    /// <param name="costumeRefPath">
    /// Optional wardrobe-only reference (see <see cref="CharacterDesignService"/> uniform-lock
    /// flow): a shared, faceless/generic costume plate reused across several characters so their
    /// coat/hat/badge design stays pixel-identical. Attached as the LAST reference image with an
    /// instruction to copy wardrobe only and ignore its face — never treated as an identity ref.
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
        EnsureAuthHeader();
        var modelName = string.IsNullOrWhiteSpace(model)
            ? _opts.DefaultImageModel
            : model;
        // Grok Imagine multi-image edit hard cap is 3; never send more than provider allows
        var cap = maxRefs > 0
            ? Math.Clamp(maxRefs, 1, ImageApiLimits.GrokMaxReferenceImages)
            : ImageApiLimits.MaxReferenceImages(_opts.ImageProvider, modelName);
        // This client is Grok-only — always enforce Grok cap even if project says gemini
        cap = Math.Min(cap, ImageApiLimits.GrokMaxReferenceImages);

        var hasCostumeRef = !string.IsNullOrWhiteSpace(costumeRefPath) && File.Exists(costumeRefPath);
        // Reserve one slot for the costume ref so identity refs + costume ref never exceed cap
        var identityCap = hasCostumeRef ? Math.Max(1, cap - 1) : cap;

        var refs = referenceImagePaths
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Take(identityCap)
            .ToList();
        if (refs.Count == 0 && !hasCostumeRef)
            throw new InvalidOperationException("No usable reference images for character edit.");

        // Downscale book plates — three full-page PNGs as data URIs (~7MB+) often fail;
        // two can succeed by luck under the request size limit.
        var imageUris = new List<string>(refs.Count + (hasCostumeRef ? 1 : 0));
        foreach (var path in refs)
            imageUris.Add(await FileToDataUriAsync(path, ct, maxEdge: 1024, jpegQuality: 85)
                .ConfigureAwait(false));
        var identityCount = imageUris.Count;
        var costumeIndex = -1;
        if (hasCostumeRef)
        {
            imageUris.Add(await FileToDataUriAsync(costumeRefPath!, ct, maxEdge: 1024, jpegQuality: 85)
                .ConfigureAwait(false));
            costumeIndex = imageUris.Count - 1;
        }

        var refNames = refs.Select(Path.GetFileName).Where(x => x is not null).Cast<string>().ToList();
        if (hasCostumeRef && Path.GetFileName(costumeRefPath) is { } costumeFileName)
            refNames.Add(costumeFileName);
        var images = new List<byte[]>();
        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke($"edit variant {i + 1}/{n}");

            var orderHint = identityCount switch
            {
                > 1 => BuildMultiImageOrderHint(identityCount),
                1 => costumeIndex >= 0
                    // Explicit index (not "the attached reference") once a second, costume-only
                    // image is also in play — an unindexed identity reference next to an
                    // explicitly-indexed <IMAGE_1> costume ref is exactly the kind of ambiguity
                    // that lets the model split the difference on wardrobe between the two.
                    ? "<IMAGE_0> is the character identity AND art style reference (highest priority over text). "
                    : "Match the attached reference identity AND illustration style (highest priority over text). ",
                _ => "",
            };
            if (costumeIndex >= 0)
            {
                var identityLabel = identityCount switch
                {
                    0 => "",
                    1 => "<IMAGE_0>",
                    _ => $"<IMAGE_0>..<IMAGE_{identityCount - 1}>",
                };
                orderHint +=
                    $"<IMAGE_{costumeIndex}> is a COSTUME REFERENCE ONLY (shared wardrobe design) — " +
                    "copy its coat, hat/cap, badge, and garment details exactly. " +
                    "COMPLETELY IGNORE any face, body, or person shown in that image — " +
                    "this character's own face and identity must come from " +
                    (identityCount > 0 ? "the other reference image(s) and " : "") +
                    "the text description below, never from the costume reference. " +
                    (identityCount > 0
                        ? $"Conversely, IGNORE any hat/coat/badge visible in {identityLabel} — " +
                          $"wardrobe comes ONLY from <IMAGE_{costumeIndex}>, even if {identityLabel} shows " +
                          "different or older wardrobe. "
                        : "");
            }
            var variantTail = illustratedMedium
                ? (n > 1
                    ? $" Variation {i + 1} of {n}: tiny pose/expression change only; " +
                      "same identity, markings, and illustrated medium as the book references. "
                    : " Single refined continuity portrait in the book’s illustration style. ")
                : (n > 1
                    ? $" Variation {i + 1} of {n}: tiny pose/expression change only; " +
                      "same identity, markings, and photoreal medium as the reference(s). "
                    : " Single refined photoreal continuity portrait matching the reference(s). ");
            var mediumClause = illustratedMedium
                ? "Keep the children's picture-book illustration style from the refs — not photoreal photography. "
                : "Keep the photoreal live-action look from the refs — NOT illustration, NOT cartoon, NOT painted/drawn medium. ";
            var variantPrompt =
                orderHint +
                prompt +
                variantTail +
                mediumClause +
                "If refs show no clothing, do not invent costumes. " +
                "No labels, no redesign, no model sheet.";

            var sw = Stopwatch.StartNew();
            try
            {
                var body = await PostImageEditAsync(
                    modelName, variantPrompt, aspectRatio, imageUris, onProgress, ct)
                    .ConfigureAwait(false);
                if (body is null)
                {
                    _telemetry.LogApiCall(new ApiCallTelemetry
                    {
                        Kind = "image_edit",
                        Endpoint = "images/edits",
                        Model = modelName,
                        DurationMs = sw.ElapsedMilliseconds,
                        Prompt = variantPrompt,
                        PromptChars = variantPrompt.Length,
                        ReferenceImagePaths = refNames,
                        RefsAttached = true,
                        Attempt = i + 1,
                        Error = "empty response",
                        Ok = false,
                    });
                    throw new InvalidOperationException(
                        $"Image edit failed (variant {i + 1}): empty response");
                }

                var batch = ParseImageResponse(body, 1, $"edits variant {i + 1}");
                images.AddRange(batch);
                _telemetry.LogApiCall(new ApiCallTelemetry
                {
                    Kind = "image_edit",
                    Endpoint = "images/edits",
                    Model = modelName,
                    DurationMs = sw.ElapsedMilliseconds,
                    Prompt = variantPrompt,
                    PromptChars = variantPrompt.Length,
                    ReferenceImagePaths = refNames,
                    RefsAttached = true,
                    ImageCount = batch.Count,
                    Attempt = i + 1,
                    Ok = true,
                });
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                _telemetry.LogApiCall(new ApiCallTelemetry
                {
                    Kind = "image_edit",
                    Endpoint = "images/edits",
                    Model = modelName,
                    DurationMs = sw.ElapsedMilliseconds,
                    Prompt = variantPrompt,
                    ReferenceImagePaths = refNames,
                    Attempt = i + 1,
                    Error = ex.Message,
                    Ok = false,
                });
                throw;
            }
        }

        if (images.Count < 1)
            throw new InvalidOperationException("Image edit returned no variants.");
        return images.Take(n).ToList();
    }

    /// <summary>
    /// xAI multi-image: use <c>images</c> (array of data URI strings), mutually exclusive with <c>image</c>.
    /// Prompt cites &lt;IMAGE_0&gt;, &lt;IMAGE_1&gt;, … Single-image keeps <c>image</c> as a string / {url}.
    /// </summary>
    private async Task<string?> PostImageEditAsync(
        string modelName,
        string variantPrompt,
        string aspectRatio,
        IReadOnlyList<string> imageUris,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        async Task<(bool Ok, int Code, string Body)> SendAsync(JsonObject payload)
        {
            using var content = new StringContent(
                payload.ToJsonString(),
                Encoding.UTF8,
                "application/json");
            using var resp = await _http.PostAsync("images/edits", content, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
        }

        JsonObject BasePayload() => new()
        {
            ["model"] = modelName,
            ["prompt"] = variantPrompt,
            ["response_format"] = "b64_json",
            ["aspect_ratio"] = aspectRatio,
        };

        if (imageUris.Count > 1)
        {
            // Primary multi-ref shape per xAI: "images": [ dataUri, ... ]
            var arr = new JsonArray();
            foreach (var u in imageUris)
                arr.Add(u);
            var multi = BasePayload();
            multi["images"] = arr;
            var (ok, _, body) = await SendAsync(multi).ConfigureAwait(false);
            if (ok) return body;

            // Fallback: "image" as string[] (older / alternate parsers)
            var alt = BasePayload();
            alt["image"] = arr.DeepClone();
            var (ok2, _, body2) = await SendAsync(alt).ConfigureAwait(false);
            if (ok2) return body2;

            // Last resort: drop last ref(s) so 3→2 still produces a portrait
            if (imageUris.Count >= 3)
            {
                onProgress?.Invoke(
                    $"3 reference images rejected by API — retrying with first 2…");
                var two = imageUris.Take(2).ToList();
                var prompt2 = variantPrompt
                    .Replace("<IMAGE_2>", "", StringComparison.Ordinal)
                    .Replace("Image 3", "Image 2", StringComparison.OrdinalIgnoreCase);
                // Rebuild shorter multi prompt tip
                prompt2 = BuildMultiImageOrderHint(2) +
                          // strip old multi-hint if present by only using short prompt tail after first period? keep full
                          variantPrompt;
                // Simpler: just use 2-image order hint + original user prompt body
                var cut = variantPrompt.IndexOf("CHARACTER CONTINUITY", StringComparison.OrdinalIgnoreCase);
                if (cut < 0)
                    cut = variantPrompt.IndexOf("IDENTITY", StringComparison.OrdinalIgnoreCase);
                var core = cut >= 0 ? variantPrompt[cut..] : variantPrompt;
                prompt2 = BuildMultiImageOrderHint(2) + core;

                return await PostImageEditAsync(
                    modelName, prompt2, aspectRatio, two, onProgress, ct).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"Image edit failed: {Trim(body.Length > 0 ? body : body2, 400)}");
        }

        // Single image: "image" as data-URI string, then { "url": ... }
        {
            var p = BasePayload();
            p["image"] = imageUris[0];
            var (ok, _, body) = await SendAsync(p).ConfigureAwait(false);
            if (ok) return body;

            var p2 = BasePayload();
            p2["image"] = new JsonObject { ["url"] = imageUris[0] };
            var (ok2, _, body2) = await SendAsync(p2).ConfigureAwait(false);
            if (ok2) return body2;

            throw new InvalidOperationException(
                $"Image edit failed: {Trim(body2.Length > 0 ? body2 : body, 400)}");
        }
    }

    private static string BuildMultiImageOrderHint(int count)
    {
        var sb = new StringBuilder();
        sb.Append("Multi-reference edit. ");
        for (var i = 0; i < count; i++)
            sb.Append($"<IMAGE_{i}> is reference {i + 1}. ");
        sb.Append("<IMAGE_0> is the identity / style lock (highest priority). ");
        if (count > 1)
            sb.Append("Later images are the SAME character for markings and style only. ");
        return sb.ToString();
    }

    private static List<byte[]> ParseImageResponse(string json, int n, string label)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array ||
            data.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                $"Grok image API returned no image data ({label}): {Trim(json, 300)}");
        }

        var images = new List<byte[]>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            if (item.TryGetProperty("b64_json", out var b64) &&
                b64.GetString() is { Length: > 0 } s)
            {
                images.Add(Convert.FromBase64String(s));
            }
            // URL form is rare with response_format=b64_json; skip for now
        }

        if (images.Count < 1)
            throw new InvalidOperationException(
                $"Grok image API returned 0 usable images ({label})");
        return images.Take(n).ToList();
    }

    /// <summary>
    /// Encode a local image as a data URI. Large book pages are downscaled (Skia)
    /// so multi-ref edits (up to 3) stay under API body limits.
    /// </summary>
    private async Task<string> FileToDataUriAsync(
        string path,
        CancellationToken ct,
        int maxEdge = 1280,
        int jpegQuality = 88)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        try
        {
            using var original = SKBitmap.Decode(bytes);
            if (original is null)
                return $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";

            var w = original.Width;
            var h = original.Height;
            var edge = Math.Max(w, h);
            SKBitmap work = original;
            SKBitmap? scaled = null;
            if (edge > maxEdge && edge > 0)
            {
                var scale = maxEdge / (float)edge;
                var nw = Math.Max(1, (int)Math.Round(w * scale));
                var nh = Math.Max(1, (int)Math.Round(h * scale));
                scaled = original.Resize(new SKImageInfo(nw, nh), SKFilterQuality.Medium);
                if (scaled is not null)
                    work = scaled;
            }

            using (scaled)
            using (var image = SKImage.FromBitmap(work))
            using (var data = image.Encode(SKEncodedImageFormat.Jpeg, jpegQuality))
            {
                if (data is null)
                    return $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";
                var encoded = data.ToArray();
                if (encoded.Length < bytes.Length || edge > maxEdge)
                {
                    _log.LogDebug(
                        "Ref {File}: {SrcKb:0} KB → {DstKb:0} KB (maxEdge={Edge})",
                        Path.GetFileName(path), bytes.Length / 1024.0, encoded.Length / 1024.0, maxEdge);
                }
                return $"data:image/jpeg;base64,{Convert.ToBase64String(encoded)}";
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not re-encode {Path}; sending original bytes", path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var mime = ext switch
            {
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".jpg" or ".jpeg" => "image/jpeg",
                _ => "image/jpeg",
            };
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
    }

    private void EnsureAuthHeader()
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

    private static string Trim(string s, int n) =>
        s.Length <= n ? s : s[..n];
}
