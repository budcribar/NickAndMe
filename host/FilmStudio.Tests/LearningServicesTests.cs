using FilmStudio.Core.Models;
using FilmStudio.Core.Options;
using FilmStudio.Engine;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FilmStudio.Tests;

public class LearningServicesTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _projects;
    private readonly ReviewEventStore _events;
    private readonly PromptPackService _packs;
    private readonly ProjectRulesService _rules;
    private readonly LearningProposalService _propose;

    public LearningServicesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs_learn2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", "Demo"));
        File.WriteAllText(Path.Combine(_root, "projects", "Demo", "project.json"),
            """{"id":"Demo","label":"Demo"}""");
        var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = _root, EnableReadCaches = false });
        _projects = new ProjectStore(opts);
        _events = new ReviewEventStore(_projects, NullLogger<ReviewEventStore>.Instance);
        _packs = new PromptPackService(_projects, NullLogger<PromptPackService>.Instance);
        _rules = new ProjectRulesService(_projects, _events, NullLogger<ProjectRulesService>.Instance);
        _propose = new LearningProposalService(
            _events,
            new OfflineChat(),
            NullLogger<LearningProposalService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch { /* temp */ }
    }

    [Fact]
    public void P2_prompt_packs_default_and_activate()
    {
        var m = _packs.EnsureDefaults();
        Assert.Contains(m.Packs, p => p.Id == "gen-v1");
        Assert.Contains(m.Packs, p => p.Id == "auto_review-v1");
        Assert.False(string.IsNullOrWhiteSpace(_packs.LoadActivePackText(PromptPackService.KindGen)));

        var created = _packs.CreateVersion("gen", "v2", "- Never invent dialogue.", "test");
        Assert.Equal("gen-v2", created.Id);
        _packs.Activate("gen-v2");
        var text = _packs.LoadActivePackText(PromptPackService.KindGen);
        Assert.Contains("Never invent dialogue", text);
    }

    [Fact]
    public async Task P3_propose_from_fails_offline()
    {
        for (var i = 0; i < 5; i++)
        {
            _events.Append(new ReviewLearningEvent
            {
                ProjectId = "Demo",
                Type = "clip_fail",
                Category = "continuity",
                Note = "Jump cut at join",
                Scene = 1,
                Clip = i + 1,
            });
        }

        var r = await _propose.ProposeAsync(new ProposeLearningRulesRequest { LastNFails = 10, ProjectId = "Demo" });
        Assert.True(r.Ok);
        Assert.True(r.FailEventsUsed >= 5);
        Assert.False(string.IsNullOrWhiteSpace(r.Proposal));
        Assert.Contains("continuity", r.Categories);
    }

    [Fact]
    public void P4_project_rules_suggest_approve()
    {
        for (var i = 0; i < 4; i++)
        {
            _events.Append(new ReviewLearningEvent
            {
                ProjectId = "Demo",
                Type = "clip_fail",
                Category = "wrong_voice",
                Note = "Female voice on dad",
                Scene = 1,
                Clip = i + 1,
            });
        }

        var doc = _rules.SuggestFromFails("Demo", minFails: 3);
        Assert.NotEmpty(doc.Pending);
        var sug = doc.Pending.First(p => p.Category == "wrong_voice");
        doc = _rules.Approve("Demo", sug.Id, textOverride: null, approvedBy: "admin");
        Assert.DoesNotContain(doc.Pending, p => p.Id == sug.Id);
        Assert.Contains(doc.Active, a => a.Category == "wrong_voice");
        var block = _rules.GetActiveRulesBlock("Demo");
        Assert.Contains("voice", block, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class OfflineChat : IGrokChatClient
    {
        public bool IsConfigured => false;
        public Task<string> CompleteAsync(
            string systemPrompt, string userPrompt, string model = "grok-4.5",
            double temperature = 0.2, CancellationToken ct = default) =>
            Task.FromResult("- offline rule");
    }
}
