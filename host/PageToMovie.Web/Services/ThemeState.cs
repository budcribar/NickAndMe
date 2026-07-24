namespace PageToMovie.Web.Services;

/// <summary>
/// Circuit-scoped UI theme preference ("dark" | "light" | "system"), sourced from the
/// active project's config (<c>ui_theme</c> in pipeline_config.json). Components that
/// render theme-dependent chrome (e.g. NavMenu) apply it to the DOM via JS interop and
/// notify subscribers so the preference stays in sync across the app.
/// </summary>
public sealed class ThemeState
{
    public string Preference { get; private set; } = "dark";

    public event Action? Changed;

    public void Set(string? preference)
    {
        var v = Normalize(preference);
        if (string.Equals(Preference, v, StringComparison.Ordinal)) return;
        Preference = v;
        Changed?.Invoke();
    }

    public static string Normalize(string? v) =>
        v is "light" or "system" ? v : "dark";
}
