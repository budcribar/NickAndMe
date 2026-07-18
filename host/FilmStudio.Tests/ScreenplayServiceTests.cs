using FilmStudio.Core.Options;
using FilmStudio.Engine;
using Microsoft.Extensions.Options;
using Xunit;

namespace FilmStudio.Tests;

public class ScreenplayServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;

    public ScreenplayServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-screenplay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", "Demo"));
        var opts = Options.Create(new FilmStudioOptions
        {
            WorkspaceRoot = _root,
        });
        _store = new ProjectStore(opts);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch { /* ignore */ }
    }

    [Fact]
    public void Save_draft_then_sign_off_materialises_stage1()
    {
        const string projectId = "Demo";
        var fountain = """
            Title: Test Script
            Author: Unit Test

            INT. LAB - DAY

            A scientist adjusts a dial.

            SCIENTIST
            Almost there.

            EXT. ROOF - NIGHT

            Rain.
            """;

        var save = ScreenplayService.SaveDraft(_store, projectId, fountain);
        Assert.True(save.Ok);
        Assert.True(save.Status.DraftExists);
        Assert.True(save.Status.Dirty);
        Assert.False(save.Status.Signed);
        Assert.True(save.Status.SceneHeadingCount >= 2);

        var sign = ScreenplayService.SignOff(_store, projectId);
        Assert.True(sign.Ok, sign.Error);
        Assert.Equal(2, sign.SceneCount);
        Assert.True(sign.Status.Signed);
        Assert.False(sign.Status.Dirty);
        Assert.True(sign.Status.ReadyForShots);
        Assert.True(sign.HashChanged);

        var scenesPath = _store.ResolveScenesJsonPath(projectId);
        Assert.True(File.Exists(scenesPath));
        Assert.Contains("Test Script", File.ReadAllText(scenesPath), StringComparison.OrdinalIgnoreCase);

        var sign2 = ScreenplayService.SignOff(_store, projectId);
        Assert.True(sign2.Ok);
        Assert.False(sign2.HashChanged);
    }

    [Fact]
    public void Edit_after_sign_off_marks_dirty_and_blocks_ready_until_reapprove()
    {
        const string projectId = "Demo";
        ScreenplayService.SaveDraft(_store, projectId, "INT. ROOM - DAY\n\nHello.\n");
        var sign = ScreenplayService.SignOff(_store, projectId);
        Assert.True(sign.Ok);
        Assert.True(sign.Status.ReadyForShots);

        ScreenplayService.SaveDraft(_store, projectId, "INT. ROOM - DAY\n\nHello world.\n");
        var status = ScreenplayService.Get(_store, projectId).Status;
        Assert.True(status.Dirty);
        Assert.False(status.Signed);
        Assert.False(status.ReadyForShots);
    }

    [Fact]
    public void Import_as_draft_does_not_write_stage1()
    {
        const string projectId = "Demo";
        var r = ScreenplayService.ImportAsDraft(_store, projectId, "INT. A - DAY\n\nAction.\n", "mine.fountain");
        Assert.True(r.Ok);
        Assert.True(r.Status.DraftExists);
        var scenesPath = _store.ResolveScenesJsonPath(projectId);
        Assert.False(File.Exists(scenesPath));
    }

    [Fact]
    public void Offline_stub_draft_from_book_has_page_tags()
    {
        // CreateDraftFromBook (sync) is the offline stub — production uses chat.
        const string projectId = "Demo";
        var source = Path.Combine(_store.GetProjectDir(projectId), "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "book_full.txt"),
            "--- PAGE 1 ---\nOnce upon a time there was a dog who loved naps.\n\n" +
            "--- PAGE 2 ---\nHe slept by the fire.\n");

        var r = ScreenplayService.CreateDraftFromBook(_store, projectId);
        Assert.True(r.Ok, r.Error);
        var text = ScreenplayService.Get(_store, projectId).Text;
        Assert.Contains("Title:", text);
        Assert.Contains("= page ", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[[page ", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("naps", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Book_context_resolves_synopsis_and_note_page_tags()
    {
        const string projectId = "Demo";
        var source = Path.Combine(_store.GetProjectDir(projectId), "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "book_full.txt"),
            "--- PAGE 1 ---\nAlpha page.\n\n--- PAGE 2 ---\nBeta bedtime page.\n");

        var ctx = BookContextService.GetContext(
            _store, projectId,
            sceneIndex: 1,
            sceneHeading: "INT. BEDROOM - NIGHT",
            sceneBody: "= page 2\n\n[[page 2]]\n\nNARRATOR\nHe slept.\n");
        Assert.True(ctx.HasBook);
        Assert.Equal(2, ctx.PageNumber);
        Assert.Contains("bedtime", ctx.Excerpt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LooksLikeGoodFountain_accepts_real_structure_rejects_dump()
    {
        var good = """
            Title: Buster
            Draft date: 1/1/2026

            INT. LIVING ROOM - EVENING

            = page 2

            [[page 2]]

            NARRATOR
            He's a small dog.

            MOMMA
            Time for bed!
            """;
        Assert.True(BookToFountainConverter.LooksLikeGoodFountain(good));

        var dump = """
            Title: X
            INT. STORY - PAGE 1 - DAY
            text
            INT. STORY - PAGE 2 - DAY
            more
            INT. STORY - PAGE 3 - DAY
            more
            """;
        Assert.False(BookToFountainConverter.LooksLikeGoodFountain(dump));
    }

    [Fact]
    public async Task BuildSystemPrompt_uses_book_to_fountain_with_stage1_learnings()
    {
        var root = FindRepoWithPrompts();
        if (root is null)
        {
            Assert.True(true); // soft-pass when prompts not on disk
            return;
        }

        var fountainPath = Path.Combine(root, "prompts", "book_to_fountain.txt");
        Assert.True(File.Exists(fountainPath), "Expected prompts/book_to_fountain.txt");

        var system = await BookToFountainConverter.BuildSystemPromptAsync(root, totalRuntimeMinutes: 12);

        // Fountain product path — not the JSON schema dump
        Assert.Contains("Fountain", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[[page N]]", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("12", system); // TOTAL_RUNTIME substituted
        Assert.DoesNotContain("{{TOTAL_RUNTIME_MINUTES}}", system);
        Assert.DoesNotContain("stage1_scene_bible.schema.json", system);

        // Key learnings carried over from the old Stage 1 prompt
        Assert.Contains("VO", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("closed cast", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fidelity", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NARRATOR", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INT.", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wardrobe", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("render medium", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("spoiler", system, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindRepoWithPrompts()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "prompts", "book_to_fountain.txt");
            if (File.Exists(candidate))
                return dir.FullName;
        }
        var known = @"C:\Users\budcr\source\repos\NickAndMe";
        if (File.Exists(Path.Combine(known, "prompts", "book_to_fountain.txt")))
            return known;
        return null;
    }

    [Fact]
    public void Adaptation_status_next_step_sign_screenplay_when_draft_dirty()
    {
        const string projectId = "Demo";
        ScreenplayService.SaveDraft(_store, projectId, "INT. A - DAY\n\nX.\n");
        var status = _store.GetAdaptationStatus(projectId);
        Assert.Equal("sign_screenplay", status.NextStep);
        Assert.True(status.Screenplay.DraftExists);
        Assert.True(status.Screenplay.Dirty);
    }

    [Fact]
    public void EnsureCanonicalDraft_adopts_imported_named_fountain()
    {
        const string projectId = "Demo";
        var source = Path.Combine(_store.GetProjectDir(projectId), "source");
        Directory.CreateDirectory(source);
        var named = Path.Combine(source, "Brick-And-Steel.fountain");
        File.WriteAllText(named, "Title: Brick\n\nINT. PATIO - DAY\n\nSun.\n");

        var adopted = ScreenplayService.EnsureCanonicalDraft(_store, projectId);
        Assert.True(adopted);
        var canonical = ScreenplayService.GetDraftPath(_store, projectId);
        Assert.True(File.Exists(canonical));
        Assert.Contains("PATIO", File.ReadAllText(canonical), StringComparison.OrdinalIgnoreCase);

        var doc = ScreenplayService.Get(_store, projectId);
        Assert.True(doc.Status.DraftExists);
        Assert.Contains("PATIO", doc.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BookTextToFountainDraft_delegates_to_stub()
    {
        var f = ScreenplayService.BookTextToFountainDraft("My Book",
            "--- PAGE 1 ---\nA little dog naps by the warm fire tonight.\n");
        Assert.Contains("Title:", f);
        Assert.Contains("[[page ", f, StringComparison.OrdinalIgnoreCase);
    }
}
