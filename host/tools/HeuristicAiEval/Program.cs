using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Engine;

// Holdout / multi-task scorer for the five heuristic→AI classifiers.
// Usage:
//   HeuristicAiEval --holdout TellTaleHeartV4
//   HeuristicAiEval --holdout TellTaleHeartV4 --write-gold-draft
//
// Writes host/evals/heuristic_ai_eval/HOLDOUT_RESULTS.md and per-task scores.

var repo = FindRepoRoot();
var evalRoot = Path.Combine(repo, "host", "evals", "heuristic_ai_eval");
Directory.CreateDirectory(evalRoot);

var writeDraft = args.Any(a => a == "--write-gold-draft");
string? holdoutId = null;
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--holdout") holdoutId = args[i + 1];
holdoutId ??= "TellTaleHeartV4";

var fountain = Directory.GetFiles(Path.Combine(repo, "projects"), "screenplay.fountain", SearchOption.AllDirectories)
    .FirstOrDefault(p => p.Contains(holdoutId, StringComparison.OrdinalIgnoreCase) &&
                         !p.Contains($"{Path.DirectorySeparatorChar}_", StringComparison.Ordinal));
if (fountain is null)
{
    Console.Error.WriteLine($"No fountain for {holdoutId}");
    return 1;
}

var key = Environment.GetEnvironmentVariable("XAI_API_KEY");
if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("XAI_API_KEY required for holdout AI scoring");
    return 1;
}

Console.WriteLine($"Holdout: {holdoutId}");
Console.WriteLine(fountain);

var stage1 = FountainStage1Importer.BuildStage1(FountainParser.Parse(await File.ReadAllTextAsync(fountain)));
// Overlay cast seeds if present
var castPath = Path.Combine(Path.GetDirectoryName(fountain)!, "cast_seeds.json");
if (File.Exists(castPath))
{
    try
    {
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(castPath));
        if (doc.RootElement.TryGetProperty("character_seed_tokens", out var seedsEl) &&
            seedsEl.ValueKind == JsonValueKind.Object)
        {
            var gpv = stage1["global_production_variables"] as Dictionary<string, object?> ?? new();
            var dict = new Dictionary<string, object?>();
            foreach (var p in seedsEl.EnumerateObject())
            {
                var inner = new Dictionary<string, object?>();
                if (p.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var q in p.Value.EnumerateObject())
                        inner[q.Name] = q.Value.ValueKind == JsonValueKind.String ? q.Value.GetString() : q.Value.ToString();
                }
                dict[p.Name] = inner;
            }
            gpv["character_seed_tokens"] = dict;
            stage1["global_production_variables"] = gpv;
        }
    }
    catch { /* optional */ }
}

