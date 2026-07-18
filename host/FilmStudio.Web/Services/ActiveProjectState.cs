namespace FilmStudio.Web.Services;

/// <summary>
/// Circuit-scoped active project for nav gating (hide Adaptation/Scenes/etc. until chosen).
/// </summary>
public sealed class ActiveProjectState
{
    public string? ProjectId { get; private set; }
    public string? Label { get; private set; }

    public bool HasProject => !string.IsNullOrWhiteSpace(ProjectId);

    public event Action? Changed;

    public void Set(string? projectId, string? label = null)
    {
        var id = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();
        var lbl = string.IsNullOrWhiteSpace(label) ? id : label.Trim();
        if (string.Equals(ProjectId, id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Label, lbl, StringComparison.Ordinal))
            return;
        ProjectId = id;
        Label = lbl;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (ProjectId is null && Label is null) return;
        ProjectId = null;
        Label = null;
        Changed?.Invoke();
    }

    /// <summary>Load active project from the API (page load / after create).</summary>
    public async Task RefreshFromApiAsync(EngineApiClient engine, CancellationToken ct = default)
    {
        try
        {
            var projs = await engine.GetProjectsAsync(ct);
            var active = projs?.Active;
            if (active?.Id is { Length: > 0 } aid)
                Set(aid, active.Label ?? active.Title ?? aid);
            else if (projs?.Projects is { Count: > 0 })
            {
                // Prefer explicit active; if none, do not invent — user must pick on Studio
                Clear();
            }
            else
            {
                Clear();
            }
        }
        catch
        {
            // API down — leave prior state (or empty)
        }
    }
}
