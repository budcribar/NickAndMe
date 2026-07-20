using FilmStudio.Engine;
using FilmStudio.Engine.Abstractions;
using Xunit;

namespace FilmStudio.Tests;

/// <summary>
/// Budget, quality gate, and SingleShotFirst → ChunkFallback path selection.
/// </summary>
public class BookToFountainPathTests
{
    [Fact]
    public void ResolvePromptBudget_grok_allows_large_single_shot()
    {
        var b = BookToFountainConverter.ResolvePromptBudget("grok-4.5");
        Assert.Equal("grok-4.5", b.ModelId);
        Assert.True(
            b.SingleShotBookMaxChars > BookToFountainConverter.SingleShotMaxChars,
            $"expected single-shot max >> legacy 28k, got {b.SingleShotBookMaxChars}");
        Assert.Equal(BookToFountainConverter.DefaultSingleShotBookMaxChars, b.SingleShotBookMaxChars);
        Assert.InRange(b.ChunkSoftMaxChars, 4_000, b.SingleShotBookMaxChars);
        Assert.Equal(BookToFountainConverter.MaxAdaptChunks, b.MaxChunks);
    }

    [Fact]
    public void ResolvePromptBudget_unknown_model_still_usable()
    {
        var b = BookToFountainConverter.ResolvePromptBudget("some-future-chat");
        Assert.True(b.SingleShotBookMaxChars >= BookToFountainConverter.SingleShotMaxChars);
        Assert.True(b.ChunkSoftMaxChars >= 4_000);
    }

    [Fact]
    public void FitsSingleShot_respects_budget()
    {
        var budget = new BookToFountainConverter.PromptBudget
        {
            ModelId = "test",
            SingleShotBookMaxChars = 10_000,
            ChunkSoftMaxChars = 5_000,
            MaxChunks = 4,
            ReservedOverheadChars = 1_000,
        };
        Assert.True(BookToFountainConverter.FitsSingleShot(new string('a', 9_000), budget));
        Assert.False(BookToFountainConverter.FitsSingleShot(new string('a', 10_001), budget));
    }

    [Fact]
    public void ShouldChunkFallback_false_for_tiny_book()
    {
        var budget = BookToFountainConverter.ResolvePromptBudget("grok-4.5");
        var tiny = "--- PAGE 1 ---\nA short picture-book line about a dog in the sun.\n";
        Assert.False(BookToFountainConverter.ShouldChunkFallback(tiny, budget));
    }

    [Fact]
    public void ShouldChunkFallback_true_for_long_chaptered_book()
    {
        var budget = BookToFountainConverter.ResolvePromptBudget("grok-4.5");
        var book = BuildChapteredBook(chapters: 12, bodyChars: 3_000);
        Assert.True(book.Length >= BookToFountainConverter.MinBookCharsForChunkFallback);
        Assert.True(BookToFountainConverter.ShouldChunkFallback(book, budget));
    }

    [Fact]
    public void EvaluateQuality_short_good_fountain_passes_single()
    {
        var book = "--- PAGE 1 ---\nA little dog naps by the warm fire tonight under soft blankets.\n";
        var fountain = GoodFountain(scenes: 3, withEnding: true);
        var gate = BookToFountainConverter.EvaluateQuality(
            fountain, book, totalRuntimeMinutes: 8, BookToFountainConverter.AdaptPath.Single);
        Assert.True(gate.Ok, gate.Reason);
        Assert.Equal("ok", gate.Reason);
    }

    [Fact]
    public void EvaluateQuality_structure_fail_is_hard()
    {
        var book = new string('x', 30_000);
        var gate = BookToFountainConverter.EvaluateQuality(
            "not fountain at all", book, 20, BookToFountainConverter.AdaptPath.Single);
        Assert.False(gate.Ok);
        Assert.True(gate.HasHardFailure);
        Assert.Contains("structure", gate.Failures);
    }

