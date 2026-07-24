using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Engine;

var repo = FindRepo();
var holdout = "The_Jungle_Book";
var seed = 20260722;
var count = 20;
var mode = "stratified"; // random | stratified (prefer action/non-dialogue)
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--seed" && i + 1 < args.Length && int.TryParse(args[i + 1], out var s)) seed = s;
    if (args[i] == "--count" && i + 1 < args.Length && int.TryParse(args[i + 1], out var c)) count = c;
    if (args[i] == "--mode" && i + 1 < args.Length) mode = args[i + 1];
    if (!args[i].StartsWith("-") && args[i] is not ("--seed" or "--count" or "--mode"))
        holdout = args[i];
}

var fountain = Directory.GetFiles(Path.Combine(repo, "projects"), "screenplay.fountain", SearchOption.AllDirectories)
    .FirstOrDefault(p => p.Contains(holdout, StringComparison.OrdinalIgnoreCase)
                         && !p.Contains($"{Path.DirectorySeparatorChar}_"));
if (fountain is null) { Console.Error.WriteLine("no fountain"); return 1; }

var key = Environment.GetEnvironmentVariable("XAI_API_KEY");
if (string.IsNullOrWhiteSpace(key)) { Console.Error.WriteLine("XAI_API_KEY required"); return 1; }

var stage1 = FountainStage1Importer.BuildStage1(FountainParser.Parse(await File.ReadAllTextAsync(fountain)));
var beats = Collect(stage1);
Console.WriteLine($"project={holdout} beats={beats.Count} seed={seed} count={count} mode={mode}");
if (beats.Count == 0) return 1;

var rng = new Random(seed);
List<(string Id, string Visual)> sample;
if (mode.Equals("stratified", StringComparison.OrdinalIgnoreCase))
{
    // ~75% action-ish (not pure "X speaks." / short parenthetical), ~25% dialogue stubs
    var action = beats.Where(b => IsActionLike(b.Visual)).OrderBy(_ => rng.Next()).ToList();
    var dialogue = beats.Where(b => !IsActionLike(b.Visual)).OrderBy(_ => rng.Next()).ToList();
    var nAction = Math.Min(action.Count, (int)Math.Round(count * 0.75));
    var nDial = Math.Min(dialogue.Count, count - nAction);
    if (nAction + nDial < count)
        nAction = Math.Min(action.Count, count - nDial);
    sample = action.Take(nAction).Concat(dialogue.Take(nDial)).OrderBy(_ => rng.Next()).Take(count).ToList();
    Console.WriteLine($"pool action={action.Count} dialogue={dialogue.Count} picked action~{nAction} dialogue~{nDial}");
}
else
{
    sample = beats.OrderBy(_ => rng.Next()).Take(count).ToList();
}

var payload = sample.Select(b =>
{
    var (ha, hs) = FountainStage1Importer.InferAmbientAndSfx(b.Visual);
    var vis = b.Visual.Length > 400 ? b.Visual[..400] + "…" : b.Visual;
    return new { id = b.Id, visual_event = vis, heuristic_ambient = ha, heuristic_sfx = hs };
}).ToList();

using var http = new HttpClient { BaseAddress = new Uri("https://api.x.ai/v1/"), Timeout = TimeSpan.FromMinutes(4) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
var bodyObj = new Dictionary<string, object?>
{
    ["model"] = "grok-4.5",
    ["temperature"] = 0,
    ["messages"] = new object[]
    {
        new Dictionary<string, object?> { ["role"] = "system", ["content"] = AmbientSfxClassifier.SystemPrompt() },
        new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = "Split each beat into ambient bed vs sfx hits. JSON only.\n" +
                          JsonSerializer.Serialize(new { beats = payload })
        }
    }
};
var resp = await http.PostAsync("chat/completions",
    new StringContent(JsonSerializer.Serialize(bodyObj), Encoding.UTF8, "application/json"));
