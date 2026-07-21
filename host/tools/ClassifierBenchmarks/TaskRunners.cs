using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FilmStudio.Engine;

namespace ClassifierBenchmarks;

public static class TaskRunners
{
    public static async Task<TaskResult> RunAmbientAsync(
        BenchPaths paths,
        string projectId,
        string model,
        double temperature,
        PromptBundle prompt,
        ChatRunner chat,
        CancellationToken ct = default)
    {
        var goldPath = paths.GoldFile(projectId, "ambient_sfx");
        if (!File.Exists(goldPath))
            throw new FileNotFoundException($"Missing gold: {goldPath}");

        using var goldDoc = JsonDocument.Parse(await File.ReadAllTextAsync(goldPath, ct));
        var root = goldDoc.RootElement;
        var curated = root.TryGetProperty("curated", out var cEl) && cEl.GetBoolean();
        var labels = root.GetProperty("labels");
        var samples = new List<(string Id, string Visual, string Ga, string Gs)>();
        foreach (var el in labels.EnumerateArray())
        {
            var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            if (id.Length == 0) continue;
            var visual = el.TryGetProperty("visual", out var vEl) ? vEl.GetString() ?? "" : "";
            var ga = el.TryGetProperty("gold_ambient", out var aEl) ? aEl.GetString() ?? "" : "";
            var gs = el.TryGetProperty("gold_sfx", out var sEl) ? sEl.GetString() ?? "" : "";
            samples.Add((id, visual, ga, gs));
        }

        var payload = samples.Select(s =>
        {
            var (ha, hs) = FountainStage1Importer.InferAmbientAndSfx(s.Visual);
            var vis = s.Visual.Length > 400 ? s.Visual[..400] + "…" : s.Visual;
            return new
            {
                id = s.Id,
                visual_event = vis,
                heuristic_ambient = ha,
                heuristic_sfx = hs,
            };
        }).ToList();

        var sw = Stopwatch.StartNew();
        var raw = await chat.CompleteAsync(
            model, temperature, prompt.Text,
            "Split each beat into ambient bed vs sfx hits. JSON only.\n" +
            JsonSerializer.Serialize(new { beats = payload }),
            ct);
        sw.Stop();

        var aiMap = AmbientSfxClassifier.ParseLabels(raw);
        var sampleScores = new List<SampleScore>();
        double baseSum = 0, aiSum = 0;
        var hits = 0;
        foreach (var s in samples)
        {
            var (ha, hs) = FountainStage1Importer.InferAmbientAndSfx(s.Visual);
            aiMap.TryGetValue(s.Id, out var pair);
            if (aiMap.ContainsKey(s.Id)) hits++;
            var aa = pair.Ambient ?? "";
            var asx = pair.Sfx ?? "";
            var bScore = (AmbientSfxClassifier.TokenJaccard(ha, s.Ga) + AmbientSfxClassifier.TokenJaccard(hs, s.Gs)) / 2.0;
            var aScore = (AmbientSfxClassifier.TokenJaccard(aa, s.Ga) + AmbientSfxClassifier.TokenJaccard(asx, s.Gs)) / 2.0;
            baseSum += bScore;
            aiSum += aScore;
            sampleScores.Add(new SampleScore
            {
                Id = s.Id,
                Visual = Trunc(s.Visual, 220),
                GoldAmbient = s.Ga,
                GoldSfx = s.Gs,
                BaselineAmbient = ha,
                BaselineSfx = hs,
                AiAmbient = aa,
                AiSfx = asx,
                BaselineScore = bScore,
                AiScore = aScore,
            });
        }

        var n = samples.Count;
        var baseMean = n == 0 ? 0 : baseSum / n;
        var aiMean = n == 0 ? 0 : aiSum / n;
        return new TaskResult
        {
            Task = "ambient_sfx",
            ProjectId = projectId,
            Model = model,
            PromptId = prompt.Id,
            PromptLabel = prompt.Label,
            PromptHash = prompt.Hash,
            Temperature = temperature,
            CuratedGold = curated,
            SampleCount = n,
            Metric = "mean_token_jaccard",
            BaselineScore = baseMean,
            AiScore = aiMean,
            Winner = Winner(baseMean, aiMean),
            LatencyMs = sw.ElapsedMilliseconds,
            AiParseHits = hits,
            Note = prompt.Notes,
            Samples = sampleScores,
        };
    }

