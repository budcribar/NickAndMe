using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Engine;

// Beat-label eval: score HEURISTIC and AI against GROUND TRUTH (not each other).
// Keep this tool + ground_truth/ + gt_score/ to compare models over time.
// See host/evals/beat_label_eval/README.md
//
// Usage:
//   BeatLabelEval --export-annotate --all
//   BeatLabelEval --score-gt --all [--ai-from v3]
//   BeatLabelEval --label-ai --all --prompt v2 --fresh
//
// Product classifier: PageToMovie.Engine.SilentBeatActionClassifier (v2_pp = v2 chat + PostProcess).

var repo = FindRepoRoot();
var evalRoot = Path.Combine(repo, "host", "evals", "beat_label_eval");
var gtDir = Path.Combine(evalRoot, "ground_truth");
var annotateDir = Path.Combine(evalRoot, "annotate");
Directory.CreateDirectory(gtDir);
Directory.CreateDirectory(annotateDir);

var exportAnnotate = args.Any(a => a is "--export-annotate");
var scoreGt = args.Any(a => a is "--score-gt");
var labelAi = args.Any(a => a is "--label-ai");
var fresh = args.Any(a => a is "--fresh" or "-f");
var allBooks = args.Any(a => a is "--all" or "-a");

var promptVer = "v2";
string? aiFrom = null;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] is "--prompt" or "-p")
        promptVer = args[i + 1].Trim().ToLowerInvariant();
    if (args[i] is "--ai-from")
        aiFrom = args[i + 1].Trim();
}

// Default mode: score against ground truth (north-star metric)
if (!exportAnnotate && !scoreGt && !labelAi)
    scoreGt = true;

if (scoreGt && string.IsNullOrWhiteSpace(aiFrom))
    aiFrom = promptVer; // prefer cached AI under same prompt folder

var key = Environment.GetEnvironmentVariable("XAI_API_KEY");
var needChat = labelAi || (scoreGt && string.IsNullOrWhiteSpace(aiFrom));
if (needChat && string.IsNullOrWhiteSpace(key) && !exportAnnotate)
{
    // score-gt can still run heuristic-only if AI cache missing
}

var fountainPaths = Directory.GetFiles(Path.Combine(repo, "projects"), "screenplay.fountain", SearchOption.AllDirectories)
    .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}_", StringComparison.Ordinal))
    .ToList();

if (fountainPaths.Count < 1)
{
    Console.Error.WriteLine("Need screenplay.fountain under projects/.");
    return 1;
}

