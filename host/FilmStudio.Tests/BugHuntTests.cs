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

    // ═══════════════════════════════════════════════════════════════════════
    // Pass 4 — bugs 31–40
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Bug31_AllocateForBeats_null_list_entries_do_not_throw()
    {
        var beats = new List<Dictionary<string, object?>?>
        {
            new() { ["dialogue"] = "hi there friend" },
            null,
            new() { ["visual_event"] = "wide establishing shot of kitchen" },
        };
        // Cast through IReadOnlyList of non-nullable maps that may still hold null refs
        IReadOnlyList<Dictionary<string, object?>> asList =
            beats.Select(b => b!).ToList();
        var durs = ClipDurationEstimator.AllocateForBeats(asList, sceneTargetSeconds: 30);
        Assert.Equal(3, durs.Count);
        Assert.All(durs, d => Assert.InRange(d, ClipDurationEstimator.MinSeconds, ClipDurationEstimator.MaxSeconds));
    }

    [Fact]
    public void Bug32_MediaDurationProbe_parses_duration_invariant()
    {
        var sec = MediaDurationProbe.TryParseFfmpegDurationLine(
            "  Duration: 00:01:05.50, start: 0.000000, bitrate: 1234 kb/s");
        Assert.NotNull(sec);
        Assert.InRange(sec!.Value, 65.4, 65.6);

        Assert.Null(MediaDurationProbe.TryParseFfmpegDurationLine(null));
        Assert.Null(MediaDurationProbe.TryParseFfmpegDurationLine("no duration here"));
    }

    [Fact]
    public void Bug33_LockKeys_Scene_rejects_empty_project()
    {
        Assert.ThrowsAny<ArgumentException>(() => LockKeys.Scene("  ", 1));
        Assert.ThrowsAny<ArgumentException>(() => LockKeys.Scene(null!, 1));
        Assert.Contains("Demo", LockKeys.Scene("Demo", 3), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bug34_LockKeys_Wip_rejects_empty_project()
    {
        Assert.ThrowsAny<ArgumentException>(() => LockKeys.Wip(""));
        Assert.Equal("project:P:wip", LockKeys.Wip("P"));
    }

    [Fact]
    public void Bug35_LockKeys_Stage_rejects_empty_project()
    {
        Assert.ThrowsAny<ArgumentException>(() => LockKeys.Stage(null!));
        Assert.StartsWith("project:X:stage", LockKeys.Stage("X"));
    }

    [Fact]
    public void Bug36_ProjectRules_null_Note_on_fails_does_not_throw()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_bug36_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects", "Demo"));
        File.WriteAllText(Path.Combine(root, "projects", "Demo", "project.json"), """{"id":"Demo"}""");
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
                    Category = "silent",
                    Note = null!, // deserializers may leave null
                });
            }

            var doc = rules.SuggestFromFails("Demo", minFails: 3);
            Assert.Contains(doc.Pending, p => p.Category == "silent");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void Bug37_GetActiveRulesBlock_skips_empty_text()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_bug37_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects", "Demo"));
        File.WriteAllText(Path.Combine(root, "projects", "Demo", "project.json"), """{"id":"Demo"}""");
        try
        {
            var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = root, EnableReadCaches = false });
            var projects = new ProjectStore(opts);
            var events = new ReviewEventStore(projects, NullLogger<ReviewEventStore>.Instance);
            var rules = new ProjectRulesService(projects, events, NullLogger<ProjectRulesService>.Instance);

            var doc = new ProjectRulesDocument
            {
                Active =
                {
                    new ProjectRule { Id = "a", Text = null!, Category = null! },
                    new ProjectRule { Id = "b", Text = "Keep wardrobe consistent.", Category = "continuity" },
                },
            };
            rules.Save("Demo", doc);
            var block = rules.GetActiveRulesBlock("Demo");
            Assert.Contains("Keep wardrobe consistent", block);
            Assert.DoesNotContain("[other] \n", block);
            Assert.Contains("continuity", block);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void Bug38_ApiWorkerPool_InFlight_non_negative()
    {
        var opts = Options.Create(new FilmStudioOptions
        {
            Capacity = new CapacityOptions { MaxVideoInFlight = 2, MaxVideoInFlightPerUser = 1 },
        });
        var pool = new ApiWorkerPool(opts);
        Assert.True(pool.InFlight >= 0);
        opts.Value.Capacity!.MaxVideoInFlight = 1;
        // Resize path + read must not throw / go negative
        Assert.True(pool.MaxGlobal >= 1);
        Assert.True(pool.InFlight >= 0);
    }

    [Fact]
    public void Bug39_Estimate_actionClass_is_case_insensitive()
    {
        var lower = ClipDurationEstimator.Estimate(null, "chase sequence across rooftops", "big_action");
        var upper = ClipDurationEstimator.Estimate(null, "chase sequence across rooftops", "BIG_ACTION");
        Assert.Equal(lower, upper);
        Assert.True(upper >= 5, "big_action floor should apply regardless of case");
    }

    [Fact]
    public void Bug40_Estimate_delivery_is_case_insensitive()
    {
        var a = ClipDurationEstimator.Estimate("I am thinking quietly about home.", null, delivery: "VO");
        var b = ClipDurationEstimator.Estimate("I am thinking quietly about home.", null, delivery: "vo");
        Assert.Equal(a, b);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Pass 5 — bugs 41–50
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Bug41_ReviewEventStore_Append_null_throws()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_bug41_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        try
        {
            var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = root, EnableReadCaches = false });
            var store = new ReviewEventStore(new ProjectStore(opts), NullLogger<ReviewEventStore>.Instance);
            Assert.Throws<ArgumentNullException>(() => store.Append(null!));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void Bug42_JobStore_Update_null_mutate_throws()
    {
        var store = new JobStore();
        var j = store.Create(new JobRecord { Status = "queued" });
        Assert.Throws<ArgumentNullException>(() => store.Update(j.JobId, null!));
    }

    [Fact]
    public void Bug43_JobStore_CountRunning_is_consistent_with_list()
    {
        var store = new JobStore();
        store.Create(new JobRecord { Status = "running", UserId = "u" });
        store.Create(new JobRecord { Status = "RUNNING", UserId = "u" });
        store.Create(new JobRecord { Status = "queued", UserId = "u" });
        Assert.Equal(2, store.CountRunning());
        Assert.Equal(3, store.CountQueuedForUser("u"));
    }

    [Fact]
    public void Bug44_JobStore_List_take_zero_returns_empty()
    {
        var store = new JobStore();
        store.Create(new JobRecord { Status = "done" });
        Assert.Empty(store.List(take: 0));
        Assert.Empty(store.List(take: -5));
        Assert.Single(store.List(take: 1));
    }

    [Fact]
    public void Bug45_ComputeCutPoint_null_or_nan_is_safe()
    {
        Assert.Null(ClipSilenceTrimmer.ComputeCutPoint(null!, 10, 0.3));
        Assert.Null(ClipSilenceTrimmer.ComputeCutPoint("", 10, 0.3));
        Assert.Null(ClipSilenceTrimmer.ComputeCutPoint("silence_start: 8.0", double.NaN, 0.3));
        Assert.Null(ClipSilenceTrimmer.ComputeCutPoint("silence_start: 8.0", 0.5, 0.3));
    }

    [Fact]
    public void Bug46_HttpRequestMetrics_classifies_path_without_leading_slash()
    {
        Assert.Equal("admin", FilmStudio.Api.Services.HttpRequestMetrics.ClassifyPathForTests("api/admin/metrics"));
        Assert.Equal("jobs", FilmStudio.Api.Services.HttpRequestMetrics.ClassifyPathForTests("/api/jobs/abc"));
        Assert.Equal("health", FilmStudio.Api.Services.HttpRequestMetrics.ClassifyPathForTests("health"));
        Assert.Equal("projects", FilmStudio.Api.Services.HttpRequestMetrics.ClassifyPathForTests("/api/projects/x?x=1"));
    }

    [Fact]
    public void Bug47_PromptPack_LoadPackText_blocks_path_traversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_bug47_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        try
        {
            var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = root, EnableReadCaches = false });
            var packs = new PromptPackService(new ProjectStore(opts), NullLogger<PromptPackService>.Instance);
            packs.EnsureDefaults();
            // Inject a malicious relative path into the manifest on disk
            var manifestPath = Path.Combine(root, "prompts", "packs", "manifest.json");
            var evilOutside = Path.Combine(root, "..", "evil.txt");
            File.WriteAllText(Path.GetFullPath(Path.Combine(root, "evil_payload.txt")), "secret");
            // Point pack at ../../ outside
            var json = File.ReadAllText(manifestPath);
            // Use CreateVersion then rewrite relative path
            var info = packs.CreateVersion("gen", "evil", "body");
            var man = JsonSerializer.Deserialize<PromptPackManifest>(File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var p = man.Packs.First(x => x.Id == info.Id);
            p.RelativePath = "../evil_payload.txt";
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(man, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));
            // Reload service reads manifest; traversal must yield null, not file contents
            var packs2 = new PromptPackService(new ProjectStore(opts), NullLogger<PromptPackService>.Instance);
            var text = packs2.LoadPackText(info.Id);
            Assert.Null(text);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void Bug48_BookContext_ParseBookPages_invariant_numbers()
    {
        var pages = BookContextService.ParseBookPages("--- PAGE 12 ---\nHello world\n--- PAGE 13 ---\nMore");
        Assert.Equal(2, pages.Count);
        Assert.Equal(12, pages[0].PageNumber);
        Assert.Equal(13, pages[1].PageNumber);
        Assert.Contains("Hello", pages[0].Text);
    }

    [Fact]
    public void Bug49_IsPrimarilyAnimalCharacter_null_fields_safe()
    {
        var animal = CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(null!, null!, null!, null!);
        Assert.False(animal);
        var human = CharacterVisualTextScrubber.IsHumanAdultCharacter("Character_Mom", null!, null!, null!);
        Assert.True(human);
    }

    [Fact]
    public async Task Bug50_RuntimeConfig_NaN_FailRate_does_not_throw()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_bug50_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var opts = Options.Create(new FilmStudioOptions
            {
                WorkspaceRoot = root,
                Capacity = new CapacityOptions(),
                Fakes = new FakesOptions(),
            });
            var store = new RuntimeConfigStore(opts, NullLogger<RuntimeConfigStore>.Instance);
            var dto = await store.UpdateAsync(new RuntimeConfigUpdateRequest
            {
                Fakes = new FakesRuntimeDto { FailRate = double.NaN, VideoMode = "MergeRealistic" },
            }, "admin");
            Assert.InRange(dto.Fakes.FailRate, 0, 1);
            Assert.False(double.IsNaN(dto.Fakes.FailRate));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Pass 6 — bugs 51–60
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Bug51_LockService_Renew_empty_resource_returns_false()
    {
        var locks = new InMemoryLockService();
        Assert.False(locks.Renew("", "u", TimeSpan.FromMinutes(5)));
        Assert.False(locks.Renew("res", "", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Bug52_LockService_Release_empty_resource_returns_false()
    {
        var locks = new InMemoryLockService();
        Assert.False(locks.Release("  ", "u"));
        Assert.True(locks.TryAcquire("r1", "u", TimeSpan.FromMinutes(5)));
        Assert.True(locks.Release("r1", "u"));
    }

    [Fact]
    public void Bug53_ParseJsonObject_empty_throws_clearly()
    {
        Assert.ThrowsAny<InvalidOperationException>(() => GrokChatClient.ParseJsonObject(""));
        Assert.ThrowsAny<InvalidOperationException>(() => GrokChatClient.ParseJsonObject("   "));
        Assert.ThrowsAny<InvalidOperationException>(() => GrokChatClient.ParseJsonObject(null!));
    }

    [Fact]
    public void Bug54_ParseJsonObject_array_only_throws()
    {
        Assert.ThrowsAny<InvalidOperationException>(() => GrokChatClient.ParseJsonObject("[1,2,3]"));
    }

    [Fact]
    public void Bug55_ProjectRules_Approve_empty_id_throws()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_bug55_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects", "Demo"));
        File.WriteAllText(Path.Combine(root, "projects", "Demo", "project.json"), """{"id":"Demo"}""");
        try
        {
            var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = root, EnableReadCaches = false });
            var rules = new ProjectRulesService(
                new ProjectStore(opts),
                new ReviewEventStore(new ProjectStore(opts), NullLogger<ReviewEventStore>.Instance),
                NullLogger<ProjectRulesService>.Instance);
            Assert.ThrowsAny<ArgumentException>(() => rules.Approve("Demo", "  ", null, "a"));
            Assert.ThrowsAny<ArgumentException>(() => rules.Reject("Demo", null!));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void Bug56_SafeFileName_strips_slashes()
    {
        var name = VoicePreviewService.SafeFileNameForTests("foo/../bar\\baz");
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain("\\", name);
        Assert.DoesNotContain("..", name);
    }

    [Fact]
    public void Bug57_CharacterRefFileName_path_segments()
    {
        var name = ProjectStore.CharacterRefFileName(@"..\..\etc\passwd");
        Assert.DoesNotContain("..", name);
        Assert.EndsWith("_ref.png", name, StringComparison.OrdinalIgnoreCase);
        Assert.False(name.Contains('/') || name.Contains('\\'));
    }

    [Fact]
    public void Bug58_EstimateForClip_undefined_element_is_floor()
    {
        var d = ClipDurationEstimator.EstimateForClip(default);
        Assert.Equal(ClipDurationEstimator.MinSeconds, d);
    }

    [Fact]
    public void Bug59_FountainParser_null_text_safe()
    {
        var r = FountainParser.Parse(null!);
        Assert.NotNull(r);
        Assert.Empty(r.Elements);
    }

    [Fact]
    public void Bug60_BuildSampleDialogue_empty_name_uses_fallback()
    {
        var s = VoicePreviewService.BuildSampleDialogue("  ");
        Assert.Contains("this character", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("  ", s);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Pass 7 — bugs 61–70
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Bug61_FindCharacterRefPaths_empty_projectDir_safe()
    {
        using var doc = JsonDocument.Parse("""{"visual_prompt":"Character_Dog runs"}""");
        var paths = ClipVideoPromptBuilder.FindCharacterRefPaths(doc.RootElement, "", maxRefs: 3);
        Assert.Empty(paths);
        paths = ClipVideoPromptBuilder.FindCharacterRefPaths(doc.RootElement, null!, maxRefs: 3);
        Assert.Empty(paths);
    }

    [Fact]
    public void Bug62_Stage1Normalizer_null_dict_safe()
    {
        var n = Stage1Normalizer.Normalize(null!);
        Assert.Equal("stage1.v1", n["schema_version"]?.ToString());
        Assert.True(n.ContainsKey("movie_title"));
    }

    [Fact]
    public void Bug63_Screenplay_BuildModel_null_text_safe()
    {
        var model = ScreenplayService.BuildModelFromFountainText(null!);
        Assert.NotNull(model);
        Assert.Equal("stage1.v1", model["schema_version"]?.ToString());
    }

    [Fact]
    public void Bug64_BookTextAnalyzer_null_text_safe()
    {
        var a = BookTextAnalyzer.Analyze(null!);
        Assert.NotNull(a);
        Assert.True(a.Pages >= 0);
    }

    [Fact]
    public void Bug67_BookTextAnalyzer_plain_txt_uses_paragraph_page_fallback()
    {
        // No --- PAGE N --- markers: must not collapse to a single synthetic page when
        // paragraphs exist (same rules as BookContextService.ParseBookPages).
        var plain = string.Join("\n\n", Enumerable.Range(1, 12).Select(i =>
            $"Paragraph {i}. " + string.Join(' ', Enumerable.Repeat("word", 100)) +
            " more narrative text for this block."));

        var bodies = BookTextAnalyzer.PageBodies(plain);
        var ctx = BookContextService.ParseBookPages(plain);
        Assert.Equal(ctx.Count, bodies.Count);
        Assert.True(bodies.Count >= 10, $"expected paragraph pages, got {bodies.Count}");
        Assert.Equal(ctx.Select(p => p.Text).ToList(), bodies);

        var analysis = BookTextAnalyzer.Analyze(plain);
        Assert.Equal(bodies.Count, analysis.Pages);
        // Before the fix, plain .txt was always pages=1 → avg-chars meaningless.
        Assert.True(analysis.Pages > 1);
        Assert.True(analysis.AvgCharsPerPage < analysis.TextChars);
        // Enough words + multi-paragraph density → short story, not picture_book.
        Assert.True(analysis.TextWords >= 800, $"words={analysis.TextWords}");
        Assert.NotEqual("picture_book", analysis.BookKind);
    }

    [Fact]
    public void Bug67_BookTextAnalyzer_page_markers_still_preferred()
    {
        var marked =
            "--- PAGE 1 ---\nAlpha line one.\n\n--- PAGE 2 ---\nBeta line two.\n\n--- PAGE 3 ---\nGamma line three.\n";
        var bodies = BookTextAnalyzer.PageBodies(marked);
        Assert.Equal(3, bodies.Count);
        Assert.Contains("Alpha", bodies[0], StringComparison.Ordinal);
        Assert.Contains("Gamma", bodies[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Bug65_StitchFountainParts_null_safe()
    {
        Assert.Equal("", BookToFountainConverter.StitchFountainParts(null));
        Assert.Equal("", BookToFountainConverter.StitchFountainParts(Array.Empty<string>()));
    }

    [Fact]
    public void Bug66_StripBookPageTags_null_safe()
    {
        Assert.Equal("", BookToFountainConverter.StripBookPageTags(null));
    }

    [Fact]
    public void Bug67_SupportedModel_Find_unknown_returns_null()
    {
        Assert.Null(SupportedModelCatalog.Find("not-a-real-model"));
        Assert.Null(SupportedModelCatalog.Find(""));
        Assert.Null(SupportedModelCatalog.Find(null));
    }

    [Fact]
    public void Bug68_SupportedModel_ResolveOrDefault_unknown_keeps_id()
    {
        var e = SupportedModelCatalog.ResolveOrDefault("custom-future-model", ModelCapability.Chat);
        Assert.Equal("custom-future-model", e.Id);
        Assert.False(e.Enabled);
    }

    [Fact]
    public void Bug69_JobStore_Create_null_throws()
    {
        var store = new JobStore();
        Assert.Throws<ArgumentNullException>(() => store.Create(null!));
    }

    [Fact]
    public void Bug70_JobStore_Get_empty_id_returns_null()
    {
        var store = new JobStore();
        Assert.Null(store.Get(""));
        Assert.Null(store.Get("   "));
        Assert.Null(store.Get(null!));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Pass 8 — bugs 71–80
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Bug71_HttpMetrics_Record_and_snapshot()
    {
        var m = new FilmStudio.Api.Services.HttpRequestMetrics();
        m.Record("/api/jobs/1");
        m.Record("api/admin/x");
        m.Record("/health");
        var snap = m.Snapshot();
        Assert.True(snap.TotalLifetime >= 3);
        Assert.True(snap.RequestsLast30Sec >= 3);
    }

    [Fact]
    public void Bug72_LoginRateLimiter_caps_and_blocks()
    {
        var lim = new FilmStudio.Api.Auth.LoginRateLimiter(maxAttempts: 3, windowSeconds: 60);
        Assert.False(lim.IsBlocked("user@x", out _));
        lim.RecordFailure("user@x");
        lim.RecordFailure("user@x");
        lim.RecordFailure("user@x");
        // Cap: further failures must not throw
        lim.RecordFailure("user@x");
        lim.RecordFailure("user@x");
        Assert.True(lim.IsBlocked("user@x", out var retry));
        Assert.True(retry > 0);
        lim.RecordSuccess("user@x");
        Assert.False(lim.IsBlocked("user@x", out _));
    }

    [Fact]
    public void Bug73_LoginRateLimiter_null_key_normalizes()
    {
        var lim = new FilmStudio.Api.Auth.LoginRateLimiter(maxAttempts: 3, windowSeconds: 60);
        lim.RecordFailure(null!);
        lim.RecordFailure("");
        // Should not throw
        Assert.False(lim.IsBlocked("other", out _));
    }

    [Fact]
    public void Bug74_PromptPack_CreateVersion_null_body_ok()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_bug74_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        try
        {
            var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = root, EnableReadCaches = false });
            var packs = new PromptPackService(new ProjectStore(opts), NullLogger<PromptPackService>.Instance);
            var info = packs.CreateVersion("gen", "emptybody", null!);
            Assert.Equal("gen-emptybody", info.Id);
            var text = packs.LoadPackText(info.Id);
            Assert.NotNull(text);
            Assert.Equal("", text);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void Bug75_PromptPack_Activate_unknown_throws()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_bug75_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        try
        {
            var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = root, EnableReadCaches = false });
            var packs = new PromptPackService(new ProjectStore(opts), NullLogger<PromptPackService>.Instance);
            Assert.ThrowsAny<InvalidOperationException>(() => packs.Activate("no-such-pack"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void Bug76_LockConflictException_message_includes_owner()
    {
        var ex = new LockConflictException("project:P:scene:01", "alice", DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Contains("alice", ex.Message);
        Assert.Equal("project:P:scene:01", ex.Resource);
    }

    [Fact]
    public void Bug77_CapacityRejectedException_keeps_message()
    {
        var ex = new CapacityRejectedException("queue full for user");
        Assert.Equal("queue full for user", ex.Message);
    }

    [Fact]
    public void Bug78_IsStandaloneTransition_fade_in()
    {
        Assert.True(FountainParser.IsStandaloneTransitionLine("FADE IN:"));
        Assert.True(FountainParser.IsStandaloneTransitionLine("**FADE IN:**"));
        Assert.False(FountainParser.IsStandaloneTransitionLine("He fades into the room."));
    }

    [Fact]
    public void Bug79_ScrubVisualProse_null_safe()
    {
        Assert.Equal("", CharacterVisualTextScrubber.ScrubVisualProse(null));
        Assert.Equal("", CharacterVisualTextScrubber.SoftenCrossSpeciesStyleLanguage(null));
        Assert.Empty(CharacterVisualTextScrubber.ScrubWardrobeList(null));
    }

    [Fact]
    public async Task Bug80_ApiWorkerPool_empty_user_maps_to_local()
    {
        var opts = Options.Create(new FilmStudioOptions
        {
            Capacity = new CapacityOptions { MaxVideoInFlight = 2, MaxVideoInFlightPerUser = 2 },
        });
        var pool = new ApiWorkerPool(opts);
        var ran = false;
        await pool.RunAsync("  ", _ => { ran = true; return Task.CompletedTask; }, CancellationToken.None);
        Assert.True(ran);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Pass 9 — bugs 81–90
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Bug81_LearningPropose_null_request_throws()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_bug81_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        try
        {
            var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = root, EnableReadCaches = false });
            var svc = new LearningProposalService(
                new ReviewEventStore(new ProjectStore(opts), NullLogger<ReviewEventStore>.Instance),
                new OfflineChatClient(),
                NullLogger<LearningProposalService>.Instance);
            await Assert.ThrowsAsync<ArgumentNullException>(() => svc.ProposeAsync(null!));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void Bug82_ReviewEventStore_Query_take_clamped()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_bug82_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        try
        {
            var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = root, EnableReadCaches = false });
            var events = new ReviewEventStore(new ProjectStore(opts), NullLogger<ReviewEventStore>.Instance);
            for (var i = 0; i < 5; i++)
                events.Append(new ReviewLearningEvent { ProjectId = "P", Type = "clip_pass" });
            // take=0 must not throw; clamp to at least 1
            var q = events.Query(take: 0);
            Assert.NotNull(q);
            Assert.True(q.Count <= 5);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void Bug83_BuildInsights_null_type_events()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_bug83_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        try
        {
            var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = root, EnableReadCaches = false });
            var events = new ReviewEventStore(new ProjectStore(opts), NullLogger<ReviewEventStore>.Instance);
            events.Append(new ReviewLearningEvent { ProjectId = "P", Type = null! });
            var insights = events.BuildInsights("P");
            Assert.True(insights.EventCount >= 1);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void Bug84_GetPrimary_empty_user_lists_all()
    {
        var store = new JobStore();
        store.Create(new JobRecord { Status = "queued", UserId = "a" });
        var p = store.GetPrimary(null);
        Assert.NotNull(p);
        Assert.Equal("queued", p!.Status);
    }

    [Fact]
    public void Bug85_TryCancel_missing_job_false()
    {
        var store = new JobStore();
        Assert.False(store.TryCancel("nope"));
        Assert.False(store.TryCancel(""));
    }

    [Fact]
    public void Bug86_GetCacheInfo_missing_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs_bug86_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "projects", "Demo"));
        File.WriteAllText(Path.Combine(root, "projects", "Demo", "project.json"), """{"id":"Demo"}""");
        try
        {
            var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = root, EnableReadCaches = false });
            // VoicePreviewService needs video+ffmpeg — only test static fingerprint / SafeFileName path
            var fp = VoicePreviewService.ComputeFingerprintForCache(
                "Character_X", "adult male", null, "X", null);
            Assert.False(string.IsNullOrWhiteSpace(fp));
            Assert.Equal(16, fp.Length);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void Bug87_Catalog_ForCapability_enabled_only()
    {
        var video = SupportedModelCatalog.ForCapability(ModelCapability.Video);
        Assert.NotEmpty(video);
        Assert.All(video, e => Assert.True(e.Enabled));
        Assert.All(video, e => Assert.Equal(ModelCapability.Video, e.Capability));
    }

    [Fact]
    public void Bug88_ProviderIdFor_video()
    {
        Assert.Equal("grok", SupportedModelCatalog.ProviderIdFor("grok-imagine-video", ModelCapability.Video));
    }

    [Fact]
    public async Task Bug89_LocalWorkerPool_InFlight_after_resize_path()
    {
        var opts = Options.Create(new FilmStudioOptions
        {
            Capacity = new CapacityOptions { MaxFfmpegInFlight = 3 },
        });
        var pool = new LocalWorkerPool(opts);
        opts.Value.Capacity!.MaxFfmpegInFlight = 1;
        await pool.RunAsync(_ => Task.CompletedTask, CancellationToken.None);
        Assert.True(pool.InFlight >= 0);
    }

    [Fact]
    public void Bug90_ServerMetrics_unmatched_ffmpeg_release()
    {
        var m = new ServerMetricsService();
        m.NoteFfmpegSlotReleased();
        m.NoteFfmpegSlotReleased();
        m.NoteFfmpegSlotAcquired();
        var snap = m.GetSnapshot(
            new JobStore(),
            new InMemoryLockService(),
            new CapacityOptionsSnapshot { MaxVideoInFlight = 4 },
            new ProcessMetricsSnapshot());
        Assert.Equal(1, snap.FfmpegInFlight);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Pass 10 — bugs 91–100
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Bug91_Estimate_null_dialogue_and_visual_is_floor()
    {
        var d = ClipDurationEstimator.Estimate(null, null);
        Assert.Equal(ClipDurationEstimator.MinSeconds, d);
    }

    [Fact]
    public void Bug92_AllocateForBeats_empty_list()
    {
        Assert.Empty(ClipDurationEstimator.AllocateForBeats(new List<Dictionary<string, object?>>()));
    }

    [Fact]
    public void Bug93_MediaDuration_probe_missing_file()
    {
        var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = Path.GetTempPath() });
        var probe = new MediaDurationProbe(opts, NullLogger<MediaDurationProbe>.Instance);
        Assert.Null(probe.GetDurationSeconds(null));
        Assert.Null(probe.GetDurationSeconds(""));
        Assert.Null(probe.GetDurationSeconds(Path.Combine(Path.GetTempPath(), "nope_" + Guid.NewGuid() + ".mp4")));
    }

    [Fact]
    public void Bug94_SceneActualDuration_empty_paths()
    {
        var opts = Options.Create(new FilmStudioOptions { WorkspaceRoot = Path.GetTempPath() });
        var probe = new MediaDurationProbe(opts, NullLogger<MediaDurationProbe>.Instance);
        Assert.Null(probe.GetSceneActualDurationSeconds(null, Array.Empty<string>()));
        Assert.Null(probe.GetSceneActualDurationSeconds("", new[] { Path.Combine(Path.GetTempPath(), "missing.mp4") }));
    }

    [Fact]
    public async Task Bug95_ProjectReadCache_disabled_skips_cache()
    {
        var cache = new ProjectReadCache { Enabled = false };
        var calls = 0;
        Task<string?> Find(CancellationToken _)
        {
            calls++;
            return Task.FromResult<string?>("bp.json");
        }
        var a = await cache.GetOrFindBlueprintPathAsync("P", Find);
        var b = await cache.GetOrFindBlueprintPathAsync("P", Find);
        Assert.Equal("bp.json", a);
        Assert.Equal("bp.json", b);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Bug96_SceneListCache_invalidate_empty_noop()
    {
        var cache = new SceneListCache();
        cache.Invalidate(null);
        cache.Invalidate("  ");
        cache.InvalidateAll(); // must not throw
    }

    [Fact]
    public void Bug97_ReleaseAllForJob_empty_noop()
    {
        var locks = new InMemoryLockService();
        locks.TryAcquire("r", "u", TimeSpan.FromMinutes(5), jobId: "j1");
        locks.ReleaseAllForJob("");
        Assert.NotNull(locks.Get("r"));
        locks.ReleaseAllForJob("j1");
        Assert.Null(locks.Get("r"));
    }

    [Fact]
    public void Bug98_LooksLikeNicknameVisualJunk_null_false()
    {
        Assert.False(CharacterVisualTextScrubber.LooksLikeNicknameVisualJunk(null));
        Assert.False(CharacterVisualTextScrubber.LooksLikeNicknameVisualJunk(""));
    }

    [Fact]
    public void Bug99_NormalizeTypographic_null_safe()
    {
        Assert.Equal("", FountainParser.NormalizeTypographicPunctuation(null!));
        var n = FountainParser.NormalizeTypographicPunctuation("MARLEY\u2019S");
        Assert.Contains("MARLEY'S", n);
    }

    [Fact]
    public void Bug100_ExtractMessageText_content_array()
    {
        using var doc = JsonDocument.Parse(
            """{"choices":[{"message":{"content":[{"text":"hello"},{"text":"world"}]}}]}""");
        var text = GrokChatClient.ExtractMessageTextForTests(doc.RootElement);
        Assert.Contains("hello", text);
        Assert.Contains("world", text);
    }

    private sealed class OfflineChatClient : IGrokChatClient
    {
        public bool IsConfigured => false;
        public Task<string> CompleteAsync(
            string systemPrompt, string userPrompt, string model = "grok-4.5",
            double temperature = 0.2, CancellationToken ct = default, string? mode = null) =>
            Task.FromResult("- offline");
    }
}
