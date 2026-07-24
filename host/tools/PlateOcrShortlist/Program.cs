using System.Text.Json;
using System.Text.Json.Nodes;
using PageToMovie.Engine;

// Thin driver over BookOcrPlateShortlist (product algorithm).
// Usage: dotnet run --project host/tools/PlateOcrShortlist -- [projects/Buster2]

var repo = FindRepo();
var projectRel = args.FirstOrDefault() ?? Path.Combine("projects", "Buster2");
var projectDir = Path.IsPathRooted(projectRel) ? projectRel : Path.Combine(repo, projectRel);
var bookTxt = BookOcrPlateShortlist.FindBookFullPath(projectDir)
              ?? Path.Combine(projectDir, "source", BookOcrPlateShortlist.BookFullFileName);
var goldPath = Path.Combine(repo, "host", "evals", "classifier_benchmarks", "gold", "Buster2", "plate_rank.json");
var castPath = Path.Combine(projectDir, "source", "cast_seeds.json");

if (!File.Exists(bookTxt))
{
    Console.Error.WriteLine($"Missing {bookTxt}");
    return 1;
}

var pages = BookOcrPlateShortlist.ParseBookFull(await File.ReadAllTextAsync(bookTxt));
Console.WriteLine($"Parsed {pages.Count} pages from {bookTxt}");
foreach (var p in pages.OrderBy(x => x.Page))
    Console.WriteLine(
        $"  p{p.Page:D2} art={BookOcrPlateShortlist.IsArtPage(p)} chars={p.Text.Trim().Length} " +
        $"preview={Trunc(p.Text.Replace('\n', ' '), 60)}");

var seeds = LoadSeeds(castPath);
var castKeys = new[] { "Character_Buster", "Character_Bunnies" };
var results = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
foreach (var key in castKeys)
{
    seeds.TryGetValue(key, out var seed);
    var aliases = BookOcrPlateShortlist.AliasesForSeed(key, seed);
    var max = key.Contains("Bunn", StringComparison.OrdinalIgnoreCase) ? 1 : 3;
    var plates = BookOcrPlateShortlist.ShortlistArtPages(pages, aliases, max);
    results[key] = plates;
    var hits = BookOcrPlateShortlist.FindTextHitPages(pages, aliases);
    Console.WriteLine();
    Console.WriteLine($"=== {key} (need {max}) ===");
    Console.WriteLine($"  aliases:   {string.Join(", ", aliases)}");
    Console.WriteLine($"  text hits: {string.Join(", ", hits.Select(h => $"p{h}"))}");
    Console.WriteLine($"  plates:    {string.Join(", ", plates.Select(p => $"p{p}"))}");
}

var gold = LoadGold(goldPath);
Console.WriteLine();
Console.WriteLine("=== SCORE vs gold ===");
var busterHits = results.GetValueOrDefault("Character_Buster")?
    .Where(p => gold.GetValueOrDefault("Character_Buster")?.Contains(p) == true).Distinct().Take(3).ToList() ?? new();
var bunnyHits = results.GetValueOrDefault("Character_Bunnies")?
    .Where(p => gold.GetValueOrDefault("Character_Bunnies")?.Contains(p) == true).Distinct().ToList() ?? new();
var perfect = busterHits.Count >= 3 && bunnyHits.Contains(13);
Console.WriteLine($"  Buster: {Fmt(busterHits)} (need 3 gold) → {(busterHits.Count >= 3 ? "PASS" : "FAIL")}");
Console.WriteLine($"  Bunny:  {Fmt(bunnyHits)} (need p13) → {(bunnyHits.Contains(13) ? "PASS" : "FAIL")}");
Console.WriteLine();
Console.WriteLine($"USER CRITERION: 3 Busters + 1 bunny(p13) → {(perfect ? "100% PASS" : "FAIL")}");

var outPath = Path.Combine(repo, "host", "evals", "classifier_benchmarks", "plate_ocr_shortlist_buster2.json");
await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(new
{
    algorithm = "BookOcrPlateShortlist (product)",
    results,
    busterHits,
    bunnyHits,
    pass = perfect,
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Wrote {outPath}");
return perfect ? 0 : 2;

static Dictionary<string, JsonObject> LoadSeeds(string path)
{
    var map = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
    if (!File.Exists(path)) return map;
    var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
    var seeds = root?["character_seed_tokens"] as JsonObject;
    if (seeds is null) return map;
    foreach (var (k, v) in seeds)
        if (v is JsonObject o) map[k] = o;
    return map;
}

static Dictionary<string, List<int>> LoadGold(string path)
{
    var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
    if (!File.Exists(path)) return map;
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    foreach (var el in doc.RootElement.GetProperty("labels").EnumerateArray())
    {
        var key = el.GetProperty("character_key").GetString() ?? "";
        var pages = new List<int>();
        if (el.TryGetProperty("gold_pages", out var gp))
            foreach (var x in gp.EnumerateArray())
                if (x.TryGetInt32(out var n)) pages.Add(n);
        map[key] = pages;
    }
    return map;
}

static string Fmt(IEnumerable<int> pages) => string.Join(",", pages.Select(p => $"p{p}"));
static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";
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