string ResolvePath(string preferId, int fallbackIndex)
{
    // Aliases for folder names that differ from gold/eval ids
    var aliases = preferId.Equals("JungleBook", StringComparison.OrdinalIgnoreCase)
        ? new[] { "The_Jungle_Book", "JungleBook", "Jungle_Book" }
        : new[] { preferId };

    foreach (var id in aliases)
    {
        var hit = fountainPaths.FirstOrDefault(p =>
            p.Contains(Path.DirectorySeparatorChar + id + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            p.Contains(id, StringComparison.OrdinalIgnoreCase));
        if (hit is not null)
            return hit;
    }
    return fountainPaths[fallbackIndex % fountainPaths.Count];
}

var namedArgs = args
    .Where(a => !a.StartsWith('-'))
    .Where(a => a is not ("v2" or "v3" or "v4" or "v5" or "v6" or "v4h"))
    .Where(a => !string.Equals(a, promptVer, StringComparison.OrdinalIgnoreCase))
    .Where(a => !string.Equals(a, aiFrom, StringComparison.OrdinalIgnoreCase))
    .ToList();

List<string> selected;
if (allBooks || namedArgs.Count == 0)
{
    var preferred = new[]
    {
        "GiftOfTheMagi", "YellowWallpaper", "ChristmasCarol", "Dracula",
        "Frankenstein", "The_Jungle_Book", "AlicesAdventures",
    };
    selected = preferred.Select((id, i) => ResolvePath(id, i)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
else
{
    selected = new List<string>();
    for (var i = 0; i < namedArgs.Count; i++)
    {
        var p = ResolvePath(namedArgs[i], i);
        if (!selected.Contains(p, StringComparer.OrdinalIgnoreCase))
            selected.Add(p);
    }
}

Console.WriteLine($"Mode: {(exportAnnotate ? "export-annotate" : scoreGt ? "score-gt" : "label-ai")}");
Console.WriteLine($"Books: {selected.Count}  prompt={promptVer}  aiFrom={aiFrom ?? "(chat)"}  fresh={fresh}");

using var http = new HttpClient { BaseAddress = new Uri("https://api.x.ai/v1/") };
if (!string.IsNullOrWhiteSpace(key))
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
http.Timeout = TimeSpan.FromMinutes(4);

var systemPrompt = promptVer switch
{
    "v3" => SystemPromptV3(),
    "v4" => SilentBeatActionClassifier.SystemPromptV4(),
    "v5" => SilentBeatActionClassifier.SystemPromptV5(),
    "v6" => SilentBeatActionClassifier.SystemPromptV6(),
    _ => SystemPromptV2(),
};

// ---------- export annotate packs ----------
if (exportAnnotate)
{
    var totalRows = 0;
    foreach (var path in selected)
    {
        var projectId = ProjectIdFromFountain(path);
        var book = LoadBook(path);
        var sample = SampleSilent(book.Silent, max: 40);
        var existingGt = LoadGroundTruth(Path.Combine(gtDir, $"{projectId}.json"));

        var rows = sample.Select(b =>
        {
            FlatBeat? prev = null, next = null;
            var sceneBeats = book.Flat.Where(x => x.Scene == b.Scene).OrderBy(x => x.IndexInScene).ToList();
            var ix = sceneBeats.FindIndex(x => x.Id == b.Id);
            if (ix > 0) prev = sceneBeats[ix - 1];
            if (ix >= 0 && ix < sceneBeats.Count - 1) next = sceneBeats[ix + 1];

            existingGt.TryGetValue(b.Id, out var gold);
            return new
            {
                id = b.Id,
                scene = b.Scene,
                beat_index = b.IndexInScene,
                is_first_silent_in_scene = book.Silent.Where(a => a.Scene == b.Scene).OrderBy(a => a.IndexInScene).First().Id == b.Id,
                setting = Trunc(b.Setting, 120),
                visual_event = b.VisualEvent, // full text for annotators
                prev_beat = prev is null ? null : DescribeNeighbor(prev),
                next_beat = next is null ? null : DescribeNeighbor(next),
                // Fill gold.class with: establishing | hold | action | big_action
                gold = gold is null
                    ? new { @class = "", note = "" }
                    : new { @class = gold.Class, note = gold.Note },
            };
        }).ToList();

        var pack = new
        {
            projectId,
            path,
            rubric = "See ground_truth/RUBRIC.md — label for DURATION budgeting, not literary theory.",
            labels = rows,
        };
        var outPath = Path.Combine(annotateDir, $"{projectId}.json");
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(pack, Pretty()));
        Console.WriteLine($"Annotate pack: {outPath}  rows={rows.Count}  alreadyGold={rows.Count(r => !string.IsNullOrWhiteSpace(r.gold.@class))}");
        totalRows += rows.Count;
    }

    Console.WriteLine($"\nExported {totalRows} rows under {annotateDir}");
    Console.WriteLine("Edit gold.class on each row, then copy into ground_truth/{ProjectId}.json (or run a merge).");
    Console.WriteLine("Minimal ground_truth format:");
    Console.WriteLine("""  { "projectId":"GiftOfTheMagi", "labels":[ {"id":"s1_b1","class":"establishing","note":"..."} ] }""");
    return 0;
}

// ---------- label AI only (cache predictions) ----------
if (labelAi)
{
    if (string.IsNullOrWhiteSpace(key))
    {
        Console.Error.WriteLine("XAI_API_KEY not set — cannot call chat.");
        return 1;
    }

    var aiOutDir = Path.Combine(evalRoot, promptVer);
    Directory.CreateDirectory(aiOutDir);

    foreach (var path in selected)
    {
        var projectId = ProjectIdFromFountain(path);
        var bookOut = Path.Combine(aiOutDir, $"{projectId}_beat_labels.json");
        if (!fresh && File.Exists(bookOut))
        {
            Console.WriteLine($"Skip AI cache {projectId} (use --fresh)");
            continue;
        }

        var book = LoadBook(path);
        var sample = SampleSilent(book.Silent, max: 40);
        Console.WriteLine($"\n=== AI labels {projectId} ({sample.Count}) ===");
        var (labels, raw) = await FetchAiLabelsAsync(http, systemPrompt, book, sample);
        var report = new
        {
            promptVersion = promptVer,
            projectId,
            path,
            silentActionBeats = book.Silent.Count,
            sampled = sample.Count,
            labels = labels.Select(kv => new { id = kv.Key, @class = kv.Value }).ToList(),
            comparisons = sample.Select(b => new
            {
                Id = b.Id,
                b.Scene,
                heuristic = b.Heuristic,
                ai = labels.GetValueOrDefault(b.Id),
                visual = Trunc(b.VisualEvent, 160),
            }).ToList(),
            aiRawPreview = Trunc(raw, 400),
        };
        await File.WriteAllTextAsync(bookOut, JsonSerializer.Serialize(report, Pretty()));
        Console.WriteLine($"Wrote {bookOut}  ai={labels.Count}");
    }

    return 0;
}

// ---------- score-gt: heuristic + AI vs ground truth ----------
{
    var scoreDir = Path.Combine(evalRoot, "gt_score");
    Directory.CreateDirectory(scoreDir);

    var bookSummaries = new List<object>();
    // Fair product comparison = CHECKED-IN baseline heuristic (pre eval-tuning) vs AI vs gold.
    // "Tuned" heuristic was edited after looking at these same books — report it but mark contaminated.
    var baseCorrect = 0;
    var tunedCorrect = 0;
    var aCorrect = 0;
    var nScored = 0;
    var baseDurOk = 0;
    var tunedDurOk = 0;
    var aDurOk = 0;
    var baseHist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var tunedHist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var aHist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var missingGtBooks = 0;
    var totalGtAvailable = 0;

    foreach (var path in selected)
    {
        var projectId = ProjectIdFromFountain(path);
        var gtPath = Path.Combine(gtDir, $"{projectId}.json");
        var gt = LoadGroundTruth(gtPath);
        if (gt.Count == 0)
        {
            Console.WriteLine($"\n=== {projectId}: no ground truth at {gtPath} — skip ===");
            missingGtBooks++;
            continue;
        }

        var book = LoadBook(path);
        var byId = book.Flat.ToDictionary(b => b.Id, StringComparer.OrdinalIgnoreCase);

        // AI predictions: cache first
        Dictionary<string, string> aiLabels = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(aiFrom))
        {
            var cachePath = Path.Combine(evalRoot, aiFrom!, $"{projectId}_beat_labels.json");
            if (File.Exists(cachePath))
            {
                aiLabels = LoadAiLabelsFromReport(cachePath);
                Console.WriteLine($"\n=== {projectId}: GT={gt.Count}  AI cache={aiLabels.Count} from {aiFrom} ===");
            }
            else
                Console.WriteLine($"\n=== {projectId}: GT={gt.Count}  no AI cache at {cachePath} ===");
        }

        // Optionally fill missing AI via chat (only for GT ids)
        var needAi = gt.Keys.Where(id => !aiLabels.ContainsKey(id)).ToList();
        if (needAi.Count > 0 && !string.IsNullOrWhiteSpace(key) && fresh)
        {
            var sample = needAi
                .Select(id => byId.GetValueOrDefault(id))
                .Where(b => b is not null && b.IsSilent)
                .Cast<FlatBeat>()
                .ToList();
            if (sample.Count > 0)
            {
                Console.WriteLine($"  Fetching AI for {sample.Count} GT beats…");
                var (labels, _) = await FetchAiLabelsAsync(http, systemPrompt, book, sample);
                foreach (var kv in labels)
                    aiLabels[kv.Key] = kv.Value;
            }
        }

        var rows = new List<object>();
        var bb = 0; // baseline correct
        var bt = 0; // tuned correct
        var ba = 0;
        var bn = 0;
        var bbd = 0;
        var btd = 0;
        var bad = 0;
        var aiMissing = 0;

        foreach (var (id, gold) in gt.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!byId.TryGetValue(id, out var beat) || !beat.IsSilent)
            {
                Console.WriteLine($"  warn: GT id {id} not found as silent beat — skip");
                continue;
            }

            var isFirst = book.Silent.Where(a => a.Scene == beat.Scene).OrderBy(a => a.IndexInScene).First().Id == id;
            // Checked-in product rule (first silent → establishing). Fair baseline for generalization.
            var baseline = BaselineInferActionClass(beat.VisualEvent, isFirst);
            // Current working-tree InferActionClass (may be tuned on these books — do not treat as fair holdout).
            var tuned = FountainStage1Importer.InferActionClass(beat.VisualEvent, isFirst);

            aiLabels.TryGetValue(id, out var ai);
            if (string.IsNullOrWhiteSpace(ai))
            {
                aiMissing++;
                ai = null;
            }

            var baseOk = string.Equals(baseline, gold.Class, StringComparison.OrdinalIgnoreCase);
            var tunedOk = string.Equals(tuned, gold.Class, StringComparison.OrdinalIgnoreCase);
            var aOk = ai is not null && string.Equals(ai, gold.Class, StringComparison.OrdinalIgnoreCase);
            bn++;
            if (baseOk) bb++;
            if (tunedOk) bt++;
            if (aOk) ba++;

            var gSec = SecondsForClass(gold.Class);
            var baseSec = SecondsForClass(baseline);
            var tunedSec = SecondsForClass(tuned);
            var aSec = ai is null ? -1 : SecondsForClass(ai);
            if (baseSec == gSec) bbd++;
            if (tunedSec == gSec) btd++;
            if (aSec == gSec) bad++;

            if (!baseOk)
            {
                var pair = $"{baseline}→{gold.Class}";
                baseHist[pair] = baseHist.GetValueOrDefault(pair) + 1;
            }
            if (!tunedOk)
            {
                var pair = $"{tuned}→{gold.Class}";
                tunedHist[pair] = tunedHist.GetValueOrDefault(pair) + 1;
            }
            if (ai is not null && !aOk)
            {
                var pair = $"{ai}→{gold.Class}";
                aHist[pair] = aHist.GetValueOrDefault(pair) + 1;
            }

            rows.Add(new
            {
                id,
                gold = gold.Class,
                goldNote = gold.Note,
                baselineHeuristic = baseline,
                tunedHeuristic = tuned,
                ai,
                baselineCorrect = baseOk,
                tunedCorrect = tunedOk,
                aiCorrect = aOk,
                durationSecondsGold = gSec,
                durationSecondsBaseline = baseSec,
                durationSecondsTuned = tunedSec,
                durationSecondsAi = aSec < 0 ? (int?)null : aSec,
                baselineDurationOk = baseSec == gSec,
                tunedDurationOk = tunedSec == gSec,
                aiDurationOk = aSec == gSec,
                visual = Trunc(beat.VisualEvent, 200),
            });
        }

        nScored += bn;
        baseCorrect += bb;
        tunedCorrect += bt;
        aCorrect += ba;
        baseDurOk += bbd;
        tunedDurOk += btd;
        aDurOk += bad;
        totalGtAvailable += gt.Count;

        var basePct = bn > 0 ? 100.0 * bb / bn : 0;
        var tunedPct = bn > 0 ? 100.0 * bt / bn : 0;
        var aPct = bn > 0 ? 100.0 * ba / bn : 0;
        var baseDurPct = bn > 0 ? 100.0 * bbd / bn : 0;
        var tunedDurPct = bn > 0 ? 100.0 * btd / bn : 0;
        var aDurPct = bn > 0 ? 100.0 * bad / bn : 0;

        Console.WriteLine($"  baseline H (checked-in) vs GT: {bb}/{bn} ({basePct:F1}%)  durationOK={bbd}/{bn} ({baseDurPct:F1}%)");
        Console.WriteLine($"  tuned H (eval-contaminated) vs GT: {bt}/{bn} ({tunedPct:F1}%)  durationOK={btd}/{bn} ({tunedDurPct:F1}%)");
        Console.WriteLine($"  AI vs GT: {ba}/{bn} ({aPct:F1}%)  durationOK={bad}/{bn} ({aDurPct:F1}%)  aiMissing={aiMissing}");

        // Show errors where baseline or AI miss gold (fair pair)
        var shown = 0;
        foreach (var r in rows)
        {
            var jo = JsonSerializer.SerializeToElement(r);
            var bc = jo.GetProperty("baselineCorrect").GetBoolean();
            var ac = jo.GetProperty("aiCorrect").GetBoolean();
            if (bc && ac) continue;
            var aiStr = jo.GetProperty("ai").ValueKind == JsonValueKind.Null ? "-" : jo.GetProperty("ai").GetString();
            var bMark = bc ? "ok" : "miss";
            var aMark = ac ? "ok" : "miss";
            Console.WriteLine($"  [{jo.GetProperty("id").GetString()}] gold={jo.GetProperty("gold").GetString()}  baseH={jo.GetProperty("baselineHeuristic").GetString()}({bMark})  AI={aiStr}({aMark})  tunedH={jo.GetProperty("tunedHeuristic").GetString()}");
            Console.WriteLine($"    {jo.GetProperty("visual").GetString()}");
            if (++shown >= 8) break;
        }

        var bookReport = new
        {
            projectId,
            path,
            goldCount = bn,
            baselineHeuristicCorrect = bb,
            tunedHeuristicCorrect = bt,
            aiCorrect = ba,
            baselineHeuristicAccuracyPct = Math.Round(basePct, 1),
            tunedHeuristicAccuracyPct = Math.Round(tunedPct, 1),
            aiAccuracyPct = Math.Round(aPct, 1),
            baselineDurationOk = bbd,
            tunedDurationOk = btd,
            aiDurationOk = bad,
            baselineDurationOkPct = Math.Round(baseDurPct, 1),
            tunedDurationOkPct = Math.Round(tunedDurPct, 1),
            aiDurationOkPct = Math.Round(aDurPct, 1),
            aiMissing,
            note = "baselineHeuristic = checked-in InferActionClass (first silent→establishing). tunedHeuristic may be eval-contaminated.",
            rows,
        };
        bookSummaries.Add(bookReport);
        var bookOut = Path.Combine(scoreDir, $"{projectId}_gt_score.json");
        await File.WriteAllTextAsync(bookOut, JsonSerializer.Serialize(bookReport, Pretty()));
        Console.WriteLine($"  Wrote {bookOut}");
    }

    var overallBase = nScored > 0 ? 100.0 * baseCorrect / nScored : 0;
    var overallTuned = nScored > 0 ? 100.0 * tunedCorrect / nScored : 0;
    var overallA = nScored > 0 ? 100.0 * aCorrect / nScored : 0;
    var overallBaseD = nScored > 0 ? 100.0 * baseDurOk / nScored : 0;
    var overallTunedD = nScored > 0 ? 100.0 * tunedDurOk / nScored : 0;
    var overallAd = nScored > 0 ? 100.0 * aDurOk / nScored : 0;

    Console.WriteLine("\n======== GROUND TRUTH SCORE ========");
    Console.WriteLine("Fair product comparison = baseline checked-in heuristic vs AI vs gold.");
    Console.WriteLine("Tuned heuristic was changed after looking at these books → contaminated for this set.");
    Console.WriteLine($"Gold beats scored: {nScored}  (books with GT: {bookSummaries.Count}, missing GT books: {missingGtBooks})");
    Console.WriteLine($"Baseline H (checked-in): {baseCorrect}/{nScored} ({overallBase:F1}%)  durationOK {baseDurOk}/{nScored} ({overallBaseD:F1}%)");
    Console.WriteLine($"Tuned H (contaminated):  {tunedCorrect}/{nScored} ({overallTuned:F1}%)  durationOK {tunedDurOk}/{nScored} ({overallTunedD:F1}%)");
    Console.WriteLine($"AI:                      {aCorrect}/{nScored} ({overallA:F1}%)  durationOK {aDurOk}/{nScored} ({overallAd:F1}%)");
    Console.WriteLine("Baseline error hist (pred→gold):");
    foreach (var kv in baseHist.OrderByDescending(k => k.Value).Take(12))
        Console.WriteLine($"  {kv.Key}: {kv.Value}");
    Console.WriteLine("AI error hist (pred→gold):");
    foreach (var kv in aHist.OrderByDescending(k => k.Value).Take(12))
        Console.WriteLine($"  {kv.Key}: {kv.Value}");

    if (nScored == 0)
    {
        Console.WriteLine("\nNo ground truth yet. Run:");
        Console.WriteLine("  BeatLabelEval --export-annotate --all");
        Console.WriteLine("Then fill gold.class and save as ground_truth/{ProjectId}.json");
        return 2;
    }

    var summary = new
    {
        ts = DateTimeOffset.UtcNow.ToString("o"),
        metric = "accuracy vs human ground truth",
        fairComparison = "baselineHeuristic (checked-in) vs AI vs gold — not tuned heuristic, not heuristic↔AI agreement",
        contaminationNote = "tunedHeuristic was edited after reviewing these eval books; report only for diagnostics. AI prompt few-shots also touched similar examples — prefer held-out books for final ship decision.",
        aiFrom,
        promptVersion = promptVer,
        goldBeatsScored = nScored,
        booksWithGold = bookSummaries.Count,
        baselineHeuristic = new
        {
            description = "Checked-in InferActionClass: first silent → establishing; short holds; big_action verbs",
            correct = baseCorrect,
            accuracyPct = Math.Round(overallBase, 1),
            durationOk = baseDurOk,
            durationOkPct = Math.Round(overallBaseD, 1),
            errorHistogram = baseHist.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => k.Value),
        },
        tunedHeuristic = new
        {
            description = "Working-tree InferActionClass after eval-driven edits (contaminated on this book set)",
            contaminated = true,
            correct = tunedCorrect,
            accuracyPct = Math.Round(overallTuned, 1),
            durationOk = tunedDurOk,
            durationOkPct = Math.Round(overallTunedD, 1),
            errorHistogram = tunedHist.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => k.Value),
        },
        ai = new
        {
            correct = aCorrect,
            accuracyPct = Math.Round(overallA, 1),
            durationOk = aDurOk,
            durationOkPct = Math.Round(overallAd, 1),
            errorHistogram = aHist.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => k.Value),
        },
        books = bookSummaries.Select(b =>
        {
            var el = JsonSerializer.SerializeToElement(b);
            return new
            {
                projectId = el.GetProperty("projectId").GetString(),
                goldCount = el.GetProperty("goldCount").GetInt32(),
                baselineHeuristicAccuracyPct = el.GetProperty("baselineHeuristicAccuracyPct").GetDouble(),
                tunedHeuristicAccuracyPct = el.GetProperty("tunedHeuristicAccuracyPct").GetDouble(),
                aiAccuracyPct = el.GetProperty("aiAccuracyPct").GetDouble(),
                baselineDurationOkPct = el.GetProperty("baselineDurationOkPct").GetDouble(),
                aiDurationOkPct = el.GetProperty("aiDurationOkPct").GetDouble(),
            };
        }),
    };
    var summaryPath = Path.Combine(scoreDir, "summary.json");
    await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, Pretty()));
    Console.WriteLine($"\nSummary: {summaryPath}");
    return 0;
}

