using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Integration tests against the fountain_test_samples fixture pack
/// (Fixtures/Fountain/*.fountain).
/// </summary>
public class FountainSampleFixtureTests
{
    private static string FixtureDir
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fountain");
            if (Directory.Exists(dir)) return dir;
            // Fallback when running from source tree (not copied to output)
            var alt = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "Fountain"));
            return alt;
        }
    }

    private static string ReadSample(string fileName)
    {
        var path = Path.Combine(FixtureDir, fileName);
        Assert.True(File.Exists(path), $"Missing fixture: {path}");
        return File.ReadAllText(path);
    }

    public static IEnumerable<object[]> AllSampleFiles()
    {
        if (!Directory.Exists(FixtureDir))
            yield break;
        foreach (var path in Directory.GetFiles(FixtureDir, "*.fountain").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            yield return new object[] { Path.GetFileName(path) };
    }

    [Theory]
    [MemberData(nameof(AllSampleFiles))]
    public void Every_sample_parses_without_throwing(string fileName)
    {
        var text = ReadSample(fileName);
        var r = FountainParser.Parse(text);
        Assert.NotNull(r);
        Assert.True(
            r.Elements.Count > 0 || r.TitlePage.Count > 0,
            $"{fileName}: expected elements or title page keys");
    }

    [Theory]
    [MemberData(nameof(AllSampleFiles))]
    public void Every_sample_builds_stage1_when_it_has_a_scene(string fileName)
    {
        var text = ReadSample(fileName);
        var r = FountainParser.Parse(text);
        if (!r.Elements.Any(e => e.Type == FountainParser.ElementType.SceneHeading))
            return; // skip samples with no scenes

        var doc = FountainStage1Importer.BuildStage1(r);
        doc = Stage1Normalizer.Normalize(doc);
        Assert.Equal("stage1.v1", doc["schema_version"]?.ToString());
        var scenes = doc["scenes"] as System.Collections.IList;
        Assert.NotNull(scenes);
        Assert.True(scenes!.Count >= 1, $"{fileName}: expected ≥1 stage1 scene");
    }

    [Fact]
    public void Sample_01_basic_scene_elements()
    {
        var r = FountainParser.Parse(ReadSample("01_basic_scene_elements.fountain"));
        Assert.Equal(2, r.Elements.Count(e => e.Type == FountainParser.ElementType.SceneHeading));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.SceneHeading &&
                                         e.Text.Contains("KITCHEN", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.SceneHeading &&
                                         e.Text.Contains("CAR", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "MARA");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "DEREK");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Parenthetical &&
                                         e.Text.Contains("offscreen", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Dialogue &&
                                         e.Text.Contains("late", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sample_02_title_page()
    {
        var r = FountainParser.Parse(ReadSample("02_title_page.fountain"));
        Assert.Equal("The Long Way Home", r.TitlePage["Title"].Trim());
        Assert.Equal("Jordan T. Ellis", r.TitlePage["Author"].Trim());
        Assert.Equal("7/18/2026", r.TitlePage["Draft date"].Trim());
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.PageBreak);
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.SceneHeading &&
                                         e.Text.Contains("MOUNTAIN", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "RENA");
    }

    [Fact]
    public void Sample_03_parentheticals_and_beats()
    {
        var r = FountainParser.Parse(ReadSample("03_parentheticals_and_beats.fountain"));
        var parens = r.Elements.Where(e => e.Type == FountainParser.ElementType.Parenthetical).Select(e => e.Text).ToList();
        Assert.True(parens.Count >= 5, string.Join(" | ", parens));
        Assert.Contains(parens, p => p.Contains("quietly", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(parens, p => p.Contains("beat", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "PRIYA");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "VICTOR");
    }

    [Fact]
    public void Sample_04_dual_dialogue()
    {
        var r = FountainParser.Parse(ReadSample("04_dual_dialogue.fountain"));
        // Sample uses caret on its own line before the second speaker
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "SAM");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "JUNE" &&
                                         e.Meta != null && e.Meta.Contains("dual"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "TOM");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "ADA" &&
                                         e.Meta != null && e.Meta.Contains("dual"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Dialogue &&
                                         e.Text.Contains("absolutely said", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Dialogue &&
                                         e.Text.Contains("splitting the check", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sample_05_transitions()
    {
        var r = FountainParser.Parse(ReadSample("05_transitions.fountain"));
        var transitions = r.Elements.Where(e => e.Type == FountainParser.ElementType.Transition).Select(e => e.Text).ToList();
        Assert.Contains(transitions, t => t.Contains("CUT TO", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(transitions, t => t.Contains("SMASH CUT TO", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(transitions, t => t.Contains("DISSOLVE TO", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Centered &&
                                         e.Text.Contains("THE END", StringComparison.OrdinalIgnoreCase));
        Assert.True(r.Elements.Count(e => e.Type == FountainParser.ElementType.SceneHeading) >= 4);
    }

    [Fact]
    public void Sample_06_centered_text()
    {
        var r = FountainParser.Parse(ReadSample("06_centered_text.fountain"));
        var centered = r.Elements.Where(e => e.Type == FountainParser.ElementType.Centered).Select(e => e.Text).ToList();
        Assert.True(centered.Count >= 3, string.Join(" | ", centered));
        Assert.Contains(centered, c => c.Contains("MOMENT OF SILENCE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(centered, c => c.Contains("LIGHTS DIM", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(centered, c => c.Contains("END OF ACT ONE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sample_07_emphasis_stripped()
    {
        var r = FountainParser.Parse(ReadSample("07_emphasis_bold_italic_underline.fountain"));
        // Emphasis markers should be stripped from plain-text import
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Action &&
                                         e.Text.Contains("CLOSED FOR RENOVATION", StringComparison.OrdinalIgnoreCase) &&
                                         !e.Text.Contains("*CLOSED"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Dialogue &&
                                         e.Text.Contains("important", StringComparison.OrdinalIgnoreCase) &&
                                         !e.Text.Contains("**"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Action &&
                                         e.Text.Contains("Cartographer", StringComparison.OrdinalIgnoreCase) &&
                                         !e.Text.Contains("***"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Dialogue &&
                                         e.Text.Contains("do not", StringComparison.OrdinalIgnoreCase) &&
                                         !e.Text.Contains("_do not_"));
    }

    [Fact]
    public void Sample_08_lyrics()
    {
        var r = FountainParser.Parse(ReadSample("08_lyrics.fountain"));
        var lyrics = r.Elements.Where(e => e.Type == FountainParser.ElementType.Lyric).Select(e => e.Text).ToList();
        Assert.True(lyrics.Count >= 3, string.Join(" | ", lyrics));
        Assert.Contains(lyrics, l => l.Contains("Miles behind us", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lyrics, l => l.Contains("younger then", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lyrics, l => l.StartsWith('~'));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "BEA");
    }

    [Fact]
    public void Sample_09_sections_and_synopses()
    {
        var r = FountainParser.Parse(ReadSample("09_sections_and_synopses.fountain"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Section && e.Text.Contains("Act One") && e.Meta == "1");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Section && e.Text.Contains("Chapter 1") && e.Meta == "2");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Section && e.Text.Contains("Act Two"));
        Assert.True(r.Elements.Count(e => e.Type == FountainParser.ElementType.Synopsis) >= 3);
        Assert.True(r.Elements.Count(e => e.Type == FountainParser.ElementType.SceneHeading) >= 3);
    }

    [Fact]
    public void Sample_10_notes_and_boneyard()
    {
        var r = FountainParser.Parse(ReadSample("10_notes_and_boneyard.fountain"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Note &&
                                         e.Text.Contains("trimming this scene", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Note &&
                                         e.Text.Contains("sound cue", StringComparison.OrdinalIgnoreCase));
        // Boneyard content must not appear as scene/dialogue
        Assert.DoesNotContain(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "ORDERLY");
        Assert.DoesNotContain(r.Elements, e => e.Type == FountainParser.ElementType.Dialogue &&
                                               e.Text.Contains("definition of alright", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "DR. OKAFOR");
    }

    [Fact]
    public void Sample_11_page_breaks()
    {
        var r = FountainParser.Parse(ReadSample("11_page_breaks.fountain"));
        Assert.Equal(2, r.Elements.Count(e => e.Type == FountainParser.ElementType.PageBreak));
        Assert.Equal(3, r.Elements.Count(e => e.Type == FountainParser.ElementType.SceneHeading));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "WREN");
    }

    [Fact]
    public void Sample_12_character_extensions()
    {
        var r = FountainParser.Parse(ReadSample("12_character_extensions.fountain"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "HANA");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "VOICE ON RADIO");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "DIRECTOR MOSS");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "TECH #1");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "TECH #2");
        // Parentheticals after character (V.O., CONT'D as paren, etc.)
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Parenthetical &&
                                         e.Text.Contains("V.O.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sample_13_forced_elements()
    {
        var r = FountainParser.Parse(ReadSample("13_forced_elements.fountain"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.SceneHeading &&
                                         e.Text.Contains("OPENING SHOT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.SceneHeading &&
                                         e.Text.Contains("UNUSUAL LOCATION", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Action &&
                                         e.Text.Contains("forced action text", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Action &&
                                         e.Text.Contains("ALL CAPS LINE THAT LOOKS LIKE A CHARACTER", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character &&
                                         e.Text.Contains("CASHIER", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "23");
    }

    [Fact]
    public void Sample_14_numbered_scene_headings()
    {
        var r = FountainParser.Parse(ReadSample("14_numbered_scene_headings.fountain"));
        var headings = r.Elements.Where(e => e.Type == FountainParser.ElementType.SceneHeading).ToList();
        Assert.Equal(4, headings.Count);
        Assert.Equal("1", headings.First(h => h.Text.Contains("BRIDGE", StringComparison.OrdinalIgnoreCase) &&
                                              !h.Text.Contains("LATER", StringComparison.OrdinalIgnoreCase)).Meta);
        Assert.Equal("2", headings.First(h => h.Text.Contains("ESTABLISHING", StringComparison.OrdinalIgnoreCase)).Meta);
        Assert.Equal("3", headings.First(h => h.Text.Contains("ENGINE", StringComparison.OrdinalIgnoreCase)).Meta);
        Assert.Equal("1A", headings.First(h => h.Text.Contains("LATER", StringComparison.OrdinalIgnoreCase)).Meta);
        Assert.All(headings, h => Assert.DoesNotContain("#", h.Text));
    }

    [Fact]
    public void Sample_15_line_breaks_and_whitespace()
    {
        var r = FountainParser.Parse(ReadSample("15_line_breaks_and_whitespace.fountain"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "OWEN");
        // Multi-line dialogue should be retained as dialogue lines
        var dialogue = r.Elements.Where(e => e.Type == FountainParser.ElementType.Dialogue).Select(e => e.Text).ToList();
        Assert.Contains(dialogue, d => d.Contains("keep thinking", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dialogue, d => d.Contains("what you said", StringComparison.OrdinalIgnoreCase) ||
                                       d.Contains("drive home", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sample_16_unicode_and_special_chars()
    {
        var r = FountainParser.Parse(ReadSample("16_unicode_and_special_chars.fountain"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.SceneHeading &&
                                         e.Text.Contains("PARISIEN", StringComparison.OrdinalIgnoreCase));
        // RENÉE (UTF-8) as character
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character &&
                                         e.Text.Contains("REN", StringComparison.OrdinalIgnoreCase) &&
                                         e.Text.Contains('É'));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "WAITER");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Dialogue &&
                                         e.Text.Contains("incroyable", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Action &&
                                         (e.Text.Contains('€') || e.Text.Contains("12,50")));
    }

    [Fact]
    public void Sample_17_combined_feature_sample()
    {
        var r = FountainParser.Parse(ReadSample("17_combined_feature_sample.fountain"));
        Assert.Equal("Signal Lost", r.TitlePage["Title"].Trim());
        Assert.Equal("A. Fountain Tester", r.TitlePage["Author"].Trim());
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Section && e.Text.Contains("Act One"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Synopsis);
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Note &&
                                         e.Text.Contains("logbook", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Transition &&
                                         e.Text.Contains("CUT TO", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Centered &&
                                         e.Text.Contains("THE END", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "LIV");
        Assert.True(r.Elements.Count(e => e.Type == FountainParser.ElementType.SceneHeading) >= 4);

        var doc = Stage1Normalizer.Normalize(FountainStage1Importer.BuildStage1(r));
        Assert.Contains("Signal", doc["movie_title"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        var scenes = doc["scenes"] as System.Collections.IList;
        Assert.NotNull(scenes);
        Assert.True(scenes!.Count >= 3);
    }

    [Fact]
    public void Sample_18_montage_sequence()
    {
        var r = FountainParser.Parse(ReadSample("18_montage_sequence.fountain"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.SceneHeading &&
                                         e.Text.Contains("MONTAGE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Action &&
                                         e.Text.Contains("pull-up", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "COACH REYES");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "KAI");
    }

    [Fact]
    public void Sample_19_minimal_dialogue_only()
    {
        var r = FountainParser.Parse(ReadSample("19_minimal_dialogue_only.fountain"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.SceneHeading);
        Assert.Equal(6, r.Elements.Count(e => e.Type == FountainParser.ElementType.Character));
        Assert.Equal(6, r.Elements.Count(e => e.Type == FountainParser.ElementType.Dialogue));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "A");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "B");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Dialogue && e.Text == "Hi.");
    }

    [Fact]
    public void Sample_20_edge_cases()
    {
        var r = FountainParser.Parse(ReadSample("20_edge_cases.fountain"));
        var headings = r.Elements.Where(e => e.Type == FountainParser.ElementType.SceneHeading).Select(e => e.Text).ToList();
        Assert.Contains(headings, h => h.Contains("INT./EXT", StringComparison.OrdinalIgnoreCase) ||
                                       h.Contains("CAR", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(headings, h => h.StartsWith("I/E", StringComparison.OrdinalIgnoreCase) ||
                                       h.Contains("SAFEHOUSE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(headings, h => h.StartsWith("EST", StringComparison.OrdinalIgnoreCase) ||
                                       h.Contains("SKYLINE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(headings, h => h.Contains("lowercase kitchen", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(headings, h => h.Contains("42", StringComparison.OrdinalIgnoreCase));

        // R2D2 / C-3PO are valid character names (need ≥1 letter)
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "R2D2");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "C-3PO");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Dialogue &&
                                         e.Text.Contains("Beep", StringComparison.OrdinalIgnoreCase));

        // Mixed-case McSCHNEIDER is not auto Character per Fountain (needs @) — must not throw
        // ALL-CAPS line with blank after is Action, not Character
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Action &&
                                         e.Text.Contains("NOT A CHARACTER CUE", StringComparison.OrdinalIgnoreCase));
    }
}
