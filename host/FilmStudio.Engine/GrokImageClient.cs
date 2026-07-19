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
public sealed class GrokImageClient : IGrokImageClient
{
    public const string ApiBase = "https://api.x.ai/v1";

    private readonly HttpClient _http;
    private readonly FilmStudioOptions _opts;
    private readonly ILogger<GrokImageClient> _log;

    public GrokImageClient(
        HttpClient http,
        IOptions<FilmStudioOptions> opts,
        ILogger<GrokImageClient> log)
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

        using var resp = await _http.PostAsJsonAsync("images/generations", payload, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Grok image generations HTTP {(int)resp.StatusCode}: {Trim(body, 400)}");

        var images = ParseImageResponse(body, n, "generations");
        if (images.Count < n)
            throw new InvalidOperationException(
                $"Grok image API returned {images.Count}/{n} usable images");
        return images;
    }

    /// <summary>
    /// Reference-guided edits (book plates). One API call per variant for reliability.
    /// </summary>
    public async Task<IReadOnlyList<byte[]>> EditVariantsAsync(
        string prompt,
        IReadOnlyList<string> referenceImagePaths,
        int n = 3,
        string aspectRatio = "1:1",
        string? model = null,
        int maxRefs = 0,
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

        var refs = referenceImagePaths
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Take(cap)
            .ToList();
        if (refs.Count == 0)
            throw new InvalidOperationException("No usable reference images for character edit.");

        // Downscale book plates — three full-page PNGs as data URIs (~7MB+) often fail;
        // two can succeed by luck under the request size limit.
        var imageUris = new List<string>(refs.Count);
        foreach (var path in refs)
            imageUris.Add(await FileToDataUriAsync(path, ct, maxEdge: 1024, jpegQuality: 85)
                .ConfigureAwait(false));

        var images = new List<byte[]>();
        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke($"edit variant {i + 1}/{n}");

            var orderHint = imageUris.Count > 1
                ? BuildMultiImageOrderHint(imageUris.Count)
                : "Match the attached reference identity AND illustration style (highest priority over text). ";
            var variantTail = n > 1
                ? $" Variation {i + 1} of {n}: tiny pose/expression change only; " +
                  "same identity, markings, and illustrated medium as the book references. "
                : " Single refined continuity portrait in the book’s illustration style. ";
            var variantPrompt =
                orderHint +
                prompt +
                variantTail +
                "Keep children's picture-book illustration style from the refs — not photoreal photography. " +
                "If refs show no clothing, do not invent costumes. " +
                "No labels, no redesign, no model sheet.";

            var body = await PostImageEditAsync(
                modelName, variantPrompt, aspectRatio, imageUris, onProgress, ct)
                .ConfigureAwait(false);
            if (body is null)
                throw new InvalidOperationException(
                    $"Image edit failed (variant {i + 1}): empty response");

            var batch = ParseImageResponse(body, 1, $"edits variant {i + 1}");
            images.AddRange(batch);
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