// ---- helpers ----

static JsonSerializerOptions Pretty() => new() { WriteIndented = true };

static string ProjectIdFromFountain(string path) =>
    new DirectoryInfo(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!).Name;

static BookLoad LoadBook(string path)
{
    var fountain = File.ReadAllText(path);
    var parsed = FountainParser.Parse(fountain);
    var stage1 = FountainStage1Importer.BuildStage1(parsed);
    var scenes = stage1["scenes"] as List<object?> ?? new List<object?>();

    var flat = new List<FlatBeat>();
    var sceneIdx = 0;
    foreach (var sObj in scenes)
    {
        if (sObj is not Dictionary<string, object?> scene) continue;
        sceneIdx++;
        var setting = scene.TryGetValue("setting", out var st) ? st?.ToString() ?? "" : "";
        var beats = scene.TryGetValue("story_beats", out var sb) && sb is List<object?> list
            ? list
            : new List<object?>();
        var firstAction = true;
        var bi = 0;
        foreach (var bObj in beats)
        {
            if (bObj is not Dictionary<string, object?> beat) continue;
            bi++;
            var dlg = beat.TryGetValue("dialogue", out var d) ? d?.ToString()?.Trim() ?? "" : "";
            var ve = beat.TryGetValue("visual_event", out var v) ? v?.ToString()?.Trim() ?? "" : "";
            var isSilent = string.IsNullOrWhiteSpace(dlg) && ve.Length > 0;
            string heuristic = "";
            if (isSilent)
            {
                var existing = beat.TryGetValue("action_class", out var ac) ? ac?.ToString() ?? "" : "";
                heuristic = string.IsNullOrWhiteSpace(existing)
                    ? FountainStage1Importer.InferActionClass(ve, firstAction)
                    : NormalizeClass(existing);
                firstAction = false;
            }

            flat.Add(new FlatBeat(
                Id: $"s{sceneIdx}_b{bi}",
                Scene: sceneIdx,
                Setting: setting,
                IndexInScene: bi,
                TotalInScene: beats.Count,
                VisualEvent: ve,
                Dialogue: dlg,
                IsSilent: isSilent,
                Heuristic: heuristic));
        }
    }

    var silent = flat.Where(b => b.IsSilent).ToList();
    return new BookLoad(flat, silent);
}

