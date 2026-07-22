using System.Text.Json;
using ClassifierBenchmarks;
using FilmStudio.Engine;

// ClassifierBenchmarks — durable AI vs baseline scorer with model/prompt matrix + history.
//
// Usage:
//   ClassifierBenchmarks run [--project The_Jungle_Book] [--tasks ambient_sfx,onscreen_cast,silent_beat_action]
//                            [--models grok-4.5] [--prompts v1_product,v2_grounded]
//                            [--temp 0] [--temps 0,0.2] [--note "after prompt tweak"]
//   silent_beat_action gold is multi-book under gold/_all_books/ (project flag ignored for gold path).
//   ClassifierBenchmarks report          # rebuild LATEST.md + history.html from history/index.json
//   ClassifierBenchmarks history         # print recent runs
//   ClassifierBenchmarks list-prompts --task ambient_sfx
//
// Gold:    host/evals/classifier_benchmarks/gold/{project}/{task}.json
// Prompts: host/evals/classifier_benchmarks/prompts/{task}/{promptId}.txt
// History: host/evals/classifier_benchmarks/history/runs/{runId}/

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

var paths = new BenchPaths(BenchPaths.FindRepoRoot());
var cmd = args[0].ToLowerInvariant();
var rest = args.Skip(1).ToArray();

try
{
    return cmd switch
    {
        "run" => await CmdRunAsync(paths, rest),
        "report" => await CmdReportAsync(paths),
        "history" => await CmdHistoryAsync(paths),
        "list-prompts" => CmdListPrompts(paths, rest),
        _ => Fail($"Unknown command '{cmd}'. Try: run | report | history | list-prompts"),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("""
        ClassifierBenchmarks — AI vs baseline over time (models × prompts)

          run     Score curated gold; append history; write reports
          report  Rebuild reports/LATEST.md + reports/history.html
          history Print recent runs
          list-prompts --task ambient_sfx

        Examples:
          dotnet run --project host/tools/ClassifierBenchmarks -- run --tasks ambient_sfx --prompts v1_product,v2_grounded --temps 0,0.2
          dotnet run --project host/tools/ClassifierBenchmarks -- run --tasks onscreen_cast --prompts v1_product,v2_grounded
          dotnet run --project host/tools/ClassifierBenchmarks -- run --tasks silent_beat_action --prompts v2_product
          dotnet run --project host/tools/ClassifierBenchmarks -- run --tasks ambient_sfx,species_kind,onscreen_cast,silent_beat_action
          dotnet run --project host/tools/ClassifierBenchmarks -- report
        """);
}

static int Fail(string msg)
{
    Console.Error.WriteLine(msg);
    return 1;
}

static Dictionary<string, string> ParseFlags(string[] args)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) continue;
        var key = args[i][2..];
        var val = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
        d[key] = val;
    }
    return d;
}

static List<string> SplitCsv(string? s) =>
    string.IsNullOrWhiteSpace(s)
        ? new List<string>()
        : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

static List<double> ParseTemps(Dictionary<string, string> flags)
{
    // --temps 0,0.2  preferred; --temp 0 still works
    var raw = flags.GetValueOrDefault("temps") ?? flags.GetValueOrDefault("temp");
    if (string.IsNullOrWhiteSpace(raw)) return new List<double> { 0 };
    var list = new List<double>();
    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (double.TryParse(part, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var t))
            list.Add(Math.Clamp(t, 0, 2));
    }
    return list.Count > 0 ? list : new List<double> { 0 };
}

