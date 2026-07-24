namespace PageToMovie.Engine;

/// <summary>
/// Loads the book → Fountain prompt pack (<c>prompts/book_to_fountain.txt</c>).
/// Fountain is the operator screenplay; there is no book→JSON scene-bible prompt.
/// </summary>
public static class Stage1PromptPack
{
    /// <summary>Primary prompt for book → Fountain.</summary>
    public const string BookToFountainRelativePath = "prompts/book_to_fountain.txt";

    /// <summary>
    /// System prompt for book → Fountain. Loads <c>prompts/book_to_fountain.txt</c>
    /// with <c>{{TOTAL_RUNTIME_MINUTES}}</c> substituted.
    /// </summary>
    public static async Task<string> LoadBookToFountainSystemPromptAsync(
        string workspaceRoot,
        int totalRuntimeMinutes,
        string? fallbackBody = null,
        CancellationToken ct = default)
    {
        totalRuntimeMinutes = Math.Clamp(totalRuntimeMinutes, 3, 180);
        var path = Path.Combine(
            workspaceRoot,
            BookToFountainRelativePath.Replace('/', Path.DirectorySeparatorChar));

        string body;
        if (File.Exists(path))
        {
            body = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(fallbackBody))
        {
            body = fallbackBody;
        }
        else
        {
            throw new InvalidOperationException(
                $"Book→Fountain prompt not found: {path}. Expected prompts/book_to_fountain.txt at workspace root.");
        }

        return body.Replace("{{TOTAL_RUNTIME_MINUTES}}", totalRuntimeMinutes.ToString());
    }
}