static List<FlatBeat> SampleSilent(List<FlatBeat> actionBeats, int max)
{
    if (actionBeats.Count <= max)
        return actionBeats.ToList();
    var step = actionBeats.Count / (double)max;
    return Enumerable.Range(0, max)
        .Select(i => actionBeats[Math.Min(actionBeats.Count - 1, (int)(i * step))])
        .GroupBy(b => b.Id)
        .Select(g => g.First())
        .ToList();
}

static Dictionary<string, GoldLabel> LoadGroundTruth(string path)
{
    var map = new Dictionary<string, GoldLabel>(StringComparer.OrdinalIgnoreCase);
    if (!File.Exists(path)) return map;
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var root = doc.RootElement;
    JsonElement labelsEl;
    if (root.TryGetProperty("labels", out var l))
        labelsEl = l;
    else if (root.ValueKind == JsonValueKind.Array)
        labelsEl = root;
    else
        return map;

    foreach (var el in labelsEl.EnumerateArray())
    {
        var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString()
            : el.TryGetProperty("Id", out var id2) ? id2.GetString() : null;
        string? cls = null;
        if (el.TryGetProperty("class", out var c)) cls = c.GetString();
        else if (el.TryGetProperty("gold", out var g))
        {
            if (g.ValueKind == JsonValueKind.String) cls = g.GetString();
            else if (g.ValueKind == JsonValueKind.Object && g.TryGetProperty("class", out var gc))
                cls = gc.GetString();
        }
        var note = el.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(cls)) continue;
        map[id!] = new GoldLabel(NormalizeClass(cls!), note);
    }
    return map;
}

