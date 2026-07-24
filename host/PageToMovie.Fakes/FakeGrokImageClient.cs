using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Fakes;

/// <summary>Returns a 1×1 PNG for each requested variant.</summary>
public sealed class FakeGrokImageClient : IImageClient
{
    private static readonly byte[] TinyPng =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,
        0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41, 0x54, 0x08, 0xD7, 0x63, 0xF8, 0xCF, 0xC0, 0x00,
        0x00, 0x00, 0x03, 0x00, 0x01, 0x00, 0x05, 0xFE, 0xD4, 0xEF, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45,
        0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    };

    private readonly ILogger<FakeGrokImageClient> _log;

    public FakeGrokImageClient(ILogger<FakeGrokImageClient> log) => _log = log;

    public bool IsConfigured => true;

    public Task<IReadOnlyList<byte[]>> GenerateVariantsAsync(
        string prompt,
        int n = 3,
        string aspectRatio = "1:1",
        string? model = null,
        CancellationToken ct = default)
    {
        n = Math.Clamp(n, 1, 3);
        _log.LogInformation("Fake image gen n={N} promptLen={Len}", n, prompt.Length);
        IReadOnlyList<byte[]> list = Enumerable.Range(0, n).Select(_ => TinyPng.ToArray()).ToList();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<byte[]>> EditVariantsAsync(
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
        onProgress?.Invoke(
            costumeRefPath is null ? "fake edit" : $"fake edit (costume ref: {Path.GetFileName(costumeRefPath)})");
        return GenerateVariantsAsync(prompt, n, aspectRatio, model, ct);
    }
}