static async Task<int> CmdRunAsync(BenchPaths paths, string[] args)
{
    var flags = ParseFlags(args);
    var temps = ParseTemps(flags);
    var cfg = new RunConfig
    {
        ProjectId = flags.GetValueOrDefault("project") ?? "The_Jungle_Book",
        Tasks = SplitCsv(flags.GetValueOrDefault("tasks") ?? "ambient_sfx"),
        Models = SplitCsv(flags.GetValueOrDefault("models") ?? "grok-4.5"),
        Prompts = SplitCsv(flags.GetValueOrDefault("prompts")),
        Temperatures = temps,
        Note = flags.GetValueOrDefault("note"),
    };

    var xaiKey = Environment.GetEnvironmentVariable("XAI_API_KEY");
    var claudeKey = Environment.GetEnvironmentVariable("CLAUDE_API_KEY");
    var needsXai = cfg.Models.Any(m => !ChatRunner.IsClaudeModel(m));
    var needsClaude = cfg.Models.Any(ChatRunner.IsClaudeModel);
    if (needsXai && string.IsNullOrWhiteSpace(xaiKey))
        return Fail("XAI_API_KEY required for model(s): " + string.Join(",", cfg.Models.Where(m => !ChatRunner.IsClaudeModel(m))));
    if (needsClaude && string.IsNullOrWhiteSpace(claudeKey))
        return Fail("CLAUDE_API_KEY required for model(s): " + string.Join(",", cfg.Models.Where(ChatRunner.IsClaudeModel)));

    // Ensure species prompt exists (snapshot from classifier)
    await EnsureDefaultSpeciesPromptAsync(paths);

    var runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + "_" + Guid.NewGuid().ToString("N")[..6];
    var run = new BenchmarkRun
    {
        RunId = runId,
        Utc = DateTimeOffset.UtcNow.ToString("u"),
        Config = cfg,
        RepoRoot = paths.RepoRoot,
    };

    Console.WriteLine($"Run {runId}");
    Console.WriteLine(
        $"  project={cfg.ProjectId} tasks=[{string.Join(",", cfg.Tasks)}] models=[{string.Join(",", cfg.Models)}] " +
        $"prompts=[{string.Join(",", cfg.Prompts)}] temps=[{string.Join(",", cfg.Temperatures.Select(t => t.ToString("0.##")))}]");

    using var chat = new ChatRunner(xaiKey, claudeKey);

    foreach (var task in cfg.Tasks)
    {
        // No --prompts given: use this task's product-recommended default, not a
        // one-size-fits-all guess that only happens to exist for some tasks.
        var promptIds = cfg.Prompts.Count > 0
            ? cfg.Prompts
            : new List<string> { TaskRunners.DefaultPromptId(task) };

        foreach (var model in cfg.Models)
        {
            foreach (var promptId in promptIds)
            {
                foreach (var temperature in cfg.Temperatures)
                {
                    // Map default/global prompt names onto this task's files when needed
                    var effectivePromptId = promptId;
                    if (task == "silent_beat_action" && !File.Exists(paths.PromptFile(task, promptId)))
                        effectivePromptId = "v2_product";

                    Console.WriteLine($"  → {task} · {model} · {effectivePromptId} · t={temperature:0.##}");
                    try
                    {
                        PromptBundle prompt;
                        try
                        {
                            prompt = PromptStore.Load(paths, task, effectivePromptId);
                        }
                        catch (FileNotFoundException) when (task == "species_kind" && effectivePromptId == "v1_product")
                        {
                            await EnsureDefaultSpeciesPromptAsync(paths);
                            prompt = PromptStore.Load(paths, task, effectivePromptId);
                        }
                        catch (FileNotFoundException) when (task == "silent_beat_action")
                        {
                            await EnsureDefaultSilentBeatPromptAsync(paths);
                            prompt = PromptStore.Load(paths, task, "v2_product");
                        }

                        TaskResult result = task switch
                        {
                            "ambient_sfx" => await TaskRunners.RunAmbientAsync(
                                paths, cfg.ProjectId, model, temperature, prompt, chat),
                            "species_kind" => await TaskRunners.RunSpeciesAsync(
                                paths, cfg.ProjectId, model, temperature, prompt, chat),
                            "onscreen_cast" => await TaskRunners.RunOnScreenCastAsync(
                                paths, cfg.ProjectId, model, temperature, prompt, chat),
                            "silent_beat_action" => await TaskRunners.RunSilentBeatActionAsync(
                                paths, cfg.ProjectId, model, temperature, prompt, chat),
                            "extend_cut" => await TaskRunners.RunExtendCutAsync(
                                paths, cfg.ProjectId, model, temperature, prompt, chat),
                            "plate_rank" => await TaskRunners.RunPlateRankAsync(
                                paths, cfg.ProjectId, model, temperature, prompt, chat),
                            _ => throw new InvalidOperationException(
                                $"Unknown task '{task}'. Supported: ambient_sfx, species_kind, onscreen_cast, silent_beat_action, extend_cut, plate_rank"),
                        };

                        run.Results.Add(result);
                        Console.WriteLine(
                            $"     baseline={result.BaselineScore:F3} ai={result.AiScore:F3} winner={result.Winner} n={result.SampleCount} ({result.LatencyMs}ms)");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"     ERROR: {ex.Message}");
                        run.Results.Add(new TaskResult
                        {
                            Task = task,
                            ProjectId = cfg.ProjectId,
                            Model = model,
                            PromptId = promptId,
                            Temperature = temperature,
                            Metric = "error",
                            Note = ex.Message,
                            Winner = "error",
                        });
                    }
                }
            }
        }
    }

    await ReportWriter.WriteRunArtifactsAsync(paths, run);
    await ReportWriter.AppendHistoryAsync(paths, run);
    await ReportWriter.WriteAggregateReportsAsync(paths);

    Console.WriteLine();
    Console.WriteLine(ReportWriter.BuildRunMarkdown(run));
    Console.WriteLine($"Saved history/runs/{runId}/");
    return run.Results.Any(r => r.Winner == "error") ? 2 : 0;
}