static async Task<(Dictionary<string, string> Labels, string Raw)> FetchAiLabelsAsync(
    HttpClient http, string systemPrompt, BookLoad book, List<FlatBeat> sample)
{
    var payloadBeats = sample.Select(b =>
    {
        FlatBeat? prev = null, next = null;
        var sceneBeats = book.Flat.Where(x => x.Scene == b.Scene).OrderBy(x => x.IndexInScene).ToList();
        var ix = sceneBeats.FindIndex(x => x.Id == b.Id);
        if (ix > 0) prev = sceneBeats[ix - 1];
        if (ix >= 0 && ix < sceneBeats.Count - 1) next = sceneBeats[ix + 1];

        return new Dictionary<string, object?>
        {
            ["id"] = b.Id,
            ["scene"] = b.Scene,
            ["beat_index"] = b.IndexInScene,
            ["beats_in_scene"] = b.TotalInScene,
            ["is_first_silent_in_scene"] = book.Silent.Where(a => a.Scene == b.Scene).OrderBy(a => a.IndexInScene).First().Id == b.Id,
            ["setting"] = Trunc(b.Setting, 100),
            ["visual_event"] = Trunc(b.VisualEvent, 280),
            ["prev_beat"] = prev is null ? null : DescribeNeighbor(prev),
            ["next_beat"] = next is null ? null : DescribeNeighbor(next),
        };
    }).ToList();

    var user =
        "Label each silent beat for duration budgeting. Return JSON only.\n\n" +
        JsonSerializer.Serialize(new { beats = payloadBeats }, Pretty());
    var raw = await ChatAsync(http, systemPrompt, user, "grok-4.5", 0.1);
    return (ParseLabels(raw), raw);
}

