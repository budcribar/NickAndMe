using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FilmStudio.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

/// <summary>xAI Grok Imagine image generate + edit client for character portraits.</summary>
public sealed class GrokImageClient
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
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        EnsureAuthHeader();
        var modelName = string.IsNullOrWhiteSpace(model)
            ? _opts.DefaultImageModel
            : model;

        var refs = referenceImagePaths
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Take(3)
            .ToList();
        if (refs.Count == 0)
            throw new InvalidOperationException("No usable reference images for character edit.");

        var imagePayloads = new List<object>();
        foreach (var path in refs)
        {
            var uri = await FileToDataUriAsync(path, ct);
            imagePayloads.Add(new Dictionary<string, string>
            {
                ["url"] = uri,
                ["type"] = "image_url",
            });
        }

        var images = new List<byte[]>();
        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke($"edit variant {i + 1}/{n}");

            // Multi-ref: first image = identity (preferred), later = book style plates
            var orderHint = imagePayloads.Count > 1
                ? "Image 1 is the identity/preferred portrait — match it closely. " +
                  "Images 2+ are book plates of the SAME character for markings and style only. "
                : "Match the attached reference image identity closely. ";
            var variantPrompt =
                orderHint +
                prompt +
                (n > 1
                    ? $" Variation {i + 1} of {n}: tiny pose/expression change only;"
                    : " Single refined portrait;") +
                " same face, coat, hat, and style as the references. No labels, no pasta hats, no redesign.";

            var payload = new Dictionary<string, object?>
            {
                ["model"] = modelName,
                ["prompt"] = variantPrompt,
                ["response_format"] = "b64_json",
                ["aspect_ratio"] = aspectRatio,
                // Keep order: preferred first (caller must sort)
                ["image"] = imagePayloads.Count == 1 ? imagePayloads[0] : imagePayloads,
            };

            using var resp = await _http.PostAsJsonAsync("images/edits", payload, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Grok image edits HTTP {(int)resp.StatusCode} (variant {i + 1}): {Trim(body, 400)}");

            var batch = ParseImageResponse(body, 1, $"edits variant {i + 1}");
            images.AddRange(batch);
        }

        if (images.Count < 1)
            throw new InvalidOperationException("Grok image edit returned no variants.");
        return images.Take(n).ToList();
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
    /// Encode a local image as a data URI without third-party image libraries
    /// (ImageSharp is dual-licensed; we stay dependency-free here).
    /// Oversized book plates may need pre-downscaling offline if the API rejects them.
    /// </summary>
    private async Task<string> FileToDataUriAsync(string path, CancellationToken ct)
    {
        const long warnBytes = 2_500_000; // ~2.5 MB raw
        var info = new FileInfo(path);
        if (info.Length > warnBytes)
        {
            _log.LogWarning(
                "Reference image {Path} is {Kb:0} KB — sending without resize. " +
                "If the API rejects it, downscale the book plate offline first.",
                path, info.Length / 1024.0);
        }

        var bytes = await File.ReadAllBytesAsync(path, ct);
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

    private void EnsureAuthHeader()
    {
        if (_http.DefaultRequestHeaders.Authorization is not null)
            return;
        var key = Environment.GetEnvironmentVariable("XAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return;
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", key.Trim());
    }

    private static string Trim(string s, int n) =>
        s.Length <= n ? s : s[..n];
}