static async Task EnsureDefaultSpeciesPromptAsync(BenchPaths paths)
{
    var dir = Path.Combine(paths.Prompts, "species_kind");
    Directory.CreateDirectory(dir);
    var txt = Path.Combine(dir, "v1_product.txt");
    if (!File.Exists(txt))
    {
        await File.WriteAllTextAsync(txt, SpeciesKindClassifier.SystemPrompt().Trim() + Environment.NewLine);
        await File.WriteAllTextAsync(Path.Combine(dir, "v1_product.meta.json"),
            JsonSerializer.Serialize(new
            {
                id = "v1_product",
                task = "species_kind",
                label = "Product SpeciesKindClassifier prompt",
            }, JsonDefaults.Pretty));
    }
}

static async Task EnsureDefaultSilentBeatPromptAsync(BenchPaths paths)
{
    var dir = Path.Combine(paths.Prompts, "silent_beat_action");
    Directory.CreateDirectory(dir);
    var txt = Path.Combine(dir, "v2_product.txt");
    if (!File.Exists(txt))
    {
        await File.WriteAllTextAsync(txt, SilentBeatActionClassifier.SystemPromptV2().Trim() + Environment.NewLine);
        await File.WriteAllTextAsync(Path.Combine(dir, "v2_product.meta.json"),
            JsonSerializer.Serialize(new
            {
                id = "v2_product",
                task = "silent_beat_action",
                label = "Product SilentBeatActionClassifier (v2)",
            }, JsonDefaults.Pretty));
    }
}

static async Task<int> CmdReportAsync(BenchPaths paths)
{
    await ReportWriter.WriteAggregateReportsAsync(paths);
    return 0;
}

static async Task<int> CmdHistoryAsync(BenchPaths paths)
{
    if (!File.Exists(paths.HistoryIndex))
    {
        Console.WriteLine("No history yet.");
        return 0;
    }
    var index = JsonSerializer.Deserialize<HistoryIndex>(
        await File.ReadAllTextAsync(paths.HistoryIndex), JsonDefaults.Flexible) ?? new HistoryIndex();
    Console.WriteLine(ReportWriter.BuildHistoryMarkdown(index));
    return 0;
}

static int CmdListPrompts(BenchPaths paths, string[] args)
{
    var flags = ParseFlags(args);
    var task = flags.GetValueOrDefault("task") ?? "ambient_sfx";
    Console.WriteLine($"Prompts for task={task}:");
    foreach (var id in PromptStore.ListPromptIds(paths, task))
    {
        try
        {
            var p = PromptStore.Load(paths, task, id);
            Console.WriteLine($"  {id}  hash={p.Hash}  {p.Label}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {id}  ({ex.Message})");
        }
    }
    return 0;
}