    [Fact]
    public void EvaluateQuality_long_book_short_draft_fails_single_soft()
    {
        var book = BuildChapteredBook(chapters: 20, bodyChars: 4_000);
        Assert.True(book.Length > 60_000);
        // Structurally valid but too thin for a long novel single-shot
        var thin = """
            Title: Thin
            Author: T

            INT. ROOM - DAY

            NARRATOR
            Once upon a time there was a very short summary of a long book that should not pass coverage.

            FADE OUT.

            THE END
            """;
        var gate = BookToFountainConverter.EvaluateQuality(
            thin, book, totalRuntimeMinutes: 40, BookToFountainConverter.AdaptPath.Single);
        Assert.False(gate.Ok, "expected soft coverage fail");
        Assert.False(gate.HasHardFailure);
        Assert.Contains(gate.Failures, f => f.StartsWith("scene_count") || f == "suspiciously_short");
    }

    [Fact]
    public void EvaluateQuality_multi_accepts_soft_scene_shortfall_if_structure_ok()
    {
        var book = BuildChapteredBook(chapters: 20, bodyChars: 4_000);
        var thin = """
            Title: Thin
            Author: T

            INT. ROOM - DAY

            NARRATOR
            Once upon a time there was a stitched partial that is still valid fountain structure for multi path.

            HERO
            Hello there friend.

            FADE OUT.

            THE END
            """;
        var gate = BookToFountainConverter.EvaluateQuality(
            thin, book, totalRuntimeMinutes: 40, BookToFountainConverter.AdaptPath.Multi);
        Assert.True(gate.Ok, gate.Reason);
        Assert.False(gate.HasHardFailure);
    }

