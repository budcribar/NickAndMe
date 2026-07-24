using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public class ReviewEventStoreTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _projects;
    private readonly ReviewEventStore _store;

    public ReviewEventStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs_learning_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects"));
        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = _root, EnableReadCaches = false });
        _projects = new ProjectStore(opts);
        _store = new ReviewEventStore(_projects, NullLogger<ReviewEventStore>.Instance);
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
    public void Append_and_query_roundtrip()
    {
        _store.Append(new ReviewLearningEvent
        {
            ProjectId = "Buster",
            Type = "clip_fail",
            Scene = 1,
            Clip = 2,
            Note = "Wrong voice",
            Category = "wrong_voice",
        });
        _store.Append(new ReviewLearningEvent
        {
            ProjectId = "Buster",
            Type = "auto_review",
            Scene = 1,
            Clip = 2,
            Suggestion = "fail",
            Category = "continuity",
            Note = "Jump cut",
        });

        Assert.True(File.Exists(_store.EventsPath));
        var all = _store.Query(projectId: "Buster", take: 10);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, e => e.Type == "clip_fail" && e.Category == "wrong_voice");

        var insights = _store.BuildInsights("Buster");
        Assert.Equal(2, insights.EventCount);
        Assert.Equal(1, insights.HumanFail);
        Assert.Equal(1, insights.AutoReview);
        Assert.True(insights.FailByCategory.ContainsKey("wrong_voice") ||
                    insights.ByCategory.ContainsKey("wrong_voice"));
    }

    [Fact]
    public void Insights_counts_apply_and_regen()
    {
        _store.Append(new ReviewLearningEvent { ProjectId = "P", Type = "auto_review_apply", SuggestionCount = 2 });
        _store.Append(new ReviewLearningEvent { ProjectId = "P", Type = "regen_after_review", Scene = 1, Clip = 1, Outcome = "done" });
        var i = _store.BuildInsights("P");
        Assert.Equal(1, i.ApplyCount);
        Assert.Equal(1, i.RegenCount);
    }
}