static object DescribeNeighbor(FlatBeat b)
{
    if (b.IsSilent)
        return new { kind = "silent", visual = Trunc(b.VisualEvent, 120) };
    return new
    {
        kind = "dialogue",
        visual = Trunc(b.VisualEvent, 80),
        dialogue = Trunc(b.Dialogue, 80),
    };
}

/// <summary>
/// Checked-in InferActionClass before eval-driven edits (first silent always establishing).
/// Kept frozen in the eval tool so we never score a heuristic that was tuned on this gold set
/// as if it were a fair general baseline.
/// </summary>
static string BaselineInferActionClass(string actionText, bool isFirstBeatInScene)
{
    var t = (actionText ?? "").Trim();
    if (t.Length == 0)
        return isFirstBeatInScene ? "establishing" : "hold";

    var lower = t.ToLowerInvariant();
    var words = ClipDurationEstimator.CountWords(t);

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

static int SecondsForClass(string cls) => NormalizeClass(cls) switch
{
    "hold" => ClipDurationEstimator.ActionOnlyMinSeconds,
    "establishing" => ClipDurationEstimator.EstablishingMaxSeconds,
    "big_action" => 8,
    _ => ClipDurationEstimator.SilentActionMaxSeconds,
};

static string NormalizeClass(string c)
{
    c = (c ?? "").Trim().ToLowerInvariant();
    return c switch
    {
        "establishing" or "hold" or "action" or "big_action" => c,
        "dialogue" => "action",
        _ => "action",
    };
}

static string Trunc(string s, int n) =>
    string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";

static Dictionary<string, string> LoadAiLabelsFromReport(string path)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var root = doc.RootElement;
    if (root.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array)
    {
        foreach (var el in labels.EnumerateArray())
        {
            var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var cls = el.TryGetProperty("class", out var cEl) ? cEl.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(cls))
                map[id!] = NormalizeClass(cls!);
        }
    }
    if (root.TryGetProperty("comparisons", out var comps))
    {
        foreach (var el in comps.EnumerateArray())
        {
            var id = el.TryGetProperty("Id", out var idEl) ? idEl.GetString() : null;
            if (id is null && el.TryGetProperty("id", out var id2)) id = id2.GetString();
            var ai = el.TryGetProperty("ai", out var aEl) && aEl.ValueKind == JsonValueKind.String
                ? aEl.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(ai)) continue;
            map[id!] = NormalizeClass(ai!);
        }
    }
    return map;
}

