using System.Text.Json;
using System.Text.RegularExpressions;
using FilmStudio.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

/// <summary>
/// Book chunks → Grok chat → scenes.json + normalize.
/// </summary>
public sealed class Stage1Service
{
    private static readonly JsonSerializerOptions JsonWrite = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ProjectStore _projects;
    private readonly GrokChatClient _chat;
    private readonly BookPrepareService _books;
    private readonly CharacterBookPlateService _plates;
    private readonly FilmStudioOptions _opts;
    private readonly ILogger<Stage1Service> _log;

    public Stage1Service(
        ProjectStore projects,
        GrokChatClient chat,
        BookPrepareService books,
        CharacterBookPlateService plates,
        IOptions<FilmStudioOptions> opts,
        ILogger<Stage1Service> log)
    {
        _projects = projects;
        _chat = chat;
        _books = books;
        _plates = plates;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<Stage1Result> RunAsync(
        string projectId,
        int chunkPages = 10,
        int? totalMinutes = null,
        string model = "grok-4.5",
        bool resume = false,
        int maxChunks = 0,
        double temperature = 0.2,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!_chat.IsConfigured)
            throw new InvalidOperationException("XAI_API_KEY is not set (required for Stage 1 LLM).");

        var projectDir = _projects.GetProjectDir(projectId);
        var bookPath = Path.Combine(projectDir, "source", "book_full.txt");
        var outPath = _projects.ResolveScenesJsonPath(projectId);

        onProgress?.Invoke("Checking book text…");
        if (!File.Exists(bookPath))
        {
            onProgress?.Invoke("No book_full.txt — running book prepare…");
            var prep = await _books.PrepareAsync(projectId, forceExtract: true, forceVision: false,
                autoVision: true, visionModel: model, onProgress: onProgress, ct: ct);
            if (!prep.ReadyForStage1)
                throw new InvalidOperationException(
                    prep.StrategyReason ?? "Book text is not ready for Stage 1. Run Prepare book first.");
        }

        var book = await File.ReadAllTextAsync(bookPath, ct);
        var analysis = BookTextAnalyzer.Analyze(book);
        if (analysis.TextQuality is "poor" or "empty" || analysis.GarbageScore >= 0.45)
            throw new InvalidOperationException(
                "book_full.txt is still garbled OCR. Run Prepare book with Grok vision first.");

        var minutes = totalMinutes is > 0
            ? Math.Clamp(totalMinutes.Value, 3, 180)
            : Math.Clamp(analysis.SuggestedTotalMinutes, 3, 180);
        onProgress?.Invoke(
            $"Target runtime {minutes} min (book_kind={analysis.BookKind}, words={analysis.TextWords})");

        var promptPath = Path.Combine(_projects.WorkspaceRoot, "prompts", "stage1_scene_bible.txt");
        if (!File.Exists(promptPath))
            throw new InvalidOperationException($"Stage 1 prompt not found: {promptPath}");
        var systemPrompt = (await File.ReadAllTextAsync(promptPath, ct))
            .Replace("{{TOTAL_RUNTIME_MINUTES}}", minutes.ToString());

        var chunks = ChunkBookByPages(book, Math.Clamp(chunkPages, 5, 30));
        if (maxChunks > 0)
            chunks = chunks.Take(maxChunks).ToList();
        onProgress?.Invoke($"Chunks: {chunks.Count} (pages/chunk≈{chunkPages})");

        Dictionary<string, object?>? partial = null;
        if (resume && File.Exists(outPath))
        {
            partial = GrokChatClient.ParseJsonObject(await File.ReadAllTextAsync(outPath, ct));
            onProgress?.Invoke(
                $"Resume from {Path.GetFileName(outPath)} ({CountScenes(partial)} scenes)");
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke($"Stage 1 chunk {i + 1}/{chunks.Count} model={model}…");
            int? resumeScene = null;
            if (partial is not null && CountScenes(partial) > 0)
                resumeScene = MaxSceneNumber(partial) + 1;

            var user = BuildUserMessage(
                chunks[i], i, chunks.Count, minutes, partial, resumeScene);

            var t0 = DateTime.UtcNow;
            string text;
            try
            {
                text = await _chat.CompleteAsync(systemPrompt, user, model, temperature, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Stage 1 chunk {Index} failed", i + 1);
                throw;
            }

            Dictionary<string, object?> parsed;
            try
            {
                parsed = GrokChatClient.ParseJsonObject(text);
            }
            catch (Exception ex)
            {
                var dump = Path.Combine(projectDir, $"stage1_raw_chunk_{i + 1}.txt");
                await File.WriteAllTextAsync(dump, text, ct);
                throw new InvalidOperationException(
                    $"Failed to parse JSON for chunk {i + 1}: {ex.Message}. Raw: {dump}", ex);
            }

            partial = MergeStage1(partial, parsed);
            var ck = Path.Combine(projectDir, $"nickandme.scenes.partial_chunk{i + 1}.json");
            await File.WriteAllTextAsync(ck, Serialize(partial), ct);
            var elapsed = (DateTime.UtcNow - t0).TotalSeconds;
            onProgress?.Invoke(
                $"chunk {i + 1} done in {elapsed:0.0}s · {CountScenes(partial)} scenes");
        }

        if (partial is null)
            throw new InvalidOperationException("No Stage 1 output produced");

        partial["schema_version"] = "stage1.v1";
        ApplyDefaultTitles(partial, projectId, projectDir);
        partial["generation"] = new Dictionary<string, object?>
        {
            ["method"] = "Stage1Service (C#)",
            ["model"] = model,
            ["book"] = bookPath,
            ["chunk_pages"] = chunkPages,
            ["chunks"] = chunks.Count,
            ["ts"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        };

        if (File.Exists(outPath))
        {
            var bak = outPath + $".bak_stage1_{DateTime.Now:yyyyMMdd_HHmmss}";
            File.Copy(outPath, bak, overwrite: true);
            onProgress?.Invoke($"Backup {Path.GetFileName(bak)}");
        }

        onProgress?.Invoke("Normalizing Stage 1…");
        partial = Stage1Normalizer.Normalize(partial);
        await File.WriteAllTextAsync(outPath, Serialize(partial), ct);

        var errs = ValidateStage1(partial);
        var hard = errs.Where(e => !e.StartsWith("(schema", StringComparison.Ordinal)).ToList();
        onProgress?.Invoke($"{errs.Count} verify issue(s); hard={hard.Count}");

        // Flexible seed pipeline: attach book plate candidates (not locks) for Characters UI / Grok
        try
        {
            onProgress?.Invoke("Attaching book plate candidates to character seeds…");
            // Fresh Stage 1: Grok-vision sort of book images → character seeds (cancellable via Stage1 ct)
            onProgress?.Invoke("Sorting book images onto characters (Grok vision when available)…");
            var plates = await _plates.AttachAsync(
                projectId,
                force: true,
                copyIntoAssets: true,
                useGrok: true,
                onProgress: onProgress,
                ct: ct);
            if (plates.Ok)
                onProgress?.Invoke(
                    $"Book plates ({plates.Method}): updated={plates.CharactersUpdated} " +
                    $"skipped={plates.CharactersSkipped} classified={plates.ImagesClassified}");
            else
                onProgress?.Invoke($"Book plate attach skipped: {plates.Reason}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Book plate attach after Stage 1 failed");
            onProgress?.Invoke($"Book plate attach failed (non-fatal): {ex.Message}");
        }

        var gpv = GetDict(partial, "global_production_variables");
        // Reload scene counts from disk if attach rewrote seeds (same file)
        var result = new Stage1Result
        {
            Ok = hard.Count == 0,
            OutPath = outPath,
            SceneCount = CountScenes(partial),
            CharacterCount = GetDict(gpv, "character_seed_tokens").Count,
            LocationCount = GetDict(gpv, "location_seed_tokens").Count,
            RuntimeSeconds = ToInt(partial.TryGetValue("cumulative_duration_target_seconds", out var rt) ? rt : 0),
            TotalMinutes = minutes,
            VerifyErrors = errs,
            HardErrors = hard,
        };
        onProgress?.Invoke(
            $"Wrote {Path.GetFileName(outPath)} · {result.SceneCount} scenes · " +
            $"{result.CharacterCount} chars · {result.LocationCount} locs");
        return result;
    }

    private static List<string> ChunkBookByPages(string book, int pagesPerChunk)
    {
        var parts = Regex.Split(book, @"(?=--- PAGE \d+ ---)")
            .Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (parts.Count == 0)
        {
            const int size = 12000;
            var chunks = new List<string>();
            for (var i = 0; i < book.Length; i += size)
                chunks.Add(book.Substring(i, Math.Min(size, book.Length - i)));
            return chunks;
        }
        var result = new List<string>();
        for (var i = 0; i < parts.Count; i += pagesPerChunk)
            result.Add(string.Concat(parts.Skip(i).Take(pagesPerChunk)).Trim());
        return result;
    }

    private static string BuildUserMessage(
        string bookChunk,
        int chunkIndex,
        int chunkTotal,
        int totalMinutes,
        Dictionary<string, object?>? prior,
        int? resumeScene)
    {
        var lines = new List<string>
        {
            $"TOTAL_RUNTIME_MINUTES = {totalMinutes}",
            $"BOOK_CHUNK {chunkIndex + 1}/{chunkTotal}",
            "",
            "Return ONLY valid Stage 1 JSON (schema_version stage1.v1).",
            "Include location_seed_tokens and per-scene location_ids[] as required by the system prompt.",
            "Phase 1: multi-place scenes may list multiple location_ids; do not invent plot to force splits.",
            "Do NOT emit veo_clips, visual_prompt, timestamps, or continuation flags.",
            "",
            "HARD TYPE REMINDERS:",
            "- story_day must be a STRING (e.g. \"Day 1\"), never a number",
            "- location_type ONLY: int | ext | mixed | flashback | dream | montage",
            "- frame_rate integer 24 (not \"24fps\")",
            "- music_intent.style_description required string on every scene",
            "- source_excerpts objects {source, excerpt} only — or omit",
            "- omit optional keys instead of null",
            "- always include full global_production_variables required fields",
            "",
        };

        if (prior is not null && resumeScene is int rs)
        {
            var scenes = GetScenes(prior);
            var tail = scenes.TakeLast(3).ToList();
            var priorSlim = new Dictionary<string, object?>
            {
                ["schema_version"] = prior.TryGetValue("schema_version", out var sv) ? sv : null,
                ["movie_title"] = prior.TryGetValue("movie_title", out var mt) ? mt : null,
                ["global_production_variables"] =
                    prior.TryGetValue("global_production_variables", out var gpv) ? gpv : null,
                ["scene_count"] = scenes.Count,
                ["last_scene_number"] = scenes.Count == 0 ? 0 : MaxSceneNumber(prior),
                ["scenes_tail"] = tail,
            };
            var priorJson = JsonSerializer.Serialize(priorSlim, JsonWrite);
            if (priorJson.Length > 80000)
                priorJson = priorJson[..80000];
            lines.AddRange(new[]
            {
                $"RESUME: Continue from scene_number >= {rs}.",
                "Copy character_seed_tokens and location_seed_tokens from PRIOR_PARTIAL (extend if new people/places appear).",
                "Return a FULL Stage 1 document that includes ALL prior scenes plus new ones for this chunk,",
                "OR return only NEW scenes in scenes[] with next_scene_number set — prefer FULL merged document.",
                "",
                "PRIOR_PARTIAL_JSON:",
                priorJson,
                "",
            });
        }

        lines.Add("BOOK_TEXT:");
        lines.Add(bookChunk);
        return string.Join("\n", lines);
    }

    private static Dictionary<string, object?> MergeStage1(
        Dictionary<string, object?>? baseDoc,
        Dictionary<string, object?> neu)
    {
        if (baseDoc is null) return neu;
        var outDoc = Clone(baseDoc);
        var gpv = GetDict(outDoc, "global_production_variables");
        var ng = GetDict(neu, "global_production_variables");
        foreach (var key in new[] { "character_seed_tokens", "location_seed_tokens" })
        {
            var oldS = GetDict(gpv, key);
            var newS = GetDict(ng, key);
            foreach (var (k, v) in newS)
                oldS[k] = v;
            gpv[key] = oldS;
        }
        foreach (var (k, v) in ng)
        {
            if (k is "character_seed_tokens" or "location_seed_tokens") continue;
            if (!gpv.ContainsKey(k))
                gpv[k] = v;
        }
        outDoc["global_production_variables"] = gpv;

        var byN = new Dictionary<int, Dictionary<string, object?>>();
        foreach (var s in GetScenes(outDoc))
        {
            var n = ToInt(s.TryGetValue("scene_number", out var sn) ? sn : 0);
            if (n > 0) byN[n] = s;
        }
        foreach (var s in GetScenes(neu))
        {
            var n = ToInt(s.TryGetValue("scene_number", out var sn) ? sn : 0);
            if (n > 0) byN[n] = s;
        }
        outDoc["scenes"] = byN.OrderBy(kv => kv.Key).Select(kv => (object?)kv.Value).ToList();
        if (neu.TryGetValue("movie_title", out var mt) && mt is not null)
            outDoc["movie_title"] = mt;
        if (neu.TryGetValue("source_book_title", out var sbt) && sbt is not null)
            outDoc["source_book_title"] = sbt;
        if (neu.TryGetValue("adaptation_notes", out var an) && an is not null)
            outDoc["adaptation_notes"] = an;

        var total = GetScenes(outDoc)
            .Sum(s => ToInt(s.TryGetValue("duration_target_seconds", out var d) ? d : 0));
        outDoc["cumulative_duration_target_seconds"] = total;
        if (neu.TryGetValue("next_scene_number", out var nsn))
            outDoc["next_scene_number"] = nsn;
        outDoc["schema_version"] = "stage1.v1";
        return outDoc;
    }

    private static void ApplyDefaultTitles(
        Dictionary<string, object?> doc,
        string projectId,
        string projectDir)
    {
        var defaultTitle = projectId;
        var metaPath = Path.Combine(projectDir, "project.json");
        if (File.Exists(metaPath))
        {
            try
            {
                using var meta = JsonDocument.Parse(File.ReadAllText(metaPath));
                if (meta.RootElement.TryGetProperty("title", out var t) &&
                    t.GetString() is { Length: > 0 } title)
                    defaultTitle = title.Trim();
            }
            catch { /* ignore */ }
        }
        var mt = (doc.TryGetValue("movie_title", out var mto) ? mto?.ToString() : null)?.Trim() ?? "";
        if (string.IsNullOrEmpty(mt) || mt is "Nick and Me" or "Untitled")
            doc["movie_title"] = defaultTitle;
        var sbt = (doc.TryGetValue("source_book_title", out var sbto) ? sbto?.ToString() : null)?.Trim() ?? "";
        if (string.IsNullOrEmpty(sbt) || sbt == "Nick and Me")
            doc["source_book_title"] = doc["movie_title"] ?? defaultTitle;
    }

    private static List<string> ValidateStage1(Dictionary<string, object?> data)
    {
        var errs = new List<string>();
        if (!string.Equals(data.TryGetValue("schema_version", out var sv) ? sv?.ToString() : null,
                "stage1.v1", StringComparison.Ordinal))
            errs.Add($"schema_version={sv} expected stage1.v1");
        var scenes = GetScenes(data);
        if (scenes.Count == 0)
            errs.Add("missing/empty scenes[]");
        var gpv = GetDict(data, "global_production_variables");
        if (GetDict(gpv, "character_seed_tokens").Count == 0)
            errs.Add("missing character_seed_tokens");
        var locSeeds = GetDict(gpv, "location_seed_tokens");
        var nums = new List<int>();
        foreach (var s in scenes)
        {
            var sn = ToInt(s.TryGetValue("scene_number", out var n) ? n : 0);
            nums.Add(sn);
            if (GetList(s, "story_beats").Count == 0)
                errs.Add($"S{sn}: no story_beats");
            if (!s.ContainsKey("setting"))
                errs.Add($"S{sn}: no setting");
            if (s.ContainsKey("veo_clips"))
                errs.Add($"S{sn}: has veo_clips (Stage 2 leak)");
            var lids = GetList(s, "location_ids").Select(x => x?.ToString() ?? "").Where(x => x.Length > 0);
            foreach (var lid in lids)
            {
                if (locSeeds.Count > 0 && !locSeeds.ContainsKey(lid) && lid != "Loc_Unknown")
                    errs.Add($"S{sn}: location_id {lid} not in location_seed_tokens");
            }
        }
        if (nums.Count > 0)
        {
            var sorted = nums.Where(n => n > 0).OrderBy(n => n).ToList();
            if (sorted.Count > 0 &&
                !sorted.SequenceEqual(Enumerable.Range(sorted[0], sorted[^1] - sorted[0] + 1)))
                errs.Add($"scene_number gaps or non-contiguous: {string.Join(",", sorted.Take(20))}…");
        }
        return errs;
    }

    private static string Serialize(Dictionary<string, object?> data) =>
        JsonSerializer.Serialize(data, JsonWrite) + "\n";

    private static Dictionary<string, object?> Clone(Dictionary<string, object?> d) =>
        GrokChatClient.ParseJsonObject(JsonSerializer.Serialize(d));

    private static List<Dictionary<string, object?>> GetScenes(Dictionary<string, object?> d) =>
        GetList(d, "scenes").OfType<Dictionary<string, object?>>().ToList();

    private static int CountScenes(Dictionary<string, object?> d) => GetScenes(d).Count;

    private static int MaxSceneNumber(Dictionary<string, object?> d) =>
        GetScenes(d).Select(s => ToInt(s.TryGetValue("scene_number", out var n) ? n : 0)).DefaultIfEmpty(0).Max();

    private static Dictionary<string, object?> GetDict(Dictionary<string, object?> d, string key)
    {
        if (d.TryGetValue(key, out var v) && v is Dictionary<string, object?> existing)
            return existing;
        return new Dictionary<string, object?>();
    }

    private static List<object?> GetList(Dictionary<string, object?> d, string key)
    {
        if (d.TryGetValue(key, out var v) && v is List<object?> list) return list;
        return new List<object?>();
    }

    private static int ToInt(object? v) => v switch
    {
        null => 0,
        int i => i,
        long l => (int)l,
        double d => (int)d,
        string s when int.TryParse(s, out var n) => n,
        _ => 0,
    };
}

public sealed class Stage1Result
{
    public bool Ok { get; set; }
    public string OutPath { get; set; } = "";
    public int SceneCount { get; set; }
    public int CharacterCount { get; set; }
    public int LocationCount { get; set; }
    public int RuntimeSeconds { get; set; }
    public int TotalMinutes { get; set; }
    public List<string> VerifyErrors { get; set; } = new();
    public List<string> HardErrors { get; set; } = new();
}
