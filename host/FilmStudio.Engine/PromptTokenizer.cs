using Microsoft.ML.Tokenizers;

namespace FilmStudio.Engine;

/// <summary>
/// Shared token-accurate truncation for classifier prompt fragments (previously each classifier
/// truncated by raw character count via its own private <c>Trunc(s, n)</c> — a crude proxy that
/// either over- or under-cuts depending on how token-dense the text is).
///
/// Uses cl100k_base (the GPT-4 / GPT-3.5 encoding) as a stand-in tokenizer. This is an
/// approximation, not a Grok-exact count: providers don't publish their tokenizer vocab, so no
/// local tokenizer — SentencePiece or otherwise — can match one byte-for-byte. It's still a much
/// closer proxy for "how much of the model's context/cost budget this text will use" than
/// character count, and — unlike a live API call — it's free and instant to compute.
/// </summary>
public static class PromptTokenizer
{
    private static readonly Lazy<TiktokenTokenizer> Encoding =
        new(() => TiktokenTokenizer.CreateForModel("gpt-4"));

    /// <summary>Approximate token count for <paramref name="text"/> (cl100k_base).</summary>
    public static int CountTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return Encoding.Value.CountTokens(text);
    }

    /// <summary>
    /// Truncates <paramref name="text"/> to at most <paramref name="maxTokens"/> tokens
    /// (cl100k_base approximation), appending an ellipsis when truncation actually happened.
    /// Mirrors the old character-Trunc's contract: empty/null in, empty string out.
    /// </summary>
    public static string TruncateToTokens(string? text, int maxTokens)
    {
        if (string.IsNullOrEmpty(text) || maxTokens <= 0) return "";
        var tokenizer = Encoding.Value;
        var ids = tokenizer.EncodeToIds(text);
        if (ids.Count <= maxTokens) return text;
        var kept = tokenizer.Decode(ids.Take(maxTokens).ToArray());
        return kept + "…";
    }
}
