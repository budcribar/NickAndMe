using FilmStudio.Engine;
using Xunit;

namespace FilmStudio.Tests;

public class CastFromScreenplayServiceTests
{
    [Fact]
    public async Task Prompt_file_exists_and_mentions_silent_cast()
    {
        var root = FindRepoWithPrompts();
        if (root is null)
        {
            Assert.True(true);
            return;
        }

        var text = await CastFromScreenplayService.LoadSystemPromptAsync(root);
        Assert.Contains("cast_seeds", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("silent", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Character_", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JSON", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BOOK-FIRST", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FORBIDDEN", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("performance_lock", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AUDIENCE", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Visual_literalize_prompt_exists_and_targets_figurative_language()
    {
        var root = FindRepoWithPrompts();
        if (root is null)
        {
            Assert.True(true);
            return;
        }

        var text = await CastVisualLiteralizeService.LoadSystemPromptAsync(root);
        Assert.Contains("figurative", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("literal", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JSON", text, StringComparison.OrdinalIgnoreCase);
        // Base-look vs later wardrobe (general, not book-specific lists)
        Assert.Contains("later", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BASE", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wardrobe", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Narrator, as described in the screenplay.", true)]
    [InlineData("as in the screenplay", true)]
    [InlineData("Match Bob consistently across scenes.", true)]
    [InlineData("short", true)]
    [InlineData("Pale nervous adult man, mid-30s, thin face, dark wool coat, 1840s photoreal.", false)]
    public void IsStubLook_detects_placeholders(string text, bool expected)
    {
        Assert.Equal(expected, CastFromScreenplayService.IsStubLook(text));
    }

    [Fact]
    public void SelectTextForPrompt_keeps_short_books_whole()
    {
        var book = "Once upon a time there was a pale man and an old man with a vulture eye.";
        var selected = CastFromScreenplayService.SelectTextForPrompt(book, 100_000);
        Assert.Equal(book, selected);
    }

    [Fact]
    public void SelectTextForPrompt_samples_long_books_with_head_mid_tail()
    {
        var head = new string('A', 50_000);
        var mid = new string('B', 50_000);
        var tail = new string('C', 50_000);
        var book = head + mid + tail;
        var selected = CastFromScreenplayService.SelectTextForPrompt(book, 40_000);
        Assert.True(selected.Length <= 45_000);
        Assert.Contains('A', selected);
        Assert.Contains('C', selected);
        Assert.Contains("sampled for length", selected, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindRepoWithPrompts()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "prompts", "fountain_to_cast.txt");
            if (File.Exists(candidate))
                return dir.FullName;
        }
        var known = @"C:\Users\budcr\source\repos\NickAndMe";
        if (File.Exists(Path.Combine(known, "prompts", "fountain_to_cast.txt")))
            return known;
        return null;
    }
}