static Dictionary<string, string> ParseLabels(string raw)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    raw = raw.Trim();
    if (raw.StartsWith("```"))
    {
        raw = Regex.Replace(raw, @"^```(?:json)?\s*", "", RegexOptions.IgnoreCase);
        raw = Regex.Replace(raw, @"\s*```\s*$", "");
    }

    try
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        JsonElement arr;
        if (root.ValueKind == JsonValueKind.Array)
            arr = root;
        else if (root.TryGetProperty("labels", out var l))
            arr = l;
        else
            return map;

        foreach (var el in arr.EnumerateArray())
        {
            var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var cls = el.TryGetProperty("class", out var cEl) ? cEl.GetString()
                : el.TryGetProperty("action_class", out var aEl) ? aEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(cls)) continue;
            map[id!] = NormalizeClass(cls!);
        }
    }
    catch
    {
        // fall through empty
    }
    return map;
}

static async Task<string> ChatAsync(HttpClient http, string system, string user, string model, double temp)
{
    var body = new
    {
        model,
        temperature = temp,
        messages = new object[]
        {
            new { role = "system", content = system },
            new { role = "user", content = user },
        },
    };
    using var resp = await http.PostAsJsonAsync("chat/completions", body);
    var text = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
        throw new InvalidOperationException($"chat HTTP {(int)resp.StatusCode}: {Trunc(text, 400)}");
    using var doc = JsonDocument.Parse(text);
    return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
           ?? "";
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PageToMovie.sln")) ||
            Directory.Exists(Path.Combine(dir.FullName, "projects")))
            return dir.FullName;
        // tools/BeatLabelEval → repo via host/
        if (Directory.Exists(Path.Combine(dir.FullName, "host")) &&
            Directory.Exists(Path.Combine(dir.FullName, "projects")))
            return dir.FullName;
        dir = dir.Parent;
    }
    // walk up from cwd
    dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "projects")) &&
            Directory.Exists(Path.Combine(dir.FullName, "host")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return Directory.GetCurrentDirectory();
}

