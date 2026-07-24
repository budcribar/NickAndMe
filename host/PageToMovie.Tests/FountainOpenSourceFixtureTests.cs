using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Tests against open-source Fountain fixtures from:
/// - nyousefi/Fountain (MIT) — original reference parser samples
/// - wildwinter/screenplay-tools (MIT) — title page + UTF-8 samples
/// See Fixtures/FountainOpenSource/README.md.
/// </summary>
public class FountainOpenSourceFixtureTests
{
    private static string FixtureDir
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "FountainOpenSource");
            if (Directory.Exists(dir)) return dir;
            return Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "FountainOpenSource"));
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
        foreach (var path in Directory.GetFiles(FixtureDir, "*.fountain")
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            yield return new object[] { Path.GetFileName(path) };
    }

    [Theory]
    [MemberData(nameof(AllSampleFiles))]
    public void Every_opensource_sample_parses_without_throwing(string fileName)
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
    public void Every_opensource_sample_with_scenes_builds_stage1(string fileName)
    {
        // Big Fish is a full-length screenplay — parse-only coverage is enough for CI time.
        if (fileName.StartsWith("Big_Fish", StringComparison.OrdinalIgnoreCase))
            return;

        var r = FountainParser.Parse(ReadSample(fileName));
        if (!r.Elements.Any(e => e.Type == FountainParser.ElementType.SceneHeading))
            return;

        var doc = Stage1Normalizer.Normalize(FountainStage1Importer.BuildStage1(r));
        Assert.Equal("stage1.v1", doc["schema_version"]?.ToString());
        var scenes = doc["scenes"] as System.Collections.IList;
        Assert.NotNull(scenes);
        Assert.True(scenes!.Count >= 1, $"{fileName}: expected ≥1 stage1 scene");
    }

    [Fact]
    public void Big_Fish_parses_full_length_screenplay()
    {
        var r = FountainParser.Parse(ReadSample("Big_Fish_nyousefi.fountain"));
        Assert.Equal("Big Fish", r.TitlePage["Title"].Trim(), ignoreCase: true);
        Assert.Contains("John August", r.TitlePage["Author"], StringComparison.OrdinalIgnoreCase);
        // Real production draft — hundreds of scenes and dialogue blocks
        Assert.True(r.Elements.Count(e => e.Type == FountainParser.ElementType.SceneHeading) >= 100,
            "expected a large number of scene headings");
        Assert.True(r.Elements.Count(e => e.Type == FountainParser.ElementType.Character) >= 100);
        Assert.True(r.Elements.Count(e => e.Type == FountainParser.ElementType.Dialogue) >= 100);
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character &&
                                         e.Text.Equals("EDWARD", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character &&
                                         e.Text.Equals("WILL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Brick_And_Steel_reference_sample()
    {
        var r = FountainParser.Parse(ReadSample("Brick_And_Steel_nyousefi.fountain"));
        Assert.Contains("BRICK", r.TitlePage["Title"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Stu Maschwitz", r.TitlePage["Author"].Trim());
        Assert.True(r.Elements.Count(e => e.Type == FountainParser.ElementType.SceneHeading) >= 6);
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "STEEL");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "BRICK" &&
                                         e.Meta != null && e.Meta.Contains("dual"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Transition);
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Centered);
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.SceneHeading &&
                                         e.Text.Contains("SNIPER", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Simple_nyousefi_mixed_features()
    {
        var r = FountainParser.Parse(ReadSample("Simple_nyousefi.fountain"));
        Assert.Equal("A Simple Script", r.TitlePage["Title"].Trim());
        Assert.Equal("Nima Yousefi", r.TitlePage["Author"].Trim());
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.SceneHeading &&
                                         e.Text.Contains("OFFICE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "MAN");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.PageBreak);
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Centered &&
                                         e.Text.Contains("THE END", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Transition &&
                                         e.Text.Contains("Burn to White", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Section);
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Synopsis);
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Note);
        // Boneyard must not surface as action text
        Assert.DoesNotContain(r.Elements, e => e.Text.Contains("This text is in the boneyard", StringComparison.OrdinalIgnoreCase));
        // Trailing spaces after CUT TO: → Action, not Transition
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Action &&
                                         e.Text.Trim().StartsWith("CUT TO:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dialogue_nyousefi()
    {
        var r = FountainParser.Parse(ReadSample("Dialogue_nyousefi.fountain"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "ADAM");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "EVE" &&
                                         e.Meta != null && e.Meta.Contains("O.S.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Parenthetical &&
                                         e.Text.Contains("nervous", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "EVE" &&
                                         e.Meta != null && e.Meta.Contains("dual"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "R2D2");
        // "23" is not a valid character name (no letter)
        Assert.DoesNotContain(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "23");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Dialogue &&
                                         e.Text.Contains("screenwriting", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DualDialogue_nyousefi()
    {
        var r = FountainParser.Parse(ReadSample("DualDialogue_nyousefi.fountain"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "ADAM");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "EVE" &&
                                         e.Meta != null && e.Meta.Contains("dual"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Dialogue && e.Text == "Yes.");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Dialogue && e.Text == "No.");
    }

    [Fact]
    public void Transitions_nyousefi()
    {
        var r = FountainParser.Parse(ReadSample("Transitions_nyousefi.fountain"));
        var transitions = r.Elements.Where(e => e.Type == FountainParser.ElementType.Transition).Select(e => e.Text).ToList();
        Assert.Contains(transitions, t => t.Equals("CUT TO:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(transitions, t => t.Contains("SMASH CUT TO", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(transitions, t => t.Contains("NOT A STANDARD TRANSITION", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(transitions, t => t.Equals("GO TO:", StringComparison.OrdinalIgnoreCase) ||
                                          t.Equals("TO:", StringComparison.OrdinalIgnoreCase));
        // FADE TO BLACK. is a common fade transition (we treat as Transition, not Action)
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Transition &&
                                         e.Text.Contains("FADE TO BLACK", StringComparison.OrdinalIgnoreCase));
        // CUT TO: with trailing spaces → Action (Fountain TO: rule)
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Action &&
                                         e.Text.TrimStart().StartsWith("CUT TO:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CenteredText_nyousefi()
    {
        var r = FountainParser.Parse(ReadSample("CenteredText_nyousefi.fountain"));
        var centered = r.Elements.Where(e => e.Type == FountainParser.ElementType.Centered).Select(e => e.Text).ToList();
        Assert.True(centered.Count >= 3, string.Join(" | ", centered));
        Assert.Contains(centered, c => c.Contains("Centered", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(centered, c => c.Contains("No space", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(centered, c => c.Contains("Lots of Space", StringComparison.OrdinalIgnoreCase));
        // "> Not centered" is forced Transition (no closing <)
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Transition &&
                                         e.Text.Contains("Not centered", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SceneHeaders_nyousefi_all_variants()
    {
        var r = FountainParser.Parse(ReadSample("SceneHeaders_nyousefi.fountain"));
        var headings = r.Elements.Where(e => e.Type == FountainParser.ElementType.SceneHeading)
            .Select(e => e.Text).ToList();

        Assert.Contains(headings, h => h.Equals("INT. HOUSE - DAY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(headings, h => h.Equals("EXT. HOUSE - DAY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(headings, h => h.Equals("INT HOUSE DAY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(headings, h => h.Equals("EXT HOUSE DAY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(headings, h => h.Contains("INT/EXT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(headings, h => h.Contains("INT./EXT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(headings, h => h.StartsWith("I/E", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(headings, h => h.StartsWith("I./E", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(headings, h => h.Contains("EST", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(headings, h => h.Contains("1979"));
        Assert.Contains(headings, h => h.Equals("KITCHEN", StringComparison.OrdinalIgnoreCase)); // forced .
        Assert.Contains(headings, h => h.Equals("int. house - day", StringComparison.OrdinalIgnoreCase));

        // Ellipsis must not force a scene heading
        Assert.DoesNotContain(r.Elements, e => e.Type == FountainParser.ElementType.SceneHeading &&
                                               e.Text.Contains("not a scene header", StringComparison.OrdinalIgnoreCase));
        // ESTABLISHING alone is not INT/EXT → Action
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Action &&
                                         e.Text.Contains("ESTABLISHING", StringComparison.OrdinalIgnoreCase));
        // Stacked headings without blank between must not become Character/Dialogue
        Assert.DoesNotContain(r.Elements, e => e.Type == FountainParser.ElementType.Character &&
                                               e.Text.Contains("HOUSE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SceneNumbers_nyousefi()
    {
        var r = FountainParser.Parse(ReadSample("SceneNumbers_nyousefi.fountain"));
        var headings = r.Elements.Where(e => e.Type == FountainParser.ElementType.SceneHeading).ToList();
        Assert.Equal(7, headings.Count);
        var metas = headings.Select(h => h.Meta).ToList();
        Assert.Contains("1", metas);
        Assert.Contains("1A", metas);
        Assert.Contains("1a", metas);
        Assert.Contains("A1", metas);
        Assert.Contains("I-1-A", metas);
        Assert.Contains("1.", metas);
        Assert.Contains("110A", metas);
        Assert.All(headings, h => Assert.DoesNotContain("#", h.Text));
    }

    [Fact]
    public void ForcedElements_nyousefi()
    {
        var r = FountainParser.Parse(ReadSample("ForcedElements_nyousefi.fountain"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Action &&
                                         e.Text.Contains("BANG", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "McDUCK");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Dialogue &&
                                         e.Text.Contains("vegan", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "SINGER");
        var lyrics = r.Elements.Where(e => e.Type == FountainParser.ElementType.Lyric).Select(e => e.Text).ToList();
        Assert.True(lyrics.Count >= 2);
        Assert.Contains(lyrics, l => l.Contains("songs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Indenting_nyousefi()
    {
        var r = FountainParser.Parse(ReadSample("Indenting_nyousefi.fountain"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Transition &&
                                         e.Text.Contains("CUT TO", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.SceneHeading &&
                                         e.Text.Contains("GARAGE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "BRICK");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "STEEL");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Parenthetical);
    }

    [Fact]
    public void Boneyard_and_Notes_nyousefi()
    {
        var b = FountainParser.Parse(ReadSample("Boneyard_nyousefi.fountain"));
        Assert.DoesNotContain(b.Elements, e => e.Text.Contains("inline Boneyard", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(b.Elements, e => e.Text.Contains("multi-line", StringComparison.OrdinalIgnoreCase) &&
                                               e.Text.Contains("Boneyard", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(b.Elements, e => e.Type == FountainParser.ElementType.Action &&
                                         e.Text.Contains("line of action", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(b.Elements, e => e.Type == FountainParser.ElementType.Action &&
                                         e.Text.Contains("boneyard", StringComparison.OrdinalIgnoreCase));

        var n = FountainParser.Parse(ReadSample("Notes_nyousefi.fountain"));
        Assert.True(n.Elements.Count(e => e.Type == FountainParser.ElementType.Note) >= 2);
        Assert.Contains(n.Elements, e => e.Type == FountainParser.ElementType.Note &&
                                         e.Text.Contains("A note", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(n.Elements, e => e.Type == FountainParser.ElementType.Note &&
                                         e.Text.Contains("multiple lines", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(n.Elements, e => e.Type == FountainParser.ElementType.Note &&
                                         e.Text.Contains("internal", StringComparison.OrdinalIgnoreCase));
        // Notes removed from surrounding action
        Assert.Contains(n.Elements, e => e.Type == FountainParser.ElementType.Action &&
                                         e.Text.Contains("This is an", StringComparison.OrdinalIgnoreCase) &&
                                         !e.Text.Contains("[["));
    }

    [Fact]
    public void PageBreaks_nyousefi()
    {
        var r = FountainParser.Parse(ReadSample("PageBreaks_nyousefi.fountain"));
        Assert.Equal(2, r.Elements.Count(e => e.Type == FountainParser.ElementType.PageBreak));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Action && e.Text.Contains("Page one"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Action && e.Text.Contains("Page two"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Action && e.Text.Contains("Page three"));
    }

    [Fact]
    public void Sections_and_Synopses_nyousefi()
    {
        var sections = FountainParser.Parse(ReadSample("SectionHeaders_nyousefi.fountain"));
        Assert.True(sections.Elements.Count(e => e.Type == FountainParser.ElementType.Section) >= 6);
        Assert.Contains(sections.Elements, e => e.Type == FountainParser.ElementType.Section &&
                                                e.Text.Contains("Act One", StringComparison.OrdinalIgnoreCase) &&
                                                e.Meta == "1");
        Assert.Contains(sections.Elements, e => e.Type == FountainParser.ElementType.Section && e.Meta == "2");
        Assert.Contains(sections.Elements, e => e.Type == FountainParser.ElementType.Section && e.Meta == "3");

        var syn = FountainParser.Parse(ReadSample("Synopses_nyousefi.fountain"));
        Assert.Equal(3, syn.Elements.Count(e => e.Type == FountainParser.ElementType.Section));
        Assert.Equal(3, syn.Elements.Count(e => e.Type == FountainParser.ElementType.Synopsis));
        Assert.Contains(syn.Elements, e => e.Type == FountainParser.ElementType.Synopsis &&
                                           e.Text.Contains("first act", StringComparison.OrdinalIgnoreCase));

        var complex = FountainParser.Parse(ReadSample("SectionsComplex_nyousefi.fountain"));
        Assert.Contains(complex.Elements, e => e.Type == FountainParser.ElementType.Section && e.Text.Contains("Act 1"));
        Assert.Contains(complex.Elements, e => e.Type == FountainParser.ElementType.Synopsis);
        Assert.Equal(2, complex.Elements.Count(e => e.Type == FountainParser.ElementType.SceneHeading));
    }

    [Fact]
    public void MultilineAction_nyousefi()
    {
        var r = FountainParser.Parse(ReadSample("MultilineAction_nyousefi.fountain"));
        Assert.All(r.Elements, e => Assert.Equal(FountainParser.ElementType.Action, e.Type));
        Assert.Contains(r.Elements, e => e.Text.Contains("WALTER HILL", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(r.Elements, e => e.Type == FountainParser.ElementType.Character);
    }

    [Fact]
    public void TitlePage_screenplaytools()
    {
        var r = FountainParser.Parse(ReadSample("TitlePage_screenplaytools.fountain"));
        Assert.Contains("BRICK", r.TitlePage["Title"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Stu Maschwitz", r.TitlePage["Author"].Trim());
        Assert.Equal("1/20/2012", r.TitlePage["Draft date"].Trim());
        Assert.True(r.TitlePage.ContainsKey("Contact") || r.TitlePage.ContainsKey("Credit"));
        Assert.Contains("Written by", r.TitlePage["Credit"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UTF8_screenplaytools()
    {
        var r = FountainParser.Parse(ReadSample("UTF8_screenplaytools.fountain"));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.SceneHeading &&
                                         e.Text.Contains("SCEHE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Character && e.Text == "DAVE");
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Dialogue &&
                                         e.Text.Contains("Nice day", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Transition &&
                                         e.Text.Contains("CUT TO", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.SceneHeading &&
                                         e.Text.Contains("OLYMPIA", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.Action &&
                                         e.Text.Contains("carnival", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.SceneHeading &&
                                         e.Text.Contains("FORCED HEADER", StringComparison.OrdinalIgnoreCase));
        // ". NOT A HEADER" — period not followed by alphanumeric
        Assert.DoesNotContain(r.Elements, e => e.Type == FountainParser.ElementType.SceneHeading &&
                                               e.Text.TrimStart().StartsWith("NOT A HEADER", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Elements, e => e.Type == FountainParser.ElementType.SceneHeading &&
                                         e.Meta == "3");
    }
}
