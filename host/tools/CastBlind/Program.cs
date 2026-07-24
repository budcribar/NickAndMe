using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Engine;

var repo = FindRepo();
var holdout = "The_Jungle_Book";
var seed = 20260722;
var count = 20;
var fountain = Directory.GetFiles(Path.Combine(repo, "projects"), "screenplay.fountain", SearchOption.AllDirectories)
    .First(p => p.Contains(holdout, StringComparison.OrdinalIgnoreCase) && !p.Contains($"{Path.DirectorySeparatorChar}_"));
var key = Environment.GetEnvironmentVariable("XAI_API_KEY") ?? throw new Exception("no key");
var stage1 = FountainStage1Importer.BuildStage1(FountainParser.Parse(await File.ReadAllTextAsync(fountain)));
var castPath = Path.Combine(Path.GetDirectoryName(fountain)!, "cast_seeds.json");
if (File.Exists(castPath))
{
    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(castPath));
    if (doc.RootElement.TryGetProperty("character_seed_tokens", out var seedsEl))
    {
        var gpv = stage1["global_production_variables"] as Dictionary<string, object?> ?? new();
        var dict = new Dictionary<string, object?>();
        foreach (var p in seedsEl.EnumerateObject())
        {
            var inner = new Dictionary<string, object?>();
            if (p.Value.ValueKind == JsonValueKind.Object)
                foreach (var q in p.Value.EnumerateObject())
                    if (q.Value.ValueKind == JsonValueKind.String) inner[q.Name] = q.Value.GetString();
            dict[p.Name] = inner;
        }
        gpv["character_seed_tokens"] = dict;
        stage1["global_production_variables"] = gpv;
    }
}
var castKeys = (stage1["global_production_variables"] as Dictionary<string, object?>)?
    .GetValueOrDefault("character_seed_tokens") is Dictionary<string, object?> cs
    ? cs.Keys.Where(k => k.StartsWith("Character_", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k).ToList()
    : new List<string>();
Console.WriteLine($"cast keys: {castKeys.Count}");

var beats = new List<(string Id, string Visual, string Dialogue, string Speaker, bool Vo)>();
var scenes = stage1["scenes"] as List<object?> ?? new();
var si = 0;
foreach (var sItem in scenes)
{
    if (sItem is not Dictionary<string, object?> scene) continue;
    si++;
    var bl = scene.TryGetValue("story_beats", out var sb) && sb is List<object?> list ? list : new();
    var bi = 0;
    foreach (var bItem in bl)
    {
        if (bItem is not Dictionary<string, object?> beat) continue;
        bi++;
        var ve = beat.TryGetValue("visual_event", out var v) ? v?.ToString()?.Trim() ?? "" : "";
        var dlg = beat.TryGetValue("dialogue", out var d) ? d?.ToString() ?? "" : "";
        var sp = beat.TryGetValue("speaker", out var s) ? s?.ToString() ?? "" : "";
        var del = beat.TryGetValue("delivery", out var delv) ? delv?.ToString() ?? "" : "";
        if (ve.Length == 0 && dlg.Length == 0) continue;
        beats.Add(($"s{si}_b{bi}", ve, dlg, sp, del.Contains("voiceover", StringComparison.OrdinalIgnoreCase)));
    }
}
var rng = new Random(seed);
bool ActionLike(string v) => v.Length >= 40 && !Regex.IsMatch(v, @"^\w[\w\s']+\s+speaks\.?\s*$", RegexOptions.IgnoreCase);
var action = beats.Where(b => ActionLike(b.Visual)).OrderBy(_ => rng.Next()).ToList();
var dial = beats.Where(b => !ActionLike(b.Visual)).OrderBy(_ => rng.Next()).ToList();
var nA = Math.Min(action.Count, 15);
var sample = action.Take(nA).Concat(dial.Take(count - nA)).OrderBy(_ => rng.Next()).Take(count).ToList();
Console.WriteLine($"beats={beats.Count} sample={sample.Count}");

var profiles = castKeys.ToDictionary(k => k,
    k => new ClipVideoPromptBuilder.CharacterProfile { DisplayName = k.Replace("Character_", "").Replace('_', ' ') },
    StringComparer.OrdinalIgnoreCase);

var payload = sample.Select(b =>
{
    var h = ClipVideoPromptBuilder.InferKeysFromProse(b.Visual + " " + b.Dialogue, profiles);
    if (!string.IsNullOrWhiteSpace(b.Speaker) && !b.Vo && !h.Contains(b.Speaker, StringComparer.OrdinalIgnoreCase))
        h = h.Append(b.Speaker).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    return new {
        id = b.Id,
        visual_event = b.Visual.Length > 280 ? b.Visual[..280] + "…" : b.Visual,
        dialogue = b.Dialogue.Length > 120 ? b.Dialogue[..120] : b.Dialogue,
        speaker_key = b.Speaker,
        is_voiceover = b.Vo,
        heuristic_keys = h,
    };
}).ToList();

using var http = new HttpClient { BaseAddress = new Uri("https://api.x.ai/v1/"), Timeout = TimeSpan.FromMinutes(4) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
var body = new Dictionary<string, object?> {
    ["model"] = "grok-4.5", ["temperature"] = 0,
    ["messages"] = new object[] {
        new Dictionary<string,object?>{["role"]="system",["content"]=OnScreenCastClassifier.SystemPrompt()},
        new Dictionary<string,object?>{["role"]="user",["content"]="Pick on-screen Character_* keys from the closed cast. JSON only.\n"+JsonSerializer.Serialize(new{cast_keys=castKeys,beats=payload})}
    }
};
var resp = await http.PostAsync("chat/completions", new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
var text = await resp.Content.ReadAsStringAsync();
if (!resp.IsSuccessStatusCode) { Console.Error.WriteLine(text[..Math.Min(600,text.Length)]); return 1; }
using var rdoc = JsonDocument.Parse(text);
var content = rdoc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
var ai = OnScreenCastClassifier.ParseLabels(content, castKeys);

var rows = new List<object>();
var n=0; int agreeN=0;
foreach (var b in sample)
{
    n++;
    var h = ClipVideoPromptBuilder.InferKeysFromProse(b.Visual + " " + b.Dialogue, profiles);
    if (!string.IsNullOrWhiteSpace(b.Speaker) && !b.Vo && !h.Contains(b.Speaker, StringComparer.OrdinalIgnoreCase))
        h = h.Concat(new[]{b.Speaker}).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    ai.TryGetValue(b.Id, out var ak);
    ak ??= new List<string>();
    var agree = new HashSet<string>(h, StringComparer.OrdinalIgnoreCase).SetEquals(ak);
    if (agree) agreeN++;
    Console.WriteLine($"--- #{n} {b.Id} {(agree?"[AGREE]":"[DIFF]")} ---");
    Console.WriteLine($"VISUAL: {b.Visual}");
    if (!string.IsNullOrWhiteSpace(b.Dialogue)) Console.WriteLine($"DIALOGUE: {Trunc(b.Dialogue,160)}");
    if (!string.IsNullOrWhiteSpace(b.Speaker)) Console.WriteLine($"SPEAKER: {b.Speaker} vo={b.Vo}");
    Console.WriteLine($"BASELINE: [{string.Join(", ", h)}]");
    Console.WriteLine($"AI:       [{string.Join(", ", ak)}]");
    Console.WriteLine();
    rows.Add(new { n, id=b.Id, visual=b.Visual, dialogue=b.Dialogue, speaker=b.Speaker, vo=b.Vo, baseline=h, ai=ak, agree });
}
var outPath = Path.Combine(repo, "host", "evals", "heuristic_ai_eval", "cast_blind_20.json");
Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(new { seed, holdout, sampleCount=rows.Count, castKeyCount=castKeys.Count, agree=agreeN, rows }, new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Agree {agreeN}/{rows.Count}");
Console.WriteLine($"Wrote {outPath}");
return 0;
static string Trunc(string s,int n)=>string.IsNullOrEmpty(s)?"":s.Length<=n?s:s[..n]+"…";
static string FindRepo() {
  var d = new DirectoryInfo(Directory.GetCurrentDirectory());
  while (d!=null) {
    if (Directory.Exists(Path.Combine(d.FullName,"projects")) && Directory.Exists(Path.Combine(d.FullName,"host"))) return d.FullName;
    d = d.Parent;
  }
  return Directory.GetCurrentDirectory();
}
