namespace FilmStudio.Core.Options;

public sealed class FilmStudioOptions
{
    public const string SectionName = "FilmStudio";

    /// <summary>Repo / workspace root containing projects/ and prompts/.</summary>
    public string WorkspaceRoot { get; set; } = "";

    public string DefaultModel { get; set; } = "grok-imagine-video";
    public string DefaultImageModel { get; set; } = "grok-imagine-image-quality";
    public string DefaultResolution { get; set; } = "480p";
    public int DefaultDurationSeconds { get; set; } = 6;
    public int GrokPollSeconds { get; set; } = 5;
    public int GrokTimeoutSeconds { get; set; } = 900;

    /// <summary>
    /// ffmpeg executable for scene remux / WIP.
    /// Empty → auto: NuGet Soenneker Resources/ffmpeg.exe, then PATH.
    /// Can be an absolute path or path relative to the API output directory.
    /// </summary>
    public string FfmpegPath { get; set; } = "";
}
