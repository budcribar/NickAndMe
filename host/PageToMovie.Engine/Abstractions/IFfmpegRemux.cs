namespace PageToMovie.Engine.Abstractions;

/// <summary>Scene composite + WIP rebuild (real ffmpeg or test double).</summary>
public interface IFfmpegRemux
{
    string FfmpegPath { get; }
    bool IsAvailable();

    Task<string?> RemuxSceneAsync(
        string projectId,
        int sceneNum,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        bool ignoreAssemblyGate = false);

    Task<string?> RebuildWipAsync(
        string projectId,
        Action<string>? onProgress = null,
        CancellationToken ct = default);

    Task<string?> RebuildPreviewAsync(
        string projectId,
        IReadOnlyList<int> orderedSceneNumbers,
        Action<string>? onProgress = null,
        CancellationToken ct = default);

    bool IsSceneCompositeStale(string projectId, int sceneNum);
}
