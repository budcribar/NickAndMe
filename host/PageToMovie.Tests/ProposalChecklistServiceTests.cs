using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public class ProposalChecklistServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ProposalChecklistService _svc;

    public ProposalChecklistServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-checklist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects"));
        var store = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
        _svc = new ProposalChecklistService(store, NullLogger<ProposalChecklistService>.Instance);
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
    public void ParseBullets_extracts_dash_and_numbered()
    {
        var bullets = ProposalChecklistService.ParseBullets("""
            - First rule here with enough text
            * Second rule also long enough
            1. Third numbered rule is fine
            not a bullet
            """);
        Assert.Equal(3, bullets.Count);
        Assert.Contains(bullets, b => b.StartsWith("First"));
    }

    [Fact]
    public void Ingest_and_toggle_persists_reviewed()
    {
        var doc = _svc.IngestProposal("""
            - Require full character description so faces never swap.
            - Reject truncated visual prompts with incomplete sentences.
            """, "test");
        Assert.Equal(2, doc.Items.Count);
        Assert.All(doc.Items, i => Assert.False(i.Reviewed));

        var id = doc.Items[0].Id;
        doc = _svc.Toggle(new PageToMovie.Core.Models.ProposalChecklistToggleRequest
        {
            Id = id,
            Reviewed = true,
            Disposition = "accepted",
        });
        var item = Assert.Single(doc.Items, i => i.Id == id);
        Assert.True(item.Reviewed);
        Assert.Equal("accepted", item.Disposition);

        var again = _svc.Load();
        Assert.True(again.Items.First(i => i.Id == id).Reviewed);
    }

    [Fact]
    public void Ingest_preserves_reviewed_when_text_matches()
    {
        _svc.IngestProposal("- Keep character identity locked across clips always.", "a");
        var id = _svc.Load().Items[0].Id;
        _svc.Toggle(new PageToMovie.Core.Models.ProposalChecklistToggleRequest { Id = id, Reviewed = true });

        var doc = _svc.IngestProposal("- Keep character identity locked across clips always.\n- Another brand new rule for the list.", "b");
        Assert.Equal(2, doc.Items.Count);
        Assert.True(doc.Items.First(i => i.Text.Contains("identity")).Reviewed);
        Assert.False(doc.Items.First(i => i.Text.Contains("brand new")).Reviewed);
    }

    [Fact]
    public void Ingest_preserves_reviewed_on_theme_wording_drift()
    {
        _svc.IngestProposal(
            "- Mandate photoreal period-correct human look and fabrics; ban smooth vampiric CGI or stylized skin.",
            "a");
        var id = _svc.Load().Items[0].Id;
        _svc.Toggle(new PageToMovie.Core.Models.ProposalChecklistToggleRequest
        {
            Id = id,
            Reviewed = true,
            Disposition = "accepted",
        });

        // Same theme, different wording (what re-propose often does)
        var doc = _svc.IngestProposal(
            "- Lock every prompt to photoreal live-action period look and ban illustrated, anime, cartoon, painted, or smooth CGI rendering.",
            "b");

        var styleItem = doc.Items.First(i => i.Id == id || i.Text.Contains("photoreal", StringComparison.OrdinalIgnoreCase));
        Assert.True(styleItem.Reviewed);
        Assert.Equal("accepted", styleItem.Disposition);
        // Prior reviewed wording kept (not forced to new bullet text)
        Assert.Contains("vampiric", styleItem.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ingest_retains_reviewed_items_not_in_new_propose()
    {
        _svc.IngestProposal("""
            - Keep character identity locked across all clips carefully.
            - Reject truncated visual prompts with incomplete sentences always.
            """, "a");
        var id = _svc.Load().Items[0].Id;
        _svc.Toggle(new PageToMovie.Core.Models.ProposalChecklistToggleRequest
        {
            Id = id,
            Reviewed = true,
            Disposition = "accepted",
        });

        // New propose only has a different pending theme
        var doc = _svc.IngestProposal(
            "- Auto-review must fail any clip that breaks style lock or cast count rules.",
            "b");

        Assert.Contains(doc.Items, i => i.Id == id && i.Reviewed);
        Assert.Contains(doc.Items, i => i.Text.Contains("Auto-review", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MarkAcceptedFromRuleTexts_sets_disposition()
    {
        _svc.IngestProposal(
            "- State exact CAST COUNT and named identities; forbid extras and background crowd.",
            "a");
        var doc = _svc.MarkAcceptedFromRuleTexts(
            new[] { "Match cast count and named identities; no unlisted people or extras on screen." },
            disposition: "accepted");
        Assert.Contains(doc.Items, i =>
            i.Reviewed && i.Disposition == "accepted" &&
            i.Text.Contains("CAST COUNT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Default_seed_has_seven_session_proposals()
    {
        var doc = ProposalChecklistService.SeedDefaultSessionReview();
        Assert.Equal(7, doc.Items.Count);
    }
}
