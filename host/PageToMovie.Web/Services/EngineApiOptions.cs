namespace PageToMovie.Web.Services;

public sealed class EngineApiOptions
{
    public const string SectionName = "EngineApi";

    /// <summary>Base URL of PageToMovie.Api (REST + SignalR hub).</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:5088";

    /// <summary>
    /// HTTP timeout for Web → API calls. Book → Fountain multi-chunk can run many minutes;
    /// keep this at or above the API chat client timeout (default 20 min).
    /// Env: EngineApi__TimeoutMinutes
    /// </summary>
    public int TimeoutMinutes { get; set; } = 30;
}