    public static async Task<TaskResult> RunSpeciesAsync(
        BenchPaths paths,
        string projectId,
        string model,
        double temperature,
        PromptBundle prompt,
        ChatRunner chat,
        CancellationToken ct = default)
    {
        var goldPath = paths.GoldFile(projectId, "species_kind");
        if (!File.Exists(goldPath))
            throw new FileNotFoundException($"Missing gold: {goldPath}");

        using var goldDoc = JsonDocument.Parse(await File.ReadAllTextAsync(goldPath, ct));
        var root = goldDoc.RootElement;
        var labelsEl = root.TryGetProperty("labels", out var l) ? l : root;
        var samples = new List<(string Key, string Desc, string Gold)>();
        foreach (var el in labelsEl.EnumerateArray())
        {
            var key = el.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
            if (key.Length == 0) continue;
            var desc = el.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var gold = el.TryGetProperty("gold", out var g) ? g.GetString() ?? "" : "";
            samples.Add((key, desc, gold));
        }

        var payload = samples.Select(s => new
        {
            key = s.Key,
            description = Trunc(s.Desc, 280),
            visual_lock = "",
            heuristic = SpeciesKindClassifier.BaselineKind(s.Key, s.Desc, ""),
        }).ToList();

        var sw = Stopwatch.StartNew();
        var raw = await chat.CompleteAsync(
            model, temperature, prompt.Text,
            "Label animal|human|other. JSON only.\n" + JsonSerializer.Serialize(new { cast = payload }),
            ct);
        sw.Stop();

        var aiMap = SpeciesKindClassifier.ParseLabels(raw);
        var sampleScores = new List<SampleScore>();
        int baseOk = 0, aiOk = 0, hits = 0;
        foreach (var s in samples)
        {
            var h = SpeciesKindClassifier.BaselineKind(s.Key, s.Desc, "");
            aiMap.TryGetValue(s.Key, out var ac);
            if (aiMap.ContainsKey(s.Key)) hits++;
            ac ??= "";
            var b = string.Equals(h, s.Gold, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
            var a = string.Equals(ac, s.Gold, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
            baseOk += (int)b;
            aiOk += (int)a;
            sampleScores.Add(new SampleScore
            {
                Id = s.Key,
                Visual = Trunc(s.Desc, 180),
                GoldLabel = s.Gold,
                BaselineLabel = h,
                AiLabel = ac,
                BaselineScore = b,
                AiScore = a,
            });
        }

        var n = samples.Count;
        var baseMean = n == 0 ? 0 : (double)baseOk / n;
        var aiMean = n == 0 ? 0 : (double)aiOk / n;
        return new TaskResult
        {
            Task = "species_kind",
            ProjectId = projectId,
            Model = model,
            PromptId = prompt.Id,
            PromptLabel = prompt.Label,
            PromptHash = prompt.Hash,
            Temperature = temperature,
            CuratedGold = true,
            SampleCount = n,
            Metric = "accuracy",
            BaselineScore = baseMean,
            AiScore = aiMean,
            Winner = Winner(baseMean, aiMean),
            LatencyMs = sw.ElapsedMilliseconds,
            AiParseHits = hits,
            Note = prompt.Notes,
            Samples = sampleScores,
        };
    }

    public static async Task<TaskResult> RunOnScreenCastAsync(
        BenchPaths paths,
        string projectId,
        string model,
        double temperature,
        PromptBundle prompt,
        ChatRunner chat,
        CancellationToken ct = default)
    {
        var goldPath = paths.GoldFile(projectId, "onscreen_cast");
        if (!File.Exists(goldPath))
            throw new FileNotFoundException($"Missing gold: {goldPath}");

        using var goldDoc = JsonDocument.Parse(await File.ReadAllTextAsync(goldPath, ct));
        var root = goldDoc.RootElement;
        var curated = root.TryGetProperty("curated", out var cEl) && cEl.GetBoolean();

        var samples = new List<(string Id, string Visual, string Dialogue, string Speaker, bool Vo, List<string> Gold)>();
        foreach (var el in root.GetProperty("labels").EnumerateArray())
        {
            var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            if (id.Length == 0) continue;
            var visual = el.TryGetProperty("visual", out var vEl) ? vEl.GetString() ?? "" : "";
            var dialogue = el.TryGetProperty("dialogue", out var dEl) ? dEl.GetString() ?? "" : "";
            var speaker = el.TryGetProperty("speaker", out var sEl) ? sEl.GetString() ?? "" : "";
            var vo = el.TryGetProperty("is_voiceover", out var voEl) && voEl.ValueKind == JsonValueKind.True;
            var gold = new List<string>();
            if (el.TryGetProperty("gold_keys", out var gk) && gk.ValueKind == JsonValueKind.Array)
            {
                foreach (var k in gk.EnumerateArray())
                    if (k.GetString() is { Length: > 0 } ks)
                        gold.Add(ks);
            }
            samples.Add((id, visual, dialogue, speaker, vo, gold));
        }

        var castKeys = await LoadCastKeysAsync(paths.RepoRoot, projectId, samples, ct);
        var profiles = castKeys.ToDictionary(
            k => k,
            k => new ClipVideoPromptBuilder.CharacterProfile
            {
                DisplayName = k.Replace("Character_", "", StringComparison.OrdinalIgnoreCase).Replace('_', ' '),
            },
            StringComparer.OrdinalIgnoreCase);

        var payload = samples.Select(s =>
        {
            var h = BaselineOnScreen(s.Visual, s.Dialogue, s.Speaker, s.Vo, profiles);
            return new Dictionary<string, object?>
            {
                ["id"] = s.Id,
                ["visual_event"] = Trunc(s.Visual, 280),
                ["dialogue"] = Trunc(s.Dialogue, 120),
                ["speaker_key"] = s.Speaker,
                ["is_voiceover"] = s.Vo,
                ["heuristic_keys"] = h,
            };
        }).ToList();

        var sw = Stopwatch.StartNew();
        var raw = await chat.CompleteAsync(
            model, temperature, prompt.Text,
            "Pick on-screen Character_* keys from the closed cast. JSON only.\n" +
            JsonSerializer.Serialize(new { cast_keys = castKeys, beats = payload }),
            ct);
        sw.Stop();

        var aiMap = OnScreenCastClassifier.ParseLabels(raw, castKeys);
        var sampleScores = new List<SampleScore>();
        double baseSum = 0, aiSum = 0;
        var hits = 0;
        foreach (var s in samples)
        {
            var h = BaselineOnScreen(s.Visual, s.Dialogue, s.Speaker, s.Vo, profiles);
            aiMap.TryGetValue(s.Id, out var ak);
            if (aiMap.ContainsKey(s.Id)) hits++;
            ak ??= new List<string>();
            var bScore = OnScreenCastClassifier.SetF1(h, s.Gold);
            var aScore = OnScreenCastClassifier.SetF1(ak, s.Gold);
            baseSum += bScore;
            aiSum += aScore;
            sampleScores.Add(new SampleScore
            {
                Id = s.Id,
                Visual = Trunc(s.Visual, 220),
                GoldLabel = string.Join(", ", s.Gold),
                BaselineLabel = string.Join(", ", h),
                AiLabel = string.Join(", ", ak),
                BaselineScore = bScore,
                AiScore = aScore,
            });
        }

        var n = samples.Count;
        var baseMean = n == 0 ? 0 : baseSum / n;
        var aiMean = n == 0 ? 0 : aiSum / n;
        return new TaskResult
        {
            Task = "onscreen_cast",
            ProjectId = projectId,
            Model = model,
            PromptId = prompt.Id,
            PromptLabel = prompt.Label,
            PromptHash = prompt.Hash,
            Temperature = temperature,
            CuratedGold = curated,
            SampleCount = n,
            Metric = "mean_set_f1",
            BaselineScore = baseMean,
            AiScore = aiMean,
            Winner = Winner(baseMean, aiMean),
            LatencyMs = sw.ElapsedMilliseconds,
            AiParseHits = hits,
            Note = prompt.Notes,
            Samples = sampleScores,
        };
    }

    static List<string> BaselineOnScreen(
        string visual,
        string dialogue,
        string speaker,
        bool isVoiceover,
        Dictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles)
    {
        var inferred = ClipVideoPromptBuilder.InferKeysFromProse(visual + " " + dialogue, profiles);
        if (!string.IsNullOrWhiteSpace(speaker) &&
            !isVoiceover &&
            !inferred.Contains(speaker, StringComparer.OrdinalIgnoreCase))
            inferred.Add(speaker);
        return inferred;
    }

    static async Task<List<string>> LoadCastKeysAsync(
        string repoRoot,
        string projectId,
        List<(string Id, string Visual, string Dialogue, string Speaker, bool Vo, List<string> Gold)> samples,
        CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var castPath = Directory.GetFiles(Path.Combine(repoRoot, "projects"), "cast_seeds.json", SearchOption.AllDirectories)
            .FirstOrDefault(p => p.Contains(projectId, StringComparison.OrdinalIgnoreCase) &&
                                 !p.Contains($"{Path.DirectorySeparatorChar}_", StringComparison.Ordinal));
        if (castPath is not null && File.Exists(castPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(castPath, ct));
                if (doc.RootElement.TryGetProperty("character_seed_tokens", out var seeds) &&
                    seeds.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in seeds.EnumerateObject())
                        if (p.Name.StartsWith("Character_", StringComparison.OrdinalIgnoreCase))
                            keys.Add(p.Name);
                }
            }
            catch { /* fall through */ }
        }

        foreach (var s in samples)
        {
            foreach (var g in s.Gold) keys.Add(g);
            if (!string.IsNullOrWhiteSpace(s.Speaker)) keys.Add(s.Speaker);
        }

        if (keys.Count == 0)
            throw new InvalidOperationException($"No cast keys for project {projectId} (need cast_seeds.json or gold keys).");
        return keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<TaskResult> RunSilentBeatActionAsync(
        BenchPaths paths,
        string projectId,
        string model,
        double temperature,
        PromptBundle prompt,
        ChatRunner chat,
        CancellationToken ct = default)
    {
        // Multi-book gold lives under gold/_all_books (ignore --project for path; keep for metadata).
        var goldPath = paths.GoldFile("_all_books", "silent_beat_action");
        if (!File.Exists(goldPath))
            throw new FileNotFoundException($"Missing gold: {goldPath}");

        using var goldDoc = JsonDocument.Parse(await File.ReadAllTextAsync(goldPath, ct));
        var root = goldDoc.RootElement;
        var curated = root.TryGetProperty("curated", out var cEl) && cEl.GetBoolean();
        var samples = new List<(string Key, string Project, string Id, string Visual, bool IsFirst, string Gold)>();
        foreach (var el in root.GetProperty("labels").EnumerateArray())
        {
            var book = el.TryGetProperty("projectId", out var pEl) ? pEl.GetString() ?? "" : "";
            var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var gold = el.TryGetProperty("gold", out var gEl) ? gEl.GetString() ?? "" : "";
            var visual = el.TryGetProperty("visual", out var vEl) ? vEl.GetString() ?? "" : "";
            var isFirst = el.TryGetProperty("is_first_silent_in_scene", out var fEl) &&
                          (fEl.ValueKind == JsonValueKind.True ||
                           (fEl.ValueKind == JsonValueKind.String &&
                            bool.TryParse(fEl.GetString(), out var fb) && fb));
            if (id.Length == 0 || gold.Length == 0 || visual.Length == 0) continue;
            var nGold = SilentBeatActionClassifier.NormalizeClass(gold) ?? gold.Trim().ToLowerInvariant();
            // Composite key so s1_b1 across books doesn't collide in the chat batch
            var key = $"{book}::{id}";
            samples.Add((key, book, id, visual, isFirst, nGold));
        }

        // Batch chat (product uses ~30)
        const int batchSize = 30;
        var aiMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sw = Stopwatch.StartNew();
        var chatCalls = 0;
        for (var offset = 0; offset < samples.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = samples.Skip(offset).Take(batchSize).ToList();
            var payload = chunk.Select(s => new Dictionary<string, object?>
            {
                ["id"] = s.Key,
                ["visual_event"] = Trunc(s.Visual, 280),
                ["is_first_silent_in_scene"] = s.IsFirst,
            }).ToList();
            var raw = await chat.CompleteAsync(
                model, temperature, prompt.Text,
                "Label each silent beat for duration budgeting. Return JSON only.\n\n" +
                JsonSerializer.Serialize(new { beats = payload }),
                ct);
            chatCalls++;
            foreach (var kv in SilentBeatActionClassifier.ParseLabels(raw))
                aiMap[kv.Key] = kv.Value;
        }
        sw.Stop();

        var sampleScores = new List<SampleScore>();
        int baseOk = 0, aiOk = 0, hits = 0;
        foreach (var s in samples)
        {
            var h = FrozenBaselineSilentActionClass(s.Visual, s.IsFirst);
            aiMap.TryGetValue(s.Key, out var ac);
            if (aiMap.ContainsKey(s.Key)) hits++;
            ac ??= "";
            var b = string.Equals(h, s.Gold, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
            var a = string.Equals(ac, s.Gold, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
            baseOk += (int)b;
            aiOk += (int)a;
            sampleScores.Add(new SampleScore
            {
                Id = s.Key,
                Visual = Trunc($"[{s.Project}] {s.Visual}", 200),
                GoldLabel = s.Gold,
                BaselineLabel = h,
                AiLabel = ac,
                BaselineScore = b,
                AiScore = a,
            });
        }

        var n = samples.Count;
        var baseMean = n == 0 ? 0 : (double)baseOk / n;
        var aiMean = n == 0 ? 0 : (double)aiOk / n;
        return new TaskResult
        {
            Task = "silent_beat_action",
            ProjectId = projectId is "_all_books" or "" ? "_all_books" : projectId,
            Model = model,
            PromptId = prompt.Id,
            PromptLabel = prompt.Label,
            PromptHash = prompt.Hash,
            Temperature = temperature,
            CuratedGold = curated,
            SampleCount = n,
            Metric = "accuracy",
            BaselineScore = baseMean,
            AiScore = aiMean,
            Winner = Winner(baseMean, aiMean),
            LatencyMs = sw.ElapsedMilliseconds,
            AiParseHits = hits,
            Note = $"{prompt.Notes}; chat_calls={chatCalls}; books=multi",
            Samples = sampleScores,
        };
    }

    /// <summary>
    /// Frozen fair baseline (BeatLabelEval BaselineInferActionClass): first silent → establishing.
    /// Do not use eval-tuned InferActionClass here.
    /// </summary>
    public static string FrozenBaselineSilentActionClass(string actionText, bool isFirstBeatInScene)
    {
        var t = (actionText ?? "").Trim();
        if (t.Length == 0)
            return isFirstBeatInScene ? "establishing" : "hold";

        var lower = t.ToLowerInvariant();
        var words = Regex.Split(lower, @"\s+").Count(w => w.Length > 0);

        if (Regex.IsMatch(lower,
                @"\b(chase|races?|sprints?|explodes?|crashes?|fights?|attacks?|leaps?|bounds?|lunges?|slams?)\b"))
            return "big_action";

        if (isFirstBeatInScene)
            return "establishing";

        if (words <= 24 &&
            Regex.IsMatch(lower,
                @"\b(smile|smiles|smiling|nods?|turns?|looks?|gazes?|freezes?|waits?|steadies|thin smile|hands on|sits still|leans?|pauses?|watches?|listens?)\b"))
            return "hold";

        if (words <= 8)
            return "hold";

        return "action";
    }

    public static async Task<TaskResult> RunExtendCutAsync(
        BenchPaths paths,
        string projectId,
        string model,
        double temperature,
        PromptBundle prompt,
        ChatRunner chat,
        CancellationToken ct = default)
    {
        var goldPath = paths.GoldFile(projectId, "extend_cut");
        if (!File.Exists(goldPath))
            throw new FileNotFoundException($"Missing gold: {goldPath}");

        using var goldDoc = JsonDocument.Parse(await File.ReadAllTextAsync(goldPath, ct));
        var root = goldDoc.RootElement;
        var curated = root.TryGetProperty("curated", out var cEl) && cEl.GetBoolean();
        var samples = new List<(string Id, string Visual, string Prev, string ActionClass, bool SameLoc, bool IsFirst, string Gold)>();
        foreach (var el in root.GetProperty("labels").EnumerateArray())
        {
            var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            if (id.Length == 0) continue;
            var visual = el.TryGetProperty("visual", out var vEl) ? vEl.GetString() ?? "" : "";
            var prev = el.TryGetProperty("prev", out var pEl) ? pEl.GetString() ?? "" : "";
            var ac = el.TryGetProperty("action_class", out var aEl) ? aEl.GetString() ?? "" : "";
            var same = el.TryGetProperty("same_location", out var sEl) &&
                       (sEl.ValueKind == JsonValueKind.True ||
                        (sEl.ValueKind == JsonValueKind.String && bool.TryParse(sEl.GetString(), out var sb) && sb));
            var first = el.TryGetProperty("is_first", out var fEl) &&
                        (fEl.ValueKind == JsonValueKind.True ||
                         (fEl.ValueKind == JsonValueKind.String && bool.TryParse(fEl.GetString(), out var fb) && fb));
            var gold = el.TryGetProperty("gold", out var gEl) ? gEl.GetString() ?? "" : "";
            gold = gold.Trim().ToLowerInvariant().Replace(' ', '_');
            if (gold is "hardcut" or "cut") gold = "hard_cut";
            if (gold is not ("hard_cut" or "extend")) continue;
            samples.Add((id, visual, prev, ac, same, first, gold));
        }

        var payload = samples.Select(s =>
        {
            var h = ExtendCutClassifier.BaselineHardCut(s.Visual, s.ActionClass, s.SameLoc, s.IsFirst)
                ? "hard_cut" : "extend";
            return new Dictionary<string, object?>
            {
                ["id"] = s.Id,
                ["prev_visual"] = Trunc(s.Prev, 160),
                ["visual_event"] = Trunc(s.Visual, 200),
                ["same_location"] = s.SameLoc,
                ["action_class"] = s.ActionClass,
                ["heuristic"] = h,
            };
        }).ToList();

        var sw = Stopwatch.StartNew();
        var raw = await chat.CompleteAsync(
            model, temperature, prompt.Text,
            "Label hard_cut vs extend for video continuity. JSON only.\n" +
            JsonSerializer.Serialize(new { beats = payload }),
            ct);
        sw.Stop();

        var aiMap = ExtendCutClassifier.ParseLabels(raw);
        var sampleScores = new List<SampleScore>();
        int baseOk = 0, aiOk = 0, hits = 0;
        foreach (var s in samples)
        {
            var h = ExtendCutClassifier.BaselineHardCut(s.Visual, s.ActionClass, s.SameLoc, s.IsFirst)
                ? "hard_cut" : "extend";
            aiMap.TryGetValue(s.Id, out var ac);
            if (aiMap.ContainsKey(s.Id)) hits++;
            ac ??= "";
            var b = string.Equals(h, s.Gold, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
            var a = string.Equals(ac, s.Gold, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
            baseOk += (int)b;
            aiOk += (int)a;
            sampleScores.Add(new SampleScore
            {
                Id = s.Id,
                Visual = Trunc(s.Visual, 200),
                GoldLabel = s.Gold,
                BaselineLabel = h,
                AiLabel = ac,
                BaselineScore = b,
                AiScore = a,
            });
        }

        var n = samples.Count;
        var baseMean = n == 0 ? 0 : (double)baseOk / n;
        var aiMean = n == 0 ? 0 : (double)aiOk / n;
        return new TaskResult
        {
            Task = "extend_cut",
            ProjectId = projectId,
            Model = model,
            PromptId = prompt.Id,
            PromptLabel = prompt.Label,
            PromptHash = prompt.Hash,
            Temperature = temperature,
            CuratedGold = curated,
            SampleCount = n,
            Metric = "accuracy",
            BaselineScore = baseMean,
            AiScore = aiMean,
            Winner = Winner(baseMean, aiMean),
            LatencyMs = sw.ElapsedMilliseconds,
            AiParseHits = hits,
            Note = prompt.Notes,
            Samples = sampleScores,
        };
    }

    public static async Task<TaskResult> RunPlateRankAsync(
        BenchPaths paths,
        string projectId,
        string model,
        double temperature,
        PromptBundle prompt,
        ChatRunner chat,
        CancellationToken ct = default)
    {
        var goldPath = paths.GoldFile(projectId, "plate_rank");
        if (!File.Exists(goldPath))
            throw new FileNotFoundException($"Missing gold: {goldPath}");

        using var goldDoc = JsonDocument.Parse(await File.ReadAllTextAsync(goldPath, ct));
        var root = goldDoc.RootElement;
        var curated = root.TryGetProperty("curated", out var cEl) && cEl.GetBoolean();

        var candidates = new List<string>();
        if (root.TryGetProperty("candidates", out var candEl) && candEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in candEl.EnumerateArray())
                if (c.GetString() is { Length: > 0 } s)
                    candidates.Add(s);
        }
        if (candidates.Count == 0)
        {
            // Fall back to project book_images page_*.png
            var imgDir = Directory.GetDirectories(Path.Combine(paths.RepoRoot, "projects"), "book_images", SearchOption.AllDirectories)
                .FirstOrDefault(d => d.Contains(projectId, StringComparison.OrdinalIgnoreCase));
            if (imgDir is not null)
                candidates = Directory.GetFiles(imgDir, "page_*.png").Select(Path.GetFileName).Where(n => n is not null).Cast<string>()
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        if (candidates.Count == 0)
            throw new InvalidOperationException($"No plate candidates for {projectId}");

        var samples = new List<(string Key, string Desc, List<string> Gold)>();
        foreach (var el in root.GetProperty("labels").EnumerateArray())
        {
            var key = el.TryGetProperty("character_key", out var kEl) ? kEl.GetString() ?? "" : "";
            if (key.Length == 0) continue;
            var desc = el.TryGetProperty("description", out var dEl) ? dEl.GetString() ?? "" : "";
            var lockTxt = el.TryGetProperty("visual_lock", out var vEl) ? vEl.GetString() ?? "" : "";
            if (!string.IsNullOrWhiteSpace(lockTxt))
                desc = (desc + " " + lockTxt).Trim();
            var gold = new List<string>();
            if (el.TryGetProperty("gold_files", out var gf) && gf.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in gf.EnumerateArray())
                    if (g.GetString() is { Length: > 0 } gs)
                        gold.Add(gs);
            }
            if (gold.Count == 0) continue;
            samples.Add((key, desc, gold));
        }

        // Filename heuristics were no better than chance on Buster gold — report fixed chance baseline.
        const double chanceBaseline = 0.5;

        double aiSum = 0;
        var sampleScores = new List<SampleScore>();
        var hits = 0;
        var sw = Stopwatch.StartNew();

        // One chat call per character (small cast)
        foreach (var s in samples)
        {
            var candidateRows = candidates.Take(24).Select(n => new
            {
                name = n,
                page = TryPageNumber(n),
                likely_art = IsLikelyArtPage(n),
            }).ToList();
            var payload = new
            {
                character_key = s.Key,
                description = Trunc(s.Desc, 280),
                candidates = candidateRows,
                instruction = "Return top 3 basenames that best SHOW this character as a visible figure. Use page structure; do not put supporting cast on the cover/early pages without reason.",
            };
            var raw = await chat.CompleteAsync(
                model, temperature, prompt.Text,
                JsonSerializer.Serialize(payload),
                ct);
            var aiTop = PlateRankClassifier.ParseRank(raw, candidates);
            if (aiTop.Count > 0) hits++;
            // No heuristic fallback ranking — empty parse stays empty (scores 0 vs gold)

            var aRec = RecallAtKCapped(aiTop, s.Gold, 3);
            aiSum += aRec;
            sampleScores.Add(new SampleScore
            {
                Id = s.Key,
                Visual = Trunc(s.Desc, 160),
                GoldLabel = string.Join(", ", s.Gold),
                BaselineLabel = "chance (0.5)",
                AiLabel = string.Join(", ", aiTop.Take(3)),
                BaselineScore = chanceBaseline,
                AiScore = aRec,
            });
        }
        sw.Stop();

        var n = samples.Count;
        var baseMean = n == 0 ? 0 : chanceBaseline;
        var aiMean = n == 0 ? 0 : aiSum / n;
        return new TaskResult
        {
            Task = "plate_rank",
            ProjectId = projectId,
            Model = model,
            PromptId = prompt.Id,
            PromptLabel = prompt.Label,
            PromptHash = prompt.Hash,
            Temperature = temperature,
            CuratedGold = curated,
            SampleCount = n,
            Metric = "mean_recall_at_3_capped",
            BaselineScore = baseMean,
            AiScore = aiMean,
            Winner = Winner(baseMean, aiMean),
            LatencyMs = sw.ElapsedMilliseconds,
            AiParseHits = hits,
            Note = (prompt.Notes ?? "") + " baseline=chance/0.5 (filename heuristic removed)",
            Samples = sampleScores,
        };
    }

    /// <summary>
    /// Hits among top-K over min(K, |gold|). With many gold pages, perfect top-3 scores 1.0 not 3/|gold|.
    /// </summary>
    public static double RecallAtKCapped(IReadOnlyList<string> pred, IReadOnlyList<string> gold, int k = 3)
    {
        if (gold.Count == 0) return pred.Count == 0 ? 1.0 : 0.0;
        var p = pred.Take(k).ToList();
        var hits = gold.Count(g => p.Any(x => x.Equals(g, StringComparison.OrdinalIgnoreCase)));
        var denom = Math.Min(k, gold.Count);
        return denom == 0 ? 0 : (double)hits / denom;
    }

    static int? TryPageNumber(string name)
    {
        var m = Regex.Match(name ?? "", @"page_(\d+)", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var p)) return p;
        m = Regex.Match(name ?? "", @"_p(\d+)", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out p)) return p;
        return null;
    }

    static bool IsLikelyArtPage(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Contains("text_heavy", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("text_page", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("text-only", StringComparison.OrdinalIgnoreCase))
            return false;
        // Even page numbers in this pilot book are text spreads
        var page = TryPageNumber(name);
        if (page is int p && p % 2 == 0 && name.Contains("render", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public static string DefaultPromptId(string task) => task switch
    {
        "ambient_sfx" => "v2_grounded",
        "onscreen_cast" => "v2_grounded",
        "extend_cut" => "v2_grounded",
        "silent_beat_action" => "v2_product",
        "species_kind" => "v1_product",
        "plate_rank" => "v2_picture_book",
        _ => "v1_product",
    };

    public static string Winner(double baseline, double ai, double eps = 0.02) =>
        Math.Abs(baseline - ai) < eps ? "tie" : ai > baseline ? "AI" : "baseline";

    static string Trunc(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";
}