static string SystemPromptV2() => """
You label silent film beats for DURATION BUDGETING in a video pipeline (any story).
Each label maps to planned clip length — optimize for that, not literary theory.

Classes (pick exactly one):
- establishing: NEW place/room open, first wide setup of a location. NOT every scene's first beat.
  Mid-story business (shopping, sitting at window continuing a prior room, writing a letter) is NOT establishing.
  Duration intent: ~4–5 seconds max.
- hold: micro performance / stillness — smile, look, hands, pause, freeze, short gesture, reaction.
  Duration intent: ~3 seconds.
- action: ordinary physical business (walk, open door, set tray, cross room) without spectacle.
  Duration intent: ~3–5 seconds.
- big_action: chase, fight, crash, leap, climb under danger, vault, violent or high-energy continuous motion.
  Duration intent: longer (up to ~10–12s).

Critical bias correction:
- The FIRST silent beat of a scene is OFTEN action or hold, not establishing.
- Only use establishing when the visual is truly about revealing/establishing a place or setup.
- Prefer hold over action for pure reaction/emotion with little locomotion.
- Prefer big_action only when energy/motion is the point of the shot.

Return JSON only:
{ "labels": [ { "id": "s1_b2", "class": "hold", "reason": "short reaction" } ] }
Use only the four class strings above.
""";

static string SystemPromptV3() => """
You assign action_class for silent beats in an automated film pipeline.
Goal: correct clip duration so the finished film is excellent with minimal human fixes.

Classes and target durations:
| class | seconds | when |
| establishing | 4-5 | true location/setup open only |
| hold | 3 | micro gesture, stillness, reaction, pause |
| action | 3-5 | normal business, walk, prop work |
| big_action | 6-12 | high energy continuous motion |

Hard rules:
1) Do NOT mark a beat establishing only because beat_index is 1 or is_first_silent_in_scene is true.
2) If prev_beat is dialogue or continuous business in the same setting, prefer action/hold.
3) If next_beat is dialogue, micro visual beats before speech are usually hold.
4) Songs, letters, reading faces, listening = hold or action, almost never big_action.
5) Chase, fight, fall, vault, stampede, crash = big_action.

Few-shot (class only):
- "Della hurries along the cold sidewalk among Christmas shoppers" → action
- "He steadies his hands on his knees. A thin smile." → hold
- "A bare lamplit chamber. A plain wooden chair faces us. Narrator sits." → establishing
- "They chase through the alley and crash into the stalls." → big_action
- "She sits by the window with her journal, two weeks later, same room" → hold (or action), NOT establishing
- "Moonlight. The wallpaper moves; women crawl behind the pattern." → big_action or action, NOT establishing solely for being first in scene

Return JSON only:
{"labels":[{"id":"s1_b2","class":"hold","reason":"≤12 words"}]}
""";

// v4 lives on SilentBeatActionClassifier.SystemPromptV4 (product + eval share one string).

record FlatBeat(
    string Id,
    int Scene,
    string Setting,
    int IndexInScene,
    int TotalInScene,
    string VisualEvent,
    string Dialogue,
    bool IsSilent,
    string Heuristic);

record BookLoad(List<FlatBeat> Flat, List<FlatBeat> Silent);

record GoldLabel(string Class, string Note);
