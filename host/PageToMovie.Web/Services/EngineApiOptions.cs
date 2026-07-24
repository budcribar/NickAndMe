namespace PageToMovie.Web.Services;

public sealed class EngineApiOptions
{
    public const string SectionName = "EngineApi";

    /// <summary>
    /// Base URL of PageToMovie.Api for server-side HttpClient (REST + SignalR).
    /// On the unified Docker/Railway host this is loopback; the browser must not use it for media.
    /// </summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:5088";

    /// <summary>
    /// Origin the <b>browser</b> should use for &lt;img&gt;/&lt;video&gt; media.
    /// <list type="bullet">
    /// <item>Empty (default): root-relative <c>/api/...</c> — correct for unified host (Railway, Docker Api+Blazor).</item>
    /// <item>Set when Blazor Web runs on another port than the API, e.g. <c>http://127.0.0.1:5088</c>.</item>
    /// </list>
    /// Env: EngineApi__BrowserMediaBaseUrl
    /// </summary>
    public string BrowserMediaBaseUrl { get; set; } = "";

    /// <summary>
    /// HTTP timeout for Web → API calls. Book → Fountain multi-chunk can run many minutes;
    /// keep this at or above the API chat client timeout (default 20 min).
    /// Env: EngineApi__TimeoutMinutes
    /// </summary>
    public int TimeoutMinutes { get; set; } = 30;
}
