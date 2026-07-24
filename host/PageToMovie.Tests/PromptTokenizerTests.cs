using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public sealed class PromptTokenizerTests
{
    [Fact]
    public void CountTokens_EmptyOrNull_ReturnsZero()
    {
        Assert.Equal(0, PromptTokenizer.CountTokens(null));
        Assert.Equal(0, PromptTokenizer.CountTokens(""));
    }

    [Fact]
    public void CountTokens_ShortSentence_ReturnsPlausibleCount()
    {
        // "Hello, world!" is well-known to be a handful of tokens under cl100k_base — not
        // asserting the exact number (that's the tokenizer library's job to get right), just
        // that we're getting real token-ish granularity back, not characters (13) or words (2).
        var n = PromptTokenizer.CountTokens("Hello, world!");
        Assert.InRange(n, 1, 6);
    }

    [Fact]
    public void CountTokens_LongerTextHasMoreTokensThanShort()
    {
        var shortCount = PromptTokenizer.CountTokens("The old man's eye was pale blue.");
        var longCount = PromptTokenizer.CountTokens(
            "The old man's eye was pale blue, filmed over like a vulture's, and it haunted the " +
            "narrator day and night until the thought of it consumed every waking hour.");
        Assert.True(longCount > shortCount);
    }

    [Fact]
    public void TruncateToTokens_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Equal("", PromptTokenizer.TruncateToTokens(null, 10));
        Assert.Equal("", PromptTokenizer.TruncateToTokens("", 10));
    }

    [Fact]
    public void TruncateToTokens_UnderBudget_ReturnsUnchanged()
    {
        var text = "A short beat.";
        Assert.Equal(text, PromptTokenizer.TruncateToTokens(text, 100));
    }

    [Fact]
    public void TruncateToTokens_OverBudget_TruncatesAndMarksIt()
    {
        var text = string.Join(" ", Enumerable.Repeat("vulture eye pale blue film", 50));
        var truncated = PromptTokenizer.TruncateToTokens(text, 10);

        Assert.True(truncated.Length < text.Length);
        Assert.EndsWith("…", truncated);
        // The truncated text (minus the ellipsis marker) must fit the budget when re-tokenized —
        // that's the actual guarantee this function exists to make.
        var reTokenized = PromptTokenizer.CountTokens(truncated[..^1]);
        Assert.True(reTokenized <= 10);
    }
}