var respText = await resp.Content.ReadAsStringAsync();
if (!resp.IsSuccessStatusCode)
{
    Console.Error.WriteLine(respText[..Math.Min(800, respText.Length)]);
    return 1;
}
using var doc = JsonDocument.Parse(respText);
var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
var ai = AmbientSfxClassifier.ParseLabels(content);

var rows = new List<object>();
Console.WriteLine();
Console.WriteLine($"=== {count} Ambient/SFX: baseline vs AI ({mode}) ===");
var n = 0;
foreach (var b in sample)
{
    n++;
    var (ha, hs) = FountainStage1Importer.InferAmbientAndSfx(b.Visual);
    ai.TryGetValue(b.Id, out var pair);
    var aa = pair.Ambient ?? "";
    var asx = pair.Sfx ?? "";
    var same = string.Equals(ha.Trim(), aa.Trim(), StringComparison.OrdinalIgnoreCase)
               && string.Equals(hs.Trim(), asx.Trim(), StringComparison.OrdinalIgnoreCase);
    Console.WriteLine($"--- #{n}  {b.Id} {(same ? "[AGREE]" : "[DIFF]")} ---");
    Console.WriteLine($"VISUAL: {b.Visual}");
    Console.WriteLine($"BASELINE ambient: {E(ha)}");
    Console.WriteLine($"BASELINE sfx:     {E(hs)}");
    Console.WriteLine($"AI       ambient: {E(aa)}");
    Console.WriteLine($"AI       sfx:     {E(asx)}");
    Console.WriteLine();
    rows.Add(new
    {
        n, id = b.Id, visual = b.Visual,
        baseline_ambient = ha, baseline_sfx = hs,
        ai_ambient = aa, ai_sfx = asx, agree = same,
        action_like = IsActionLike(b.Visual)
    });
}

var outPath = Path.Combine(repo, "host", "evals", "heuristic_ai_eval", $"ambient_blind_{count}.json");
Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(new
{
    seed,
    holdout,
    mode,
    sampleCount = rows.Count,
    rows
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Wrote {outPath}");
return 0;

static string E(string s) => string.IsNullOrEmpty(s) ? "(empty)" : s;

/// <summary>Prefer multi-word action prose over "Name speaks." / short parentheticals.</summary>
static bool IsActionLike(string visual)
{
    var v = (visual ?? "").Trim();
    if (v.Length < 24) return false;
    if (Regex.IsMatch(v, @"^\w[\w\s']+\s+speaks\.?\s*$", RegexOptions.IgnoreCase)) return false;
    if (Regex.IsMatch(v, @"^[\w\s']+\s*\([^)]+\)\.?\s*$", RegexOptions.IgnoreCase) && v.Length < 40)
        return false;
    return true;
}

static List<(string Id, string Visual)> Collect(Dictionary<string, object?> stage1)
{
    var list = new List<(string, string)>();
    var scenes = stage1.TryGetValue("scenes", out var sObj) && sObj is List<object?> sl ? sl : new();
    var si = 0;
    foreach (var sItem in scenes)
    {
        if (sItem is not Dictionary<string, object?> scene) continue;
        si++;
        var beats = scene.TryGetValue("story_beats", out var sb) && sb is List<object?> bl ? bl : new();
        var bi = 0;
        foreach (var bItem in beats)
        {
            if (bItem is not Dictionary<string, object?> beat) continue;
            bi++;
            var ve = beat.TryGetValue("visual_event", out var v) ? v?.ToString()?.Trim() ?? "" : "";
            if (ve.Length == 0) continue;
            list.Add(($"s{si}_b{bi}", ve));
        }
    }
    return list;
}

static string FindRepo()
{
    var d = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (d != null)
    {
        if (Directory.Exists(Path.Combine(d.FullName, "projects")) &&
            Directory.Exists(Path.Combine(d.FullName, "host")))
            return d.FullName;
        d = d.Parent;
    }
    return Directory.GetCurrentDirectory();
}
