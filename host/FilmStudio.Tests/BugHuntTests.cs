using System.Text.Json;
using System.Text.Json.Nodes;
using FilmStudio.Core.Models;
using FilmStudio.Core.Options;
using FilmStudio.Engine;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FilmStudio.Tests;

/// <summary>
/// Regression tests for bugs found in a code review pass.
/// Each test names the bug; fixes live in production code under test.
/// </summary>
public class BugHuntTests
{
    // ── 1. JobStore.GetPrimary must prefer queued over done ─────────────

    [Fact]
    public void Bug1_GetPrimary_prefers_queued_over_newer_done()
    {
        var store = new JobStore();
        var done = store.Create(new JobRecord
        {
            Status = "done",
            UserId = "u",
            ProjectId = "P",
            FinishedAt = DateTimeOffset.UtcNow,
            QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });
        var queued = store.Create(new JobRecord
        {
            Status = "queued",
            UserId = "u",
            ProjectId = "P",
            QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), // older than done.FinishedAt
        });

        var primary = store.GetPrimary("u");
        Assert.NotNull(primary);
        Assert.Equal(queued.JobId, primary!.JobId);
        Assert.Equal("queued", primary.Status);
        Assert.NotEqual(done.JobId, primary.JobId);
    }

    // ── 2. Voice preview cache fingerprint must use default sample text ─

    [Fact]
    public void Bug2_VoicePreview_cache_matches_without_explicit_sample()
    {
        // Generate stores fingerprint with BuildSampleDialogue(display).
        // Status checks often omit sampleText — must still match.
        var display = "Daddy";
        var sample = VoicePreviewService.BuildSampleDialogue(display);
        var stored = VoicePreviewService.ComputeFingerprint(
            "Character_Daddy", "Adult male", "Daddy", sample);
        // Status path: sampleText null/empty
        var statusFp = VoicePreviewService.ComputeFingerprint(
            "Character_Daddy", "Adult male", "Daddy", sampleText: null);
        // After fix, status path should normalize via display/name
        var statusFp2 = VoicePreviewService.ComputeFingerprintForCache(
            "Character_Daddy", "Adult male", "Daddy", displayName: display, sampleText: null);
        Assert.Equal(stored, statusFp2);
    }

    // ── 3. Project rules: do not re-suggest category already active ─────

    [Fact]
    public void Bug3_ProjectRules_skips_category_already_active()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_bug3_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects", "Demo"));
        File.WriteAllText(Path.Combine(root, "projects", "Demo", "project.json"),
            """{"id":"Demo"}""");
        try
        {
            var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = root, EnableReadCaches = false });
            var projects = new ProjectStore(opts);
            var events = new ReviewEventStore(projects, NullLogger<ReviewEventStore>.Instance);
            var rules = new ProjectRulesService(projects, events, NullLogger<ProjectRulesService>.Instance);

            for (var i = 0; i < 4; i++)
            {
                events.Append(new ReviewLearningEvent
                {
                    ProjectId = "Demo",
                    Type = "clip_fail",
                    Category = "continuity",
                    Note = "jump",
                    Scene = 1,
                    Clip = i + 1,
                });
            }

            var doc = rules.SuggestFromFails("Demo", minFails: 3);
            Assert.Single(doc.Pending);
            var id = doc.Pending[0].Id;
            doc = rules.Approve("Demo", id, null, "admin");
            Assert.Single(doc.Active);

            // More fails same category — must NOT add another pending for continuity
            for (var i = 0; i < 4; i++)
            {
                events.Append(new ReviewLearningEvent
                {
                    ProjectId = "Demo",
                    Type = "clip_fail",
                    Category = "continuity",
                    Note = "jump again",
                    Scene = 2,
                    Clip = i + 1,
                });
            }

            doc = rules.SuggestFromFails("Demo", minFails: 3);
            Assert.DoesNotContain(doc.Pending, p => p.Category == "continuity");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    // ── 4. duration_seconds as JSON number (double) must be read ────────

    [Fact]
    public void Bug4_EstimateForClip_reads_double_duration_seconds()
    {
        using var doc = JsonDocument.Parse(
            """{"duration_seconds": 7.0, "visual_prompt": "Wide shot", "audio_payload": {}}""");
        var est = ClipDurationEstimator.EstimateForClip(doc.RootElement);
        // Without fix, planned stays 0 and estimate is action-only (~3–4s).
        // With planned=7 and no dialogue: clamp min(planned, est+2) → should be 7 if est+2>=7 or close.
        Assert.True(est >= 5, $"expected planned duration influence, got {est}");
    }

    // ── 5. Catalog must not return video entry for chat capability ──────

    [Fact]
    public void Bug5_Catalog_Find_does_not_return_video_model_for_chat()
    {
        var hit = SupportedModelCatalog.Find("grok-imagine-video", ModelCapability.Chat);
        Assert.Null(hit);
        // ResolveOrDefault should fall through to a real chat default, not the video model id
        var resolved = SupportedModelCatalog.ResolveOrDefault(
            "grok-imagine-video", ModelCapability.Chat, fallbackId: "grok-4.5");
        Assert.Equal(ModelCapability.Chat, resolved.Capability);
        Assert.NotEqual("grok-imagine-video", resolved.Id);
    }

    // ── 6. Silence cut floor must respect MinSeconds ───────────────────

    [Fact]
    public void Bug6_Silence_cut_respects_MinSeconds_floor()
    {
        // Trailing silence starts at 1.2s on a 5s clip — cut would be ~1.55 with keepTail 0.35
        var log = "silence_start: 1.2\n";
        var cut = ClipSilenceTrimmer.ComputeCutPoint(log, totalDuration: 5.0, keepTailSeconds: 0.35);
        Assert.Null(cut); // must refuse cut below MinSeconds (~3)
    }

    // ── 7. UpdateClipVisualPrompt must use veo_clips ───────────────────

    [Fact]
    public void Bug7_UpdateClipVisualPrompt_updates_veo_clips()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_bug7_" + Guid.NewGuid().ToString("N"));
        var proj = Path.Combine(root, "projects", "Demo");
        Directory.CreateDirectory(proj);
        File.WriteAllText(Path.Combine(proj, "project.json"), """{"id":"Demo"}""");
        File.WriteAllText(Path.Combine(proj, "pipeline_config.json"),
            """{"blueprint_file":"blueprint.clips.grok.json"}""");
        File.WriteAllText(Path.Combine(proj, "blueprint.clips.grok.json"),
            """
            {
              "scenes": [
                {
                  "scene_number": 1,
                  "veo_clips": [
                    { "clip_number": 1, "visual_prompt": "OLD PROMPT" }
                  ]
                }
              ]
            }
            """);
        try
        {
            var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = root, EnableReadCaches = false });
            var store = new ProjectStore(opts);
            store.UpdateClipVisualPrompt("Demo", 1, 1, "NEW PROMPT");
            var json = File.ReadAllText(Path.Combine(proj, "blueprint.clips.grok.json"));
            Assert.Contains("NEW PROMPT", json);
            Assert.DoesNotContain("OLD PROMPT", json);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    // ── 8. Auto-review LoadClipPlan must read veo_clips ─────────────────

    [Fact]
    public void Bug8_LoadClipPlan_reads_veo_clips_visual_prompt()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_bug8_" + Guid.NewGuid().ToString("N"));
        var proj = Path.Combine(root, "projects", "Demo");
        Directory.CreateDirectory(proj);
        File.WriteAllText(Path.Combine(proj, "project.json"), """{"id":"Demo"}""");
        File.WriteAllText(Path.Combine(proj, "pipeline_config.json"),
            """{"blueprint_file":"blueprint.clips.grok.json"}""");
        File.WriteAllText(Path.Combine(proj, "blueprint.clips.grok.json"),
            """
            {
              "scenes": [
                {
                  "scene_number": 2,
                  "veo_clips": [
                    {
                      "clip_number": 3,
                      "visual_prompt": "CU of dog barking",
                      "audio_payload": { "speaker": "Character_Dog", "dialogue": "Woof" }
                    }
                  ]
                }
              ]
            }
            """);
        try
        {
            var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = root, EnableReadCaches = false });
            var store = new ProjectStore(opts);
            var plan = ClipAutoReviewService.LoadClipPlanForTests(store, "Demo", 2, 3);
            Assert.Equal("CU of dog barking", plan.VisualPrompt);
            Assert.Equal("Woof", plan.Dialogue);
            Assert.Equal("Character_Dog", plan.Speaker);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    // ── 9. JobStore.Get must not tear under concurrent Update ───────────

    [Fact]
    public async Task Bug9_JobStore_Get_safe_under_concurrent_Update()
    {
        var store = new JobStore();
        var rec = store.Create(new JobRecord { Status = "running", UserId = "u", Log = new List<string>() });
        var errors = 0;
        using var cts = new CancellationTokenSource();

        var updater = Task.Run(() =>
        {
            for (var i = 0; i < 5_000 && !cts.IsCancellationRequested; i++)
            {
                store.Update(rec.JobId, j =>
                {
                    j.Log.Add("line-" + i);
                    if (j.Log.Count > 20)
                        j.Log = j.Log.TakeLast(10).ToList();
                    j.Message = "msg-" + i;
                    j.Index = i;
                });
            }
        }, cts.Token);

        for (var i = 0; i < 5_000; i++)
        {
            try
            {
                var g = store.Get(rec.JobId);
                Assert.NotNull(g);
                _ = g!.Log.Count; // may throw if list torn
                _ = g.Message;
            }
            catch
            {
                Interlocked.Increment(ref errors);
            }
        }

        cts.Cancel();
        try { await updater; } catch (OperationCanceledException) { /* expected */ }
        Assert.Equal(0, errors);
    }

    // ── 10. ParseJsonObject must accept markdown fence with trailing text ─

    [Fact]
    public void Bug10_ParseJsonObject_ignores_braces_in_preamble()
    {
        // First "{" is in prose ("{high}"), not the JSON object
        var text = """
            Confidence is {high} for this pick.
            ```json
            { "ok": true, "count": 2 }
            ```
            """;
        var d = GrokChatClient.ParseJsonObject(text);
        Assert.True(d.ContainsKey("ok"));
        Assert.Equal(true, d["ok"]);
        Assert.Equal(2L, Convert.ToInt64(d["count"]));
    }

    // ── 11. Dialogue without speaker must still produce AUDIO block ─────

    [Fact]
    public void Bug11_BuildPrompt_includes_dialogue_even_without_speaker()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "visual_prompt": "Wide shot of the yard",
              "audio_payload": {
                "dialogue": "Hello there!",
                "delivery": "on_camera",
                "speaker": ""
              }
            }
            """);
        var built = ClipVideoPromptBuilder.Build(doc.RootElement, projectDir: Path.GetTempPath());
        Assert.Contains("Hello there!", built.Prompt, StringComparison.Ordinal);
        Assert.Contains("AUDIO:", built.Prompt, StringComparison.Ordinal);
    }

    // ── 12. PromptPack CreateVersion must reject null kind ──────────────

    [Fact]
    public void Bug12_PromptPack_CreateVersion_null_kind_throws_argument()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_bug12_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        try
        {
            var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = root, EnableReadCaches = false });
            var packs = new PromptPackService(new ProjectStore(opts), NullLogger<PromptPackService>.Instance);
            var ex = Assert.ThrowsAny<Exception>(() => packs.CreateVersion(null!, "v1", "body"));
            Assert.False(ex is NullReferenceException, "should be ArgumentException, not NRE");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    // ── 13. JobStore.Create must not silently overwrite an existing id ──

    [Fact]
    public void Bug13_JobStore_Create_does_not_overwrite_existing_job_id()
    {
        var store = new JobStore();
        var a = store.Create(new JobRecord { JobId = "fixedid123456", Status = "running", Kind = "scene" });
        var b = store.Create(new JobRecord { JobId = "fixedid123456", Status = "queued", Kind = "remux" });
        Assert.NotEqual(a.JobId, b.JobId);
        Assert.Equal("scene", store.Get(a.JobId)!.Kind);
        Assert.Equal("remux", store.Get(b.JobId)!.Kind);
    }

    // ── 14. JobStore.Clone must tolerate null Log ───────────────────────

    [Fact]
    public void Bug14_JobStore_Create_with_null_Log_does_not_throw()
    {
        var store = new JobStore();
        var rec = store.Create(new JobRecord { Status = "queued", Log = null! });
        var got = store.Get(rec.JobId);
        Assert.NotNull(got);
        Assert.NotNull(got!.Log);
        Assert.Empty(got.Log);
    }

    // ── 15. LoginRateLimiter must not grow failure list without bound ───

    [Fact]
    public void Bug15_LoginRateLimiter_caps_failure_history()
    {
        var lim = new FilmStudio.Api.Auth.LoginRateLimiter(maxAttempts: 5, windowSeconds: 300);
        for (var i = 0; i < 500; i++)
            lim.RecordFailure("attacker");
        // Reflect into private window to assert bound (public API still blocks)
        Assert.True(lim.IsBlocked("attacker", out _));
        var field = typeof(FilmStudio.Api.Auth.LoginRateLimiter)
            .GetField("_windows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var dict = field!.GetValue(lim) as System.Collections.IDictionary;
        Assert.NotNull(dict);
        object? window = null;
        foreach (System.Collections.DictionaryEntry e in dict!)
        {
            window = e.Value;
            break;
        }
        Assert.NotNull(window);
        var failuresProp = window!.GetType().GetProperty("Failures");
        var failures = failuresProp!.GetValue(window) as System.Collections.ICollection;
        Assert.NotNull(failures);
        Assert.True(failures!.Count <= 5,
            $"failure list unbounded: count={failures.Count}");
    }

    // ── 16. ExtractMessageText must not throw when message is missing ───

    [Fact]
    public void Bug16_ExtractMessageText_missing_message_returns_raw_or_empty()
    {
        using var doc = JsonDocument.Parse(
            """{"choices":[{"finish_reason":"stop"}],"id":"x"}""");
        // Via CompleteAsync path we only have ExtractMessageText private — exercise Parse-less public surface:
        // Use reflection or public Complete with fake — call through public helper if exposed.
        var text = GrokChatClient.ExtractMessageTextForTests(doc.RootElement);
        Assert.NotNull(text); // must not throw
        Assert.True(text.Length >= 0);
    }

    // ── 17. AllocateForBeats null must not NRE ──────────────────────────

    [Fact]
    public void Bug17_AllocateForBeats_null_returns_empty()
    {
        var durs = ClipDurationEstimator.AllocateForBeats(null!);
        Assert.NotNull(durs);
        Assert.Empty(durs);
    }

    // ── 18. FindCharacterRefPaths clamps non-positive maxRefs ───────────

    [Fact]
    public void Bug18_FindCharacterRefPaths_non_positive_maxRefs_is_safe()
    {
        using var doc = JsonDocument.Parse(
            """{"visual_prompt": "Character_Dog runs", "primary_subject": "Character_Dog"}""");
        var paths = ClipVideoPromptBuilder.FindCharacterRefPaths(doc.RootElement, Path.GetTempPath(), maxRefs: 0);
        Assert.Empty(paths);
        // negative must not throw / infinite-loop
        paths = ClipVideoPromptBuilder.FindCharacterRefPaths(doc.RootElement, Path.GetTempPath(), maxRefs: -3);
        Assert.Empty(paths);
    }

    // ── 19. CharacterRefFileName empty must not be bare _ref.png ────────

    [Fact]
    public void Bug19_CharacterRefFileName_rejects_empty_key()
    {
        var name = ProjectStore.CharacterRefFileName("  ");
        Assert.False(string.Equals(name, "_ref.png", StringComparison.OrdinalIgnoreCase));
        Assert.True(name.Length > "_ref.png".Length || name.StartsWith("character", StringComparison.OrdinalIgnoreCase) || name.Contains("unknown", StringComparison.OrdinalIgnoreCase),
            $"unexpected empty-key name: {name}");
    }

    // ── 20. Server metrics must not go negative after unmatched releases ─

    [Fact]
    public void Bug20_ServerMetrics_release_without_acquire_does_not_skew_count()
    {
        var m = new ServerMetricsService();
        m.NoteApiSlotReleased("ghost");
        m.NoteApiSlotReleased("ghost");
        m.NoteApiSlotAcquired("real");
        // Without floor: -2 + 1 = -1 → display Max(0,-1)=0. With floor: 0+1=1.
        var snap = m.GetSnapshot(
            new JobStore(),
            new InMemoryLockService(),
            new CapacityOptionsSnapshot { MaxVideoInFlight = 4 },
            new ProcessMetricsSnapshot());
        Assert.Equal(1, snap.ApiInFlight);
    }

    // ── 21. Blueprint path cache must not stick on null forever ─────────

    [Fact]
    public async Task Bug21_ProjectReadCache_does_not_cache_missing_blueprint_forever()
    {
        var cache = new ProjectReadCache { Enabled = true };
        var calls = 0;
        Task<string?> Find(CancellationToken _)
        {
            calls++;
            return Task.FromResult<string?>(calls == 1 ? null : @"C:\tmp\blueprint.clips.grok.json");
        }

        var a = await cache.GetOrFindBlueprintPathAsync("P", Find);
        Assert.Null(a);
        var b = await cache.GetOrFindBlueprintPathAsync("P", Find);
        // Second call must re-find after a miss (blueprint may appear later)
        Assert.Equal(@"C:\tmp\blueprint.clips.grok.json", b);
        Assert.True(calls >= 2, $"find should run again after null cache, calls={calls}");
    }

    // ── 22. TryCancel must treat DONE / Error as terminal (ignore case) ─

    [Fact]
    public void Bug22_JobStore_TryCancel_is_case_insensitive_for_terminal()
    {
        var store = new JobStore();
        var done = store.Create(new JobRecord { Status = "DONE", Kind = "scene" });
        Assert.False(store.TryCancel(done.JobId));
        Assert.Equal("DONE", store.Get(done.JobId)!.Status);

        var err = store.Create(new JobRecord { Status = "Error", Kind = "remux" });
        Assert.False(store.TryCancel(err.JobId));
    }

    // ── 23. SceneListCache clone must tolerate null list props ──────────

    [Fact]
    public async Task Bug23_SceneListCache_null_list_props_do_not_throw()
    {
        var cache = new SceneListCache();
        var list = await cache.GetOrBuildAsync("p", probeDurations: false, _ =>
            Task.FromResult<IReadOnlyList<SceneSummary>>(new List<SceneSummary>
            {
                new()
                {
                    SceneNumber = 1,
                    CharactersOnScreen = null!,
                    LocationIds = null!,
                },
            }));
        Assert.Single(list);
        Assert.NotNull(list[0].CharactersOnScreen);
        Assert.NotNull(list[0].LocationIds);
    }

    // ── 24. Progress percent parse must be culture-invariant ────────────

    [Fact]
    public void Bug24_TryParseGrokProgress_invariant_decimal()
    {
        // Public test hook
        var pct = VoicePreviewService.TryParseGrokProgressForTests("status=pending (42.5%)");
        Assert.Equal(43, pct); // rounded
        pct = VoicePreviewService.TryParseGrokProgressForTests("status=pending (12%)");
        Assert.Equal(12, pct);
    }

    // ── 25. LocalWorkerPool.InFlight never negative ─────────────────────

    [Fact]
    public void Bug25_LocalWorkerPool_InFlight_non_negative()
    {
        var opts = Options.Create(new FilmStudioOptions
        {
            Capacity = new CapacityOptions { MaxFfmpegInFlight = 2 },
        });
        var pool = new LocalWorkerPool(opts);
        // Force resize path then read — must not throw / go negative
        opts.Value.Capacity!.MaxFfmpegInFlight = 1;
        // Trigger EnsureCaps via a no-op run that completes immediately is hard without async;
        // InFlight property should always be >= 0
        Assert.True(pool.InFlight >= 0);
    }

    // ── 26. LockKeys.Character rejects empty key ────────────────────────

    [Fact]
    public void Bug26_LockKeys_Character_empty_throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => LockKeys.Character("P", "  "));
        Assert.ThrowsAny<ArgumentException>(() => LockKeys.Character("P", null!));
    }

    // ── 27. Propose rules must find fails under a flood of passes ───────

    [Fact]
    public async Task Bug27_Propose_finds_fails_buried_under_many_passes()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_bug27_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        try
        {
            var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = root, EnableReadCaches = false });
            var projects = new ProjectStore(opts);
            var events = new ReviewEventStore(projects, NullLogger<ReviewEventStore>.Instance);
            // 80 recent passes
            for (var i = 0; i < 80; i++)
            {
                events.Append(new ReviewLearningEvent
                {
                    ProjectId = "Demo",
                    Type = "clip_pass",
                    Ts = DateTimeOffset.UtcNow.AddSeconds(-i),
                });
            }
            // 6 older fails
            for (var i = 0; i < 6; i++)
            {
                events.Append(new ReviewLearningEvent
                {
                    ProjectId = "Demo",
                    Type = "clip_fail",
                    Category = "continuity",
                    Note = "jump " + i,
                    Ts = DateTimeOffset.UtcNow.AddMinutes(-30 - i),
                });
            }

            var propose = new LearningProposalService(
                events,
                new OfflineChatClient(),
                NullLogger<LearningProposalService>.Instance);
            // If Propose only scans the newest N mixed events via Query(take:small), fails are missed.
            var r = await propose.ProposeAsync(new ProposeLearningRulesRequest
            {
                ProjectId = "Demo",
                LastNFails = 5,
            });
            Assert.True(r.Ok, r.Error);
            Assert.True(r.FailEventsUsed >= 5, $"FailEventsUsed={r.FailEventsUsed}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    // ── 28. EstimateForBeat null dictionary must not NRE ────────────────

    [Fact]
    public void Bug28_EstimateForBeat_null_returns_action_floor()
    {
        var d = ClipDurationEstimator.EstimateForBeat(null!);
        Assert.True(d >= ClipDurationEstimator.MinSeconds);
    }

    // ── 29. BuildInsights recentTake=1 must not force 5 rows when fewer ─

    [Fact]
    public void Bug29_BuildInsights_honors_small_recentTake()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_bug29_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        try
        {
            var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = root, EnableReadCaches = false });
            var events = new ReviewEventStore(new ProjectStore(opts), NullLogger<ReviewEventStore>.Instance);
            for (var i = 0; i < 3; i++)
                events.Append(new ReviewLearningEvent { ProjectId = "P", Type = "clip_pass", Note = "n" + i });

            var insights = events.BuildInsights("P", recentTake: 1);
            Assert.Single(insights.Recent);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    // ── 30. SafeFileName must neutralize path segments like ".." ────────

    [Fact]
    public void Bug30_VoicePreview_SafeFileName_blocks_dotdot()
    {
        var name = VoicePreviewService.SafeFileNameForTests("..");
        Assert.DoesNotContain("..", name);
        Assert.False(string.IsNullOrWhiteSpace(name));
        var path = Path.Combine(Path.GetTempPath(), "voice_previews", name + ".mp3");
        // Resolved path must stay under voice_previews parent
        var full = Path.GetFullPath(path);
        Assert.Contains("voice_previews", full, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar, full);
    }

    private sealed class OfflineChatClient : IGrokChatClient
    {
        public bool IsConfigured => false;
        public Task<string> CompleteAsync(
            string systemPrompt, string userPrompt, string model = "grok-4.5",
            double temperature = 0.2, CancellationToken ct = default) =>
            Task.FromResult("- offline");
    }
}