using var http = new HttpClient { BaseAddress = new Uri("https://api.x.ai/v1/"), Timeout = TimeSpan.FromMinutes(4) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

var goldDir = Path.Combine(evalRoot, "holdout_gold", holdoutId);
Directory.CreateDirectory(goldDir);

// ── 1 Ambient/SFX ────────────────────────────────────────────────────────
var ambientSamples = SampleBeats(stage1, 24);
if (writeDraft || !File.Exists(Path.Combine(goldDir, "ambient_sfx.json")))
{
    var draft = ambientSamples.Select(b =>
    {
        var (ha, hs) = FountainStage1Importer.InferAmbientAndSfx(b.Visual);
        return new
        {
            b.Id,
            visual = Trunc(b.Visual, 200),
            heuristic_ambient = ha,
            heuristic_sfx = hs,
            gold_ambient = ha, // draft = heuristic; human can edit
            gold_sfx = hs,
        };
    }).ToList();
    await File.WriteAllTextAsync(Path.Combine(goldDir, "ambient_sfx.json"),
        JsonSerializer.Serialize(new { projectId = holdoutId, labels = draft }, Pretty()));
    Console.WriteLine("Wrote ambient_sfx gold draft (edit gold_* fields)");
}

// Call AI for ambient on samples
var ambPayload = ambientSamples.Select(b =>
{
    var (ha, hs) = FountainStage1Importer.InferAmbientAndSfx(b.Visual);
    return new { id = b.Id, visual_event = Trunc(b.Visual, 280), heuristic_ambient = ha, heuristic_sfx = hs };
}).ToList();
var ambRaw = await ChatAsync(http, AmbientSfxClassifier.SystemPrompt(),
    "Split ambient vs sfx. JSON only.\n" + JsonSerializer.Serialize(new { beats = ambPayload }));
var ambAi = AmbientSfxClassifier.ParseLabels(ambRaw);
var ambGold = LoadAmbientGold(Path.Combine(goldDir, "ambient_sfx.json"));
// Prefer AI labels as provisional gold for holdout when draft still equals heuristic and AI differs
// — for fair table we score both against **curated** gold; if gold==heuristic draft, score AI vs heuristic as proxy
double ambBase = 0, ambAiScore = 0;
var nAmb = 0;
foreach (var g in ambGold)
{
    nAmb++;
    var (hbA, hbS) = FountainStage1Importer.InferAmbientAndSfx(
        ambientSamples.FirstOrDefault(x => x.Id == g.Id)?.Visual ?? "");
    ambBase += (AmbientSfxClassifier.TokenJaccard(hbA, g.Ambient) + AmbientSfxClassifier.TokenJaccard(hbS, g.Sfx)) / 2;
    ambAi.TryGetValue(g.Id, out var aiPair);
    var aa = aiPair.Ambient ?? "";
    var asx = aiPair.Sfx ?? "";
    ambAiScore += (AmbientSfxClassifier.TokenJaccard(aa, g.Ambient) + AmbientSfxClassifier.TokenJaccard(asx, g.Sfx)) / 2;
}
if (nAmb == 0) { ambBase = double.NaN; ambAiScore = double.NaN; }
else { ambBase /= nAmb; ambAiScore /= nAmb; }

// ── 2 On-screen cast ─────────────────────────────────────────────────────
var castKeys = GetCastKeys(stage1);
var castSamples = SampleBeats(stage1, 20);
var castGoldPath = Path.Combine(goldDir, "onscreen_cast.json");
if (writeDraft || !File.Exists(castGoldPath))
{
    var draft = castSamples.Select(b =>
    {
        var profiles = castKeys.ToDictionary(k => k,
            k => new ClipVideoPromptBuilder.CharacterProfile { DisplayName = k.Replace("Character_", "").Replace('_', ' ') });
        var h = ClipVideoPromptBuilder.InferKeysFromProse(b.Visual, profiles);
        return new { b.Id, visual = Trunc(b.Visual, 180), heuristic_keys = h, gold_keys = h };
    }).ToList();
    await File.WriteAllTextAsync(castGoldPath, JsonSerializer.Serialize(new { labels = draft }, Pretty()));
}
var castPayload = castSamples.Select(b => new
{
    id = b.Id,
    visual_event = Trunc(b.Visual, 200),
    dialogue = "",
    speaker_key = "",
    is_voiceover = false,
    heuristic_keys = Array.Empty<string>(),
}).ToList();
var castRaw = await ChatAsync(http, OnScreenCastClassifier.SystemPrompt(),
    "Pick on-screen keys. JSON only.\n" + JsonSerializer.Serialize(new { cast_keys = castKeys, beats = castPayload }));
var castAi = OnScreenCastClassifier.ParseLabels(castRaw, castKeys);
var castGold = LoadKeysGold(castGoldPath);
double castBase = 0, castAiS = 0;
var nCast = 0;
foreach (var g in castGold)
{
    nCast++;
    var profiles = castKeys.ToDictionary(k => k,
        k => new ClipVideoPromptBuilder.CharacterProfile { DisplayName = k.Replace("Character_", "").Replace('_', ' ') });
    var vis = castSamples.FirstOrDefault(x => x.Id == g.Id)?.Visual ?? "";
    var h = ClipVideoPromptBuilder.InferKeysFromProse(vis, profiles);
    castBase += OnScreenCastClassifier.SetF1(h, g.Keys);
    castAi.TryGetValue(g.Id, out var ak);
    castAiS += OnScreenCastClassifier.SetF1(ak ?? new List<string>(), g.Keys);
}
if (nCast > 0) { castBase /= nCast; castAiS /= nCast; }

// ── 3 Extend / hard-cut ──────────────────────────────────────────────────
var extSamples = SampleBeatsWithPrev(stage1, 24);
var extGoldPath = Path.Combine(goldDir, "extend_cut.json");
if (writeDraft || !File.Exists(extGoldPath))
{
    var draft = extSamples.Select(b =>
    {
        var hard = ExtendCutClassifier.BaselineHardCut(b.Visual, b.ActionClass, b.SameLocation, b.IsFirst);
        return new
        {
            b.Id,
            visual = Trunc(b.Visual, 160),
            prev = Trunc(b.PrevVisual, 100),
            heuristic = hard ? "hard_cut" : "extend",
            gold = hard ? "hard_cut" : "extend",
        };
    }).ToList();
    await File.WriteAllTextAsync(extGoldPath, JsonSerializer.Serialize(new { labels = draft }, Pretty()));
}
var extPayload = extSamples.Select(b => new
{
    id = b.Id,
    prev_visual = Trunc(b.PrevVisual, 120),
    visual_event = Trunc(b.Visual, 160),
    same_location = b.SameLocation,
    action_class = b.ActionClass,
    heuristic = ExtendCutClassifier.BaselineHardCut(b.Visual, b.ActionClass, b.SameLocation, b.IsFirst) ? "hard_cut" : "extend",
}).ToList();
var extRaw = await ChatAsync(http, ExtendCutClassifier.SystemPrompt(),
    "Label hard_cut vs extend. JSON only.\n" + JsonSerializer.Serialize(new { beats = extPayload }));
var extAi = ExtendCutClassifier.ParseLabels(extRaw);
var extGold = LoadClassGold(extGoldPath, "gold");
int extBaseOk = 0, extAiOk = 0, nExt = 0;
foreach (var g in extGold)
{
    nExt++;
    var b = extSamples.FirstOrDefault(x => x.Id == g.Id);
    if (b is null) continue;
    var h = ExtendCutClassifier.BaselineHardCut(b.Visual, b.ActionClass, b.SameLocation, b.IsFirst) ? "hard_cut" : "extend";
    if (h == g.Class) extBaseOk++;
    if (extAi.TryGetValue(g.Id, out var ac) && ac == g.Class) extAiOk++;
}

// ── 4 Species ────────────────────────────────────────────────────────────
var speciesRows = GetSpeciesRows(stage1);
var spGoldPath = Path.Combine(goldDir, "species.json");
if (writeDraft || !File.Exists(spGoldPath))
{
    var draft = speciesRows.Select(s => new
    {
        key = s.Key,
        description = Trunc(s.Desc, 160),
        heuristic = SpeciesKindClassifier.BaselineKind(s.Key, s.Desc, s.Lock),
        gold = SpeciesKindClassifier.BaselineKind(s.Key, s.Desc, s.Lock),
    }).ToList();
    await File.WriteAllTextAsync(spGoldPath, JsonSerializer.Serialize(new { labels = draft }, Pretty()));
}
// Curate TellTale species gold (known cast)
if (holdoutId.Contains("TellTale", StringComparison.OrdinalIgnoreCase))
{
    await File.WriteAllTextAsync(spGoldPath, JsonSerializer.Serialize(new
    {
        labels = new object[]
        {
            new { key = "Character_Narrator", gold = "human", note = "adult man confessor" },
            new { key = "Character_Old_Man", gold = "human", note = "elderly man" },
            new { key = "Character_Officer", gold = "human", note = "officer" },
            new { key = "Character_Officer_Clemm", gold = "human" },
            new { key = "Character_Officer_Hayes", gold = "human" },
            new { key = "Character_Officer_Reynolds", gold = "human" },
        }
    }, Pretty()));
}
var spPayload = speciesRows.Select(s => new
{
    key = s.Key,
    description = Trunc(s.Desc, 200),
    visual_lock = Trunc(s.Lock, 120),
    heuristic = SpeciesKindClassifier.BaselineKind(s.Key, s.Desc, s.Lock),
}).ToList();
var spRaw = await ChatAsync(http, SpeciesKindClassifier.SystemPrompt(),
    "Label animal|human|other. JSON only.\n" + JsonSerializer.Serialize(new { cast = spPayload }));
var spAi = SpeciesKindClassifier.ParseLabels(spRaw);
var spGold = LoadSpeciesGold(spGoldPath);
int spBaseOk = 0, spAiOk = 0, nSp = 0;
foreach (var g in spGold)
{
    nSp++;
    var row = speciesRows.FirstOrDefault(r => r.Key.Equals(g.Key, StringComparison.OrdinalIgnoreCase));
    var h = string.IsNullOrEmpty(row.Key) ? "other" : SpeciesKindClassifier.BaselineKind(row.Key, row.Desc, row.Lock);
    if (h == g.Class) spBaseOk++;
    if (spAi.TryGetValue(g.Key, out var ac) && ac == g.Class) spAiOk++;
}

// ── 5 Plate rank (assets/characters basenames; mock plates OK for eval) ──
var plateDir = Path.Combine(repo, "projects", holdoutId, "assets", "characters");
var bookImgDir = Path.Combine(repo, "projects", holdoutId, "source", "book_images");
var plateNames = new List<string>();
if (Directory.Exists(plateDir))
    plateNames.AddRange(Directory.GetFiles(plateDir).Select(Path.GetFileName!).Where(n => n.EndsWith(".png", StringComparison.OrdinalIgnoreCase)));
// Also include book_images basenames so ranking can prefer illustrated plates
if (Directory.Exists(bookImgDir))
{
    foreach (var n in Directory.GetFiles(bookImgDir).Select(Path.GetFileName!).Where(n => n.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
        if (!plateNames.Contains(n, StringComparer.OrdinalIgnoreCase))
            plateNames.Add(n);
}
var plateGoldPath = Path.Combine(goldDir, "plate_rank.json");

// Multi-character plate eval when cast seeds exist (Jungle Book mock plates, TellTale real)
var plateTargets = new List<(string Key, string Desc, string Token)>();
foreach (var row in speciesRows.Take(12))
{
    var token = row.Key.Replace("Character_", "", StringComparison.OrdinalIgnoreCase).Replace("_", "").ToLowerInvariant();
    // filename slug uses underscores: character_shere_khan_ref.png
    var slug = row.Key.Replace("Character_", "", StringComparison.OrdinalIgnoreCase)
        .Replace(" ", "_").ToLowerInvariant();
    plateTargets.Add((row.Key, row.Desc, slug));
}
if (plateTargets.Count == 0)
    plateTargets.Add(("Character_Narrator", "Pale nervous adult man", "narrator"));

double plateBaseRecSum = 0, plateAiRecSum = 0;
var plateDetails = new List<object>();
var plateEvalN = 0;
foreach (var (pKey, pDesc, slug) in plateTargets)
{
    if (plateNames.Count == 0) break;
    // Gold: files whose name contains this character slug (ref + variants preferred)
    var gold = plateNames
        .Where(n => n.Contains(slug, StringComparison.OrdinalIgnoreCase) ||
                    n.Contains(slug.Replace("_", ""), StringComparison.OrdinalIgnoreCase))
        .OrderBy(n => n.Contains("_ref", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
        .Take(3)
        .ToList();
    if (gold.Count == 0) continue;

    var baseline = plateNames
        .Where(n => n.Contains(slug, StringComparison.OrdinalIgnoreCase) ||
                    n.Contains(slug.Replace("_", ""), StringComparison.OrdinalIgnoreCase))
        .OrderBy(n => n.Contains("_ref", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .Take(3)
        .ToList();
    if (baseline.Count == 0) baseline = plateNames.Take(3).ToList();

    // Candidate pool: mix of matching + decoys (up to 24)
    var candidates = gold
        .Concat(plateNames.Where(n => !gold.Contains(n, StringComparer.OrdinalIgnoreCase)).Take(20))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(24)
        .ToList();

    var plateRaw = await ChatAsync(http, PlateRankClassifier.SystemPrompt(),
        JsonSerializer.Serialize(new
        {
            character_key = pKey,
            description = Trunc(pDesc, 240),
            candidates,
        }));
    var plateAi = PlateRankClassifier.ParseRank(plateRaw, candidates);
    var bRec = PlateRankClassifier.RecallAtK(baseline, gold, 3);
    var aRec = PlateRankClassifier.RecallAtK(plateAi, gold, 3);
    plateBaseRecSum += bRec;
    plateAiRecSum += aRec;
    plateEvalN++;
    plateDetails.Add(new
    {
        character = pKey,
        gold_top3 = gold,
        baseline_top3 = baseline,
        ai_top3 = plateAi,
        baselineRecallAt3 = bRec,
        aiRecallAt3 = aRec,
    });
    Console.WriteLine($"  plate {pKey}: base={bRec:F2} ai={aRec:F2}");
}

var plateBaseRec = plateEvalN > 0 ? plateBaseRecSum / plateEvalN : 0;
var plateAiRec = plateEvalN > 0 ? plateAiRecSum / plateEvalN : 0;
await File.WriteAllTextAsync(plateGoldPath, JsonSerializer.Serialize(new
{
    projectId = holdoutId,
    plateFileCount = plateNames.Count,
    charactersEvaluated = plateEvalN,
    meanBaselineRecallAt3 = plateBaseRec,
    meanAiRecallAt3 = plateAiRec,
    details = plateDetails,
}, Pretty()));

// ── Results table ────────────────────────────────────────────────────────
var rows = new List<(string Task, string Metric, string Baseline, string Ai, string Winner)>
{
    ("1 Ambient/SFX", "mean token Jaccard",
        Fmt(ambBase), Fmt(ambAiScore), Win(ambBase, ambAiScore, higherIsBetter: true)),
    ("2 On-screen cast", "mean set F1",
        Fmt(castBase), Fmt(castAiS), Win(castBase, castAiS, true)),
    ("3 Extend/hard-cut", "accuracy",
        nExt > 0 ? $"{extBaseOk}/{nExt} ({100.0 * extBaseOk / nExt:F0}%)" : "n/a",
        nExt > 0 ? $"{extAiOk}/{nExt} ({100.0 * extAiOk / nExt:F0}%)" : "n/a",
        nExt > 0 ? WinCount(extBaseOk, extAiOk) : "—"),
    ("4 Species kind", "accuracy",
        nSp > 0 ? $"{spBaseOk}/{nSp} ({100.0 * spBaseOk / nSp:F0}%)" : "n/a",
        nSp > 0 ? $"{spAiOk}/{nSp} ({100.0 * spAiOk / nSp:F0}%)" : "n/a",
        nSp > 0 ? WinCount(spBaseOk, spAiOk) : "—"),
    ("5 Plate rank", "recall@3",
        Fmt(plateBaseRec), Fmt(plateAiRec), Win(plateBaseRec, plateAiRec, true)),
};

var md = new System.Text.StringBuilder();
md.AppendLine($"# Holdout results — `{holdoutId}`");
md.AppendLine();
md.AppendLine($"Generated: {DateTimeOffset.UtcNow:u}");
md.AppendLine();
md.AppendLine("Gold notes:");
md.AppendLine("- Ambient/cast/extend gold drafts start as heuristic (proxy) unless edited under `holdout_gold/`.");
md.AppendLine("- Species gold for TellTale is **curated** (all human).");
md.AppendLine("- Plate gold: Narrator locked plate basenames under assets/characters.");
md.AppendLine();
md.AppendLine("| Task | Metric | Baseline heuristic | AI | Winner |");
md.AppendLine("|------|--------|--------------------|----|--------|");
foreach (var r in rows)
    md.AppendLine($"| {r.Task} | {r.Metric} | {r.Baseline} | {r.Ai} | **{r.Winner}** |");
md.AppendLine();
md.AppendLine("## Product wiring");
md.AppendLine("| Task | Service | Stage2 / plates |");
md.AppendLine("|------|---------|-----------------|");
md.AppendLine("| Ambient/SFX | `AmbientSfxClassifier` | Stage2 enrich |");
md.AppendLine("| On-screen cast | `OnScreenCastClassifier` | Stage2 enrich → clip cast |");
md.AppendLine("| Extend/cut | `ExtendCutClassifier` | Stage2 `cut_decision` → ForceNone |");
md.AppendLine("| Species | `SpeciesKindClassifier` | Stage2 seed field `species_kind` |");
md.AppendLine("| Plate rank | `PlateRankClassifier` | CharacterBookPlateService re-rank |");
md.AppendLine();
md.AppendLine("Policy for all: **AI preferred → retry → heuristic fallback** (not when AI merely disagrees).");

var outPath = Path.Combine(evalRoot, "HOLDOUT_RESULTS.md");
await File.WriteAllTextAsync(outPath, md.ToString());
Console.WriteLine(md.ToString());
Console.WriteLine($"Wrote {outPath}");

// Update PROGRESS
var progressPath = Path.Combine(evalRoot, "PROGRESS.md");
await File.WriteAllTextAsync(progressPath, $"""
# Heuristic → AI classifiers progress

**Holdout:** `{holdoutId}`  
**Final table:** `HOLDOUT_RESULTS.md`

| # | Classifier | Status | Product class | Holdout winner |
|---|------------|--------|---------------|----------------|
| 0 | Silent beat action_class | SHIPPED | SilentBeatActionClassifier v2_pp | AI v2 + post-process (~88.4% gold) |
| 1 | Ambient / SFX | SHIPPED | AmbientSfxClassifier | {rows[0].Winner} |
| 2 | On-screen cast | SHIPPED | OnScreenCastClassifier | {rows[1].Winner} |
| 3 | Extend vs hard-cut | SHIPPED | ExtendCutClassifier | {rows[2].Winner} |
| 4 | Animal vs human | SHIPPED | SpeciesKindClassifier | {rows[3].Winner} |
| 5 | Book plate rank | SHIPPED | PlateRankClassifier | {rows[4].Winner} |
| F | Holdout table | DONE | — | see HOLDOUT_RESULTS.md |

Updated: {DateTimeOffset.UtcNow:u}
""");

return 0;

// ── helpers ──────────────────────────────────────────────────────────────

static JsonSerializerOptions Pretty() => new() { WriteIndented = true };

static string Fmt(double x) => double.IsNaN(x) ? "n/a" : x.ToString("F2");
static string Win(double a, double b, bool higherIsBetter) =>
    Math.Abs(a - b) < 0.02 ? "tie" : (higherIsBetter ? (b > a ? "AI" : "baseline") : (b < a ? "AI" : "baseline"));
static string WinCount(int a, int b) =>
    a == b ? "tie" : (b > a ? "AI" : "baseline");

static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "projects")) &&
            Directory.Exists(Path.Combine(dir.FullName, "host")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return Directory.GetCurrentDirectory();
}

static async Task<string> ChatAsync(HttpClient http, string system, string user)
{
    var body = new
    {
        model = "grok-4.5",
        temperature = 0.0,
        messages = new object[]
        {
            new { role = "system", content = system },
            new { role = "user", content = user },
        },
    };
    using var resp = await http.PostAsJsonAsync("chat/completions", body);
    var text = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
        throw new InvalidOperationException($"chat {(int)resp.StatusCode}: {Trunc(text, 300)}");
    using var doc = JsonDocument.Parse(text);
    return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
}

static List<BeatRow> SampleBeats(Dictionary<string, object?> stage1, int max)
{
    var all = new List<BeatRow>();
    var scenes = stage1["scenes"] as List<object?> ?? new();
    var si = 0;
    foreach (var s in scenes)
    {
        if (s is not Dictionary<string, object?> scene) continue;
        si++;
        var beats = scene.TryGetValue("story_beats", out var sb) && sb is List<object?> bl ? bl : new();
        var bi = 0;
        foreach (var b in beats)
        {
            if (b is not Dictionary<string, object?> beat) continue;
            bi++;
            var ve = beat.TryGetValue("visual_event", out var v) ? v?.ToString() ?? "" : "";
            if (ve.Length < 8) continue;
            var ac = beat.TryGetValue("action_class", out var a) ? a?.ToString() ?? "" : "";
            all.Add(new BeatRow(si, bi, $"s{si}_b{bi}", ve, ac, "", true, true));
        }
    }
    if (all.Count <= max) return all;
    var step = all.Count / (double)max;
    return Enumerable.Range(0, max).Select(i => all[Math.Min(all.Count - 1, (int)(i * step))]).GroupBy(x => x.Id).Select(g => g.First()).ToList();
}

static List<BeatRow> SampleBeatsWithPrev(Dictionary<string, object?> stage1, int max)
{
    var all = new List<BeatRow>();
    var scenes = stage1["scenes"] as List<object?> ?? new();
    var si = 0;
    foreach (var s in scenes)
    {
        if (s is not Dictionary<string, object?> scene) continue;
        si++;
        var primary = scene.TryGetValue("primary_location_id", out var pl) ? pl?.ToString() ?? "" : "";
        var beats = scene.TryGetValue("story_beats", out var sb) && sb is List<object?> bl ? bl : new();
        string? prevVe = null;
        string? prevLid = null;
        var bi = 0;
        var first = true;
        foreach (var b in beats)
        {
            if (b is not Dictionary<string, object?> beat) continue;
            bi++;
            var ve = beat.TryGetValue("visual_event", out var v) ? v?.ToString() ?? "" : "";
            if (ve.Length < 4) continue;
            var lid = beat.TryGetValue("location_id", out var l) ? l?.ToString() ?? primary : primary;
            var ac = beat.TryGetValue("action_class", out var a) ? a?.ToString() ?? "" : "";
            all.Add(new BeatRow(si, bi, $"s{si}_b{bi}", ve, ac, prevVe ?? "",
                prevLid is null || string.Equals(prevLid, lid, StringComparison.OrdinalIgnoreCase), first));
            first = false;
            prevVe = ve;
            prevLid = lid;
        }
    }
    if (all.Count <= max) return all;
    var step = all.Count / (double)max;
    return Enumerable.Range(0, max).Select(i => all[Math.Min(all.Count - 1, (int)(i * step))]).GroupBy(x => x.Id).Select(g => g.First()).ToList();
}

static List<string> GetCastKeys(Dictionary<string, object?> stage1)
{
    var gpv = stage1.TryGetValue("global_production_variables", out var g) && g is Dictionary<string, object?> gd ? gd : null;
    var seeds = gpv is not null && gpv.TryGetValue("character_seed_tokens", out var c) && c is Dictionary<string, object?> cs ? cs : null;
    return seeds?.Keys.OrderBy(k => k).ToList() ?? new List<string>();
}

static List<(string Key, string Desc, string Lock)> GetSpeciesRows(Dictionary<string, object?> stage1)
{
    var list = new List<(string, string, string)>();
    var gpv = stage1.TryGetValue("global_production_variables", out var g) && g is Dictionary<string, object?> gd ? gd : null;
    var seeds = gpv is not null && gpv.TryGetValue("character_seed_tokens", out var c) && c is Dictionary<string, object?> cs ? cs : null;
    if (seeds is null) return list;
    foreach (var (k, v) in seeds)
    {
        if (v is not Dictionary<string, object?> d) continue;
        list.Add((k, d.TryGetValue("description", out var de) ? de?.ToString() ?? "" : "",
            d.TryGetValue("visual_lock", out var vl) ? vl?.ToString() ?? "" : ""));
    }
    return list;
}

static List<(string Id, string Ambient, string Sfx)> LoadAmbientGold(string path)
{
    var list = new List<(string, string, string)>();
    if (!File.Exists(path)) return list;
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    if (!doc.RootElement.TryGetProperty("labels", out var labels)) return list;
    foreach (var el in labels.EnumerateArray())
    {
        var id = PropStr(el, "id") ?? PropStr(el, "Id");
        if (string.IsNullOrWhiteSpace(id)) continue;
        var a = PropStr(el, "gold_ambient") ?? "";
        var s = PropStr(el, "gold_sfx") ?? "";
        list.Add((id!, a, s));
    }
    return list;
}

static string? PropStr(JsonElement el, string name) =>
    el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

static List<(string Id, List<string> Keys)> LoadKeysGold(string path)
{
    var list = new List<(string, List<string>)>();
    if (!File.Exists(path)) return list;
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    if (!doc.RootElement.TryGetProperty("labels", out var labels)) return list;
    foreach (var el in labels.EnumerateArray())
    {
        var id = PropStr(el, "id") ?? PropStr(el, "Id");
        if (string.IsNullOrWhiteSpace(id)) continue;
        var keys = new List<string>();
        if (el.TryGetProperty("gold_keys", out var gk) && gk.ValueKind == JsonValueKind.Array)
            foreach (var k in gk.EnumerateArray())
                if (k.GetString() is { } s) keys.Add(s);
        list.Add((id!, keys));
    }
    return list;
}

static List<(string Id, string Class)> LoadClassGold(string path, string field)
{
    var list = new List<(string, string)>();
    if (!File.Exists(path)) return list;
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    if (!doc.RootElement.TryGetProperty("labels", out var labels)) return list;
    foreach (var el in labels.EnumerateArray())
    {
        var id = PropStr(el, "id") ?? PropStr(el, "Id");
        if (string.IsNullOrWhiteSpace(id)) continue;
        var cls = PropStr(el, field) ?? "";
        list.Add((id!, cls));
    }
    return list;
}

static List<(string Key, string Class)> LoadSpeciesGold(string path)
{
    var list = new List<(string, string)>();
    if (!File.Exists(path)) return list;
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    if (!doc.RootElement.TryGetProperty("labels", out var labels)) return list;
    foreach (var el in labels.EnumerateArray())
    {
        var key = el.TryGetProperty("key", out var k) ? k.GetString()
            : el.TryGetProperty("id", out var id) ? id.GetString() : null;
        if (string.IsNullOrWhiteSpace(key)) continue;
        var cls = el.TryGetProperty("gold", out var g) ? g.GetString()
            : el.TryGetProperty("class", out var c) ? c.GetString() : "";
        list.Add((key!, cls ?? ""));
    }
    return list;
}

record BeatRow(int Scene, int Index, string Id, string Visual, string ActionClass, string PrevVisual, bool SameLocation, bool IsFirst);