    [Fact]
    public async Task Convert_short_book_uses_single_shot_only()
    {
        var chat = new RecordingChatClient(_ => GoodFountain(scenes: 4, withEnding: true));
        var book = "--- PAGE 1 ---\nA little dog naps by the warm fire tonight under soft blankets and dreams.\n"
                   + "--- PAGE 2 ---\nMorning comes and the dog stretches in the golden light of the kitchen.\n";
        var root = Path.GetTempPath();

        var text = await BookToFountainConverter.ConvertAsync(
            workspaceRoot: root,
            title: "Short",
            bookText: book,
            author: "A",
            totalRuntimeMinutes: 6,
            chat: chat,
            model: "grok-4.5");

        Assert.True(BookToFountainConverter.LooksLikeGoodFountain(text));
        Assert.InRange(chat.Calls, 1, 2); // 1 pass, or structure retry only
        Assert.DoesNotContain(chat.UserPrompts, u => u.Contains("multi-chunk", StringComparison.OrdinalIgnoreCase)
            || u.Contains("BOOK_CHUNK 2/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Convert_medium_book_single_first_when_quality_ok()
    {
        var chat = new RecordingChatClient(_ => GoodFountain(scenes: 12, withEnding: true, padBody: 400));
        // ~40k–50k: under default 120k single-shot budget, over legacy 28k
        var book = BuildChapteredBook(chapters: 14, bodyChars: 3_200);
        Assert.InRange(book.Length, BookToFountainConverter.SingleShotMaxChars + 1, BookToFountainConverter.DefaultSingleShotBookMaxChars);

        var text = await BookToFountainConverter.ConvertAsync(
            workspaceRoot: Path.GetTempPath(),
            title: "Medium",
            bookText: book,
            totalRuntimeMinutes: 20,
            chat: chat,
            model: "grok-4.5");

        Assert.True(BookToFountainConverter.LooksLikeGoodFountain(text));
        // Single-shot success: no multi-chunk (would be ≥3 calls for 2+ chunks + possible merge)
        Assert.True(chat.Calls <= 2, $"expected single-shot (≤2 calls), got {chat.Calls}");
        Assert.Contains(chat.UserPrompts, u => u.Contains("BOOK_CHUNK 1/1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Convert_quality_soft_fail_triggers_chunk_fallback()
    {
        var chat = new RecordingChatClient(n =>
        {
            // First single-shot attempts (pass + coverage retry): thin draft (structure ok, coverage fail)
            if (n <= 2)
            {
                return """
                    Title: Thin
                    Author: T

                    INT. ROOM - DAY

                    NARRATOR
                    A brief opening only — not enough arc for the full novel length below.

                    HERO
                    We start.
                    """;
            }

            // Multi-chunk parts + merge: return per-chunk scenes
            return GoodFountain(scenes: 4, withEnding: true, padBody: 120);
        });

        var book = BuildChapteredBook(chapters: 18, bodyChars: 4_000);
        Assert.True(book.Length > 60_000);
        Assert.True(BookToFountainConverter.ShouldChunkFallback(
            book, BookToFountainConverter.ResolvePromptBudget("grok-4.5")));

        var progress = new List<string>();
        var text = await BookToFountainConverter.ConvertAsync(
            workspaceRoot: Path.GetTempPath(),
            title: "Long",
            bookText: book,
            totalRuntimeMinutes: 45,
            chat: chat,
            model: "grok-4.5",
            onProgress: s => progress.Add(s));

        Assert.True(BookToFountainConverter.LooksLikeGoodFountain(text));
        Assert.True(chat.Calls >= 3, $"expected chunk fallback (≥3 calls), got {chat.Calls}");
        Assert.Contains(progress, p => p.Contains("multi-chunk", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Falling back", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Convert_over_budget_skips_single_goes_multi()
    {
        var chat = new RecordingChatClient(_ => GoodFountain(scenes: 3, withEnding: true, padBody: 80));
        var book = BuildChapteredBook(chapters: 10, bodyChars: 2_500);
        var tinyBudget = new BookToFountainConverter.PromptBudget
        {
            ModelId = "tiny-test",
            SingleShotBookMaxChars = 5_000,
            ChunkSoftMaxChars = 6_000,
            MaxChunks = 4,
            ReservedOverheadChars = 1_000,
        };
        Assert.False(BookToFountainConverter.FitsSingleShot(book, tinyBudget));

        var progress = new List<string>();
        var text = await BookToFountainConverter.ConvertAsync(
            workspaceRoot: Path.GetTempPath(),
            title: "Over",
            bookText: book,
            totalRuntimeMinutes: 15,
            chat: chat,
            model: "grok-4.5",
            onProgress: s => progress.Add(s),
            budgetOverride: tinyBudget);

        Assert.True(BookToFountainConverter.LooksLikeGoodFountain(text));
        Assert.Contains(progress, p => p.Contains("exceeds model budget", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(progress, p => p.Contains("single pass", StringComparison.OrdinalIgnoreCase));
        // Multi path should mention chunks
        Assert.True(chat.Calls >= 2, $"expected multi-chunk calls, got {chat.Calls}");
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static string BuildChapteredBook(int chapters, int bodyChars)
    {
        var sb = new System.Text.StringBuilder();
        for (var c = 1; c <= chapters; c++)
        {
            sb.Append("CHAPTER ").Append(c).Append('\n');
            sb.Append(new string((char)('a' + (c % 26)), bodyChars));
            sb.Append(" chapter body ").Append(c).Append("\n\n");
        }
        return sb.ToString();
    }

    private static string GoodFountain(int scenes, bool withEnding, int padBody = 60)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Title: Test");
        sb.AppendLine("Author: Unit");
        sb.AppendLine();
        for (var i = 1; i <= scenes; i++)
        {
            sb.AppendLine(i % 2 == 0 ? $"EXT. PLACE {i} - DAY" : $"INT. ROOM {i} - NIGHT");
            sb.AppendLine();
            sb.AppendLine("NARRATOR");
            sb.AppendLine(new string('w', Math.Max(40, padBody)) + $" scene {i} action and description.");
            sb.AppendLine();
            sb.AppendLine("HERO");
            sb.AppendLine($"Line number {i} with enough dialogue text for the gate.");
            sb.AppendLine();
        }

        if (withEnding)
        {
            sb.AppendLine("FADE OUT.");
            sb.AppendLine();
            sb.AppendLine("THE END");
        }

        return sb.ToString();
    }

    private sealed class RecordingChatClient : IGrokChatClient
    {
        private readonly Func<int, string> _responseForCall;

        public RecordingChatClient(Func<int, string> responseForCall) =>
            _responseForCall = responseForCall;

        public int Calls { get; private set; }
        public List<string> UserPrompts { get; } = new();
        public bool IsConfigured => true;

        public Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            string model = "grok-4.5",
            double temperature = 0.2,
            CancellationToken ct = default,
            string? mode = null)
        {
            Calls++;
            UserPrompts.Add(userPrompt ?? "");
            return Task.FromResult(_responseForCall(Calls));
        }
    }
}
