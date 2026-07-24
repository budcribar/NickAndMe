using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Regression suite for the book_to_fountain package
/// (Fixtures/BookToFountainPackage) — public-domain adaptations exercising
/// real screenplay shapes: FADE IN, CONT'D, V.O., multi-word cast, epistolary VO.
/// </summary>
public class BookToFountainPackageTests
{
    private static string PackageDir
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "BookToFountainPackage");
            if (Directory.Exists(dir)) return dir;
            return Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "BookToFountainPackage"));
        }
    }

    private static string FountainDir => Path.Combine(PackageDir, "fountain_adaptations");

    public static IEnumerable<object[]> AllPackageFountains()
    {
        if (!Directory.Exists(FountainDir))
            yield break;
        foreach (var path in Directory.GetFiles(FountainDir, "*.fountain")
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            yield return new object[] { Path.GetFileName(path) };
    }

    [Fact]
    public void Package_has_ten_fountain_adaptations()
    {
        Assert.True(Directory.Exists(FountainDir), $"Missing package dir: {FountainDir}");
        var n = Directory.GetFiles(FountainDir, "*.fountain").Length;
        Assert.Equal(10, n);
    }

    [Theory]
    [MemberData(nameof(AllPackageFountains))]
    public void Package_fountain_parses_with_scene_headings_and_no_unspecified_phantom(string fileName)
    {
        var path = Path.Combine(FountainDir, fileName);
        Assert.True(File.Exists(path), path);
        var text = File.ReadAllText(path);

        var parsed = FountainParser.Parse(text);
        var heads = parsed.Elements.Count(e => e.Type == FountainParser.ElementType.SceneHeading);
        var chars = parsed.Elements.Count(e => e.Type == FountainParser.ElementType.Character);
        var dlg = parsed.Elements.Count(e => e.Type == FountainParser.ElementType.Dialogue);

        Assert.True(heads >= 1, $"{fileName}: expected scene headings");
        Assert.True(chars >= 1, $"{fileName}: expected character cues");
        Assert.True(dlg >= 1, $"{fileName}: expected dialogue");
        Assert.True(BookToFountainConverter.LooksLikeGoodFountain(text),
            $"{fileName}: failed LooksLikeGoodFountain gate");

        // FADE IN (if present) must be Transition — never Action that invents INT. UNSPECIFIED
        foreach (var e in parsed.Elements.Where(e =>
                     e.Text.Contains("FADE IN", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Equal(FountainParser.ElementType.Transition, e.Type);
        }

        var model = ScreenplayService.BuildModelFromFountainText(text);
        Assert.True(model.TryGetValue("scenes", out var scenesObj));
        var scenes = Assert.IsAssignableFrom<System.Collections.IList>(scenesObj);
        Assert.True(scenes.Count >= 1, $"{fileName}: model has no scenes");
        Assert.Equal(heads, scenes.Count); // importer should not invent extra UNSPECIFIED

        foreach (var item in scenes)
        {
            var dict = Assert.IsType<Dictionary<string, object?>>(item);
            var setting = dict.TryGetValue("setting", out var st) ? st?.ToString() ?? "" : "";
            Assert.DoesNotContain("UNSPECIFIED", setting, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Package_christmas_carol_keeps_iconic_dialogue_tokens()
    {
        var path = Path.Combine(FountainDir, "02_A_Christmas_Carol.fountain");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        // Package is slightly paraphrased, but iconic stems must survive for film recognition
        Assert.Contains("Humbug", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prisons", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("surplus population", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("God bless us", text, StringComparison.OrdinalIgnoreCase);

        var parsed = FountainParser.Parse(text);
        Assert.Contains(parsed.Elements, e =>
            e.Type == FountainParser.ElementType.Character &&
            e.Text.Contains("SCROOGE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(parsed.Elements, e =>
            e.Type == FountainParser.ElementType.Character &&
            e.Text.Contains("MARLEY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(parsed.Elements, e =>
            e.Type == FountainParser.ElementType.Character &&
            e.Text.Contains("GHOST OF CHRISTMAS PAST", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Package_raven_preserves_nevermore_refrain()
    {
        var path = Path.Combine(FountainDir, "09_The_Raven.fountain");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.True(
            System.Text.RegularExpressions.Regex.Matches(text, "Nevermore",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count >= 3,
            "Raven adaptation should keep the Nevermore refrain");
        // Package notes paraphrase of verse — product prompt should still prefer book lines
        Assert.Contains("paraphrased", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Package_contd_and_vo_character_extensions_parse()
    {
        var src = """
            Title: Ext Test

            INT. ROOM - DAY

            SCROOGE
            One.

            SCROOGE (CONT'D)
            Two.

            NARRATOR (V.O.)
            Voice.

            NARRATOR (V.O.) (CONT'D)
            More voice.

            MAN (CONT'D)
            (quietly)
            Whisper.
            """;
        var r = FountainParser.Parse(src);
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character &&
                                         e.Text == "SCROOGE" && e.Meta != null &&
                                         e.Meta.Contains("CONT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character &&
                                         e.Text == "NARRATOR" && e.Meta != null &&
                                         e.Meta.Contains("V.O", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Parenthetical &&
                                         e.Text.Contains("quietly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Curly_apostrophe_contd_normalizes_for_character_cues()
    {
        // Typographic apostrophe U+2019
        var src = "Title: T\n\nINT. ROOM - DAY\n\nSCROOGE (CONT\u2019D)\nBah!\n";
        var r = FountainParser.Parse(src);
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character &&
                                         e.Text == "SCROOGE");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Dialogue &&
                                         e.Text.Contains("Bah", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Book_to_fountain_prompt_file_emphasizes_dialogue_fidelity()
    {
        // Walk up from test bin to repo prompts/
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? promptPath = null;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "prompts", "book_to_fountain.txt");
            if (File.Exists(candidate)) { promptPath = candidate; break; }
            dir = dir.Parent;
        }
        // Also try known workspace layout
        if (promptPath is null)
        {
            var alt = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", "prompts", "book_to_fountain.txt"));
            if (File.Exists(alt)) promptPath = alt;
        }

        Assert.True(promptPath is not null && File.Exists(promptPath),
            "Could not locate prompts/book_to_fountain.txt from test host");
        var body = File.ReadAllText(promptPath!);
        Assert.Contains("DIALOGUE FIDELITY", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nevermore", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("book wording", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do NOT emit JSON", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Fountain 1.1", body, StringComparison.OrdinalIgnoreCase);
    }
}
