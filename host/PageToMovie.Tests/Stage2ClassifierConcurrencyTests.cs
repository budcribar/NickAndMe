using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Proves the 9 per-scene classifiers in Stage2PlannerService.PlanAsync (pacing, lighting,
/// camera, negative-prompt, wardrobe, emotion, sound, depth-of-field, color-grading) actually
/// fan out via Task.WhenAll instead of running one network round-trip at a time.
/// </summary>
public sealed class Stage2ClassifierConcurrencyTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fs-s2-concurrency-" + Guid.NewGuid().ToString("N"));

    public Stage2ClassifierConcurrencyTests() =>
        Directory.CreateDirectory(Path.Combine(_root, "projects", "Demo"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Tracks how many CompleteAsync calls were ever in flight at the same time.</summary>
    private sealed class ConcurrencyProbeChatClient : IChatClient
    {
        private readonly object _gate = new();
        private int _active;
        public int MaxConcurrent { get; private set; }
        public bool IsConfigured => true;

        public async Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            string model = "grok-4.5",
            double temperature = 0.2,
            CancellationToken ct = default,
            string? mode = null)
        {
            lock (_gate)
            {
                _active++;
                if (_active > MaxConcurrent) MaxConcurrent = _active;
            }
            try
            {
                // Wide enough that overlapping calls are reliably observed, short enough the
                // test stays fast even with 9 sequential-worst-case classifiers.
                await Task.Delay(60, ct).ConfigureAwait(false);
                return "{}"; // parses to nothing useful for every classifier — fine, this test
                             // only cares that the call happened concurrently, not its result.
            }
            finally
            {
                lock (_gate) { _active--; }
            }
        }
    }

    [Fact]
    public async Task PlanAsync_RunsPerSceneClassifiers_Concurrently()
    {
        var store = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
        const string projectId = "Demo";
        var fountain = """
            Title: Concurrency Check

            INT. ROOM - DAY

            The room is quiet. Dust hangs in a shaft of light from the window.

            HERO
            Hello there. It has been a very long time since I stood in this room.

            HERO crosses to the window and looks out at the street below.
            """;
        ScreenplayService.SaveDraft(store, projectId, fountain);
        var sign = ScreenplayService.SignOff(store, projectId);
        Assert.True(sign.Ok, sign.Error);

        var probe = new ConcurrencyProbeChatClient();
        var opts = Options.Create(new PageToMovieOptions());

        var planner = new Stage2PlannerService(
            store,
            NullLogger<Stage2PlannerService>.Instance,
            beatPacingClassifier: new BeatPacingClassifier(probe, opts, NullLogger<BeatPacingClassifier>.Instance),
            lightingClassifier: new CinematicLightingClassifier(probe, opts, NullLogger<CinematicLightingClassifier>.Instance),
            cameraClassifier: new CameraDirectorClassifier(probe, opts, NullLogger<CameraDirectorClassifier>.Instance),
            negativeClassifier: new NegativePromptClassifier(probe, opts, NullLogger<NegativePromptClassifier>.Instance),
            wardrobeClassifier: new WardrobeContinuityClassifier(probe, opts, NullLogger<WardrobeContinuityClassifier>.Instance),
            emotionClassifier: new CharacterEmotionArcClassifier(probe, opts, NullLogger<CharacterEmotionArcClassifier>.Instance),
            soundComposerClassifier: new SoundDesignComposerClassifier(probe, opts, NullLogger<SoundDesignComposerClassifier>.Instance),
            dofClassifier: new DepthOfFieldClassifier(probe, opts, NullLogger<DepthOfFieldClassifier>.Instance),
            colorGradingClassifier: new ColorPaletteGradingClassifier(probe, opts, NullLogger<ColorPaletteGradingClassifier>.Instance));

        var result = await planner.PlanAsync(projectId, resolution: "480p", scenes: "all");

        Assert.True(result.Ok, "expected PlanAsync to succeed");
        Assert.True(result.SceneCount > 0, "expected at least one planned scene");
        Assert.True(
            probe.MaxConcurrent > 1,
            $"expected overlapping classifier calls (Task.WhenAll fan-out), but max observed concurrency was {probe.MaxConcurrent}");
    }
}
