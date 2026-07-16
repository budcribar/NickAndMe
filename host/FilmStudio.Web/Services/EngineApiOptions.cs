namespace FilmStudio.Web.Services;

public sealed class EngineApiOptions
{
    public const string SectionName = "EngineApi";

    /// <summary>Base URL of host/python_engine_api.py (default http://127.0.0.1:8765).</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8765";
}
