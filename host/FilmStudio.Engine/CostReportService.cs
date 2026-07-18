using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FilmStudio.Core.Models;

namespace FilmStudio.Engine;

/// <summary>
/// Cost ledger (pipeline_state.cost_ledger) + planning estimates from blueprint + list rates.
/// Cost estimates + ledger aggregation (list-rate estimates, not xAI invoices).
/// </summary>
public sealed class CostReportService
{
    private static readonly Regex TimestampDur = new(
        @"^\s*(\d+):(\d{2})\s*-\s*(\d+):(\d{2})\s*$",
        RegexOptions.Compiled);

    private readonly ProjectStore _projects;

    public CostReportService(ProjectStore projects) => _projects = projects;

    public CostReport GetReport(
        string projectId,
        string? draftResolution = null,
        string? heroResolution = null,
        double? assumeAvgRetries = null,
        int recentLimit = 40) =>
        GetReportAsync(projectId, draftResolution, heroResolution, assumeAvgRetries, recentLimit)
            .GetAwaiter().GetResult();

    public async Task<CostReport> GetReportAsync(
        string projectId,
        string? draftResolution = null,
        string? heroResolution = null,
        double? assumeAvgRetries = null,
        int recentLimit = 40,
        CancellationToken ct = default)
    {
        var cfg = await LoadConfigMapAsync(projectId, ct).ConfigureAwait(false);
        var rates = RatesFromConfig(cfg);
        var draftRes = draftResolution
            ?? GetStr(cfg, "resolution", "480p");
        var heroRes = heroResolution ?? "720p";
        var retries = assumeAvgRetries
            ?? GetDouble(rates, "assume_avg_retries", 0);

        var ledger = await GetCostLedgerAsync(projectId, ct).ConfigureAwait(false);
        var actual = SummarizeLedger(ledger);

        var blueprintClips = await LoadBlueprintClipsAsync(projectId, ct).ConfigureAwait(false);
        var onDisk = IndexOnDiskClips(projectId);
        var heroes = await LoadHeroMapAsync(projectId, ct).ConfigureAwait(false);

        var draftCfg = CloneCfg(cfg, draftRes, retries);
        var heroCfg = CloneCfg(cfg, heroRes, retries);
        var draftRates = RatesFromConfig(draftCfg);
        var heroRates = RatesFromConfig(heroCfg);

        double spent = 0, remainingDraft = 0, remainingHero = 0, allDraft = 0, allHero = 0;
        int clipsOnDisk = 0, clipsMissing = 0, clipsTotal = 0;
        double secOnDisk = 0, secMissing = 0;
        var rows = new List<CostSceneRow>();

        foreach (var scene in blueprintClips)
        {
            var sn = scene.SceneNumber;
            var diskMap = onDisk.GetValueOrDefault(sn) ?? new Dictionary<int, bool>();
            heroes.TryGetValue(sn, out var heroResForScene);
            var isHero = !string.IsNullOrEmpty(heroResForScene);

            double sSpent = 0, sMiss = 0, sHero = 0, sAllD = 0, sAllH = 0;
            double dOn = 0, dMiss = 0;
            int nDisk = 0, nMiss = 0, nAll = 0;

            foreach (var clip in scene.Clips)
            {
                nAll++;
                var on = diskMap.GetValueOrDefault(clip.ClipNumber);
                if (on) nDisk++;
                else nMiss++;

                var spentRes = isHero ? (heroResForScene ?? heroRes) : draftRes;
                var spentEst = EstimateClip(clip, spentRes, rates, retries);
                var missEst = EstimateClip(clip, draftRes, draftRates, retries);
                var heroEst = EstimateClip(clip, heroRes, heroRates, retries);
                var allD = EstimateClip(clip, draftRes, draftRates, retries);
                var allH = EstimateClip(clip, heroRes, heroRates, retries);

                sAllD += allD.Usd;
                sAllH += allH.Usd;
                if (on)
                {
                    sSpent += spentEst.Usd;
                    dOn += spentEst.DurationSec;
                    if (!isHero)
                        sHero += heroEst.Usd;
                }
                else
                {
                    sMiss += missEst.Usd;
                    dMiss += missEst.DurationSec;
                }
            }

            spent += sSpent;
            remainingDraft += sMiss;
            remainingHero += sHero;
            allDraft += sAllD;
            allHero += sAllH;
            clipsOnDisk += nDisk;
            clipsMissing += nMiss;
            clipsTotal += nAll;
            secOnDisk += dOn;
            secMissing += dMiss;

            actual.ByScene.TryGetValue(sn.ToString(CultureInfo.InvariantCulture), out var actualScene);

            rows.Add(new CostSceneRow
            {
                Scene = sn,
                Setting = scene.Setting.Length > 60 ? scene.Setting[..60] : scene.Setting,
                ClipsTotal = nAll,
                ClipsOnDisk = nDisk,
                ClipsMissing = nMiss,
                IsHero = isHero,
                HeroResolution = heroResForScene,
                CharactersOnScreen = scene.CharactersOnScreen,
                LocationIds = scene.LocationIds,
                PrimaryLocationId = scene.PrimaryLocationId,
                SpentUsd = Math.Round(sSpent, 2),
                ActualUsd = Math.Round(actualScene, 2),
                RemainingDraftUsd = Math.Round(sMiss, 2),
                HeroUpgradeUsd = Math.Round(sHero, 2),
                AllDraftUsd = Math.Round(sAllD, 2),
                AllHeroUsd = Math.Round(sAllH, 2),
                DurationOnDiskSec = Math.Round(dOn, 1),
                DurationMissingSec = Math.Round(dMiss, 1),
            });
        }

        rows.Sort((a, b) => a.Scene.CompareTo(b.Scene));

        var scenarios = BuildScenarios(blueprintClips, onDisk, cfg, rates, retries, draftRes, heroRes);

        return new CostReport
        {
            ProjectId = projectId,
            DraftResolution = draftRes,
            HeroResolution = heroRes,
            ModelName = GetStr(cfg, "model_name", "grok-imagine-video"),
            VideoProvider = GetStr(cfg, "video_provider", "grok"),
            OutputRateDraft = OutputRate(draftRes, draftRates),
            OutputRateHero = OutputRate(heroRes, heroRates),
            AssumeAvgRetries = retries,
            Summary = new CostReportSummary
            {
                ClipsTotal = clipsTotal,
                ClipsOnDisk = clipsOnDisk,
                ClipsMissing = clipsMissing,
                SecOnDisk = Math.Round(secOnDisk, 1),
                SecMissing = Math.Round(secMissing, 1),
                SpentUsd = Math.Round(spent, 2),
                ActualUsd = actual.ActualUsd,
                ActualEvents = actual.EventCount,
                ActualVideoJobs = actual.VideoJobs,
                ActualVideoSec = actual.VideoSec,
                RemainingFirstPassUsd = Math.Round(remainingDraft, 2),
                RemainingHeroUpgradeUsd = Math.Round(remainingHero, 2),
                FinishDraftUsd = Math.Round(spent + remainingDraft, 2),
                FinishDraftPlusHeroUsd = Math.Round(spent + remainingDraft + remainingHero, 2),
                FinishFromActualUsd = Math.Round(actual.ActualUsd + remainingDraft, 2),
                FullFilmAllDraftUsd = Math.Round(allDraft, 2),
                FullFilmAllHeroUsd = Math.Round(allHero, 2),
                ScenesWithMedia = rows.Count(r => r.ClipsOnDisk > 0),
                ScenesHero = rows.Count(r => r.IsHero),
                ScenesTotal = rows.Count,
            },
            Actual = actual,
            Scenes = rows,
            Scenarios = scenarios,
            RecentEvents = ledger
                .OrderByDescending(e => e.Ts ?? "")
                .Take(Math.Clamp(recentLimit, 1, 200))
                .ToList(),
            Notes =
                "Estimates = planning (current rates × scope). " +
                "Actual = cost_ledger at list rates when each job completed (not xAI invoice). " +
                "Backfill historical clips if actual looks low.",
        };
    }

    public CostBackfillResult BackfillFromDisk(string projectId, bool onlyMissing = true) =>
        BackfillFromDiskAsync(projectId, onlyMissing).GetAwaiter().GetResult();

    public async Task<CostBackfillResult> BackfillFromDiskAsync(
        string projectId,
        bool onlyMissing = true,
        CancellationToken ct = default)
    {
        var cfg = await LoadConfigMapAsync(projectId, ct).ConfigureAwait(false);
        var rates = RatesFromConfig(cfg);
        var ledger = await GetCostLedgerRawAsync(projectId, ct).ConfigureAwait(false);
        var seen = new HashSet<(int, int)>();
        foreach (var e in ledger)
        {
            if (!string.Equals(GetRawKind(e), "video", StringComparison.OrdinalIgnoreCase))
                continue;
            if (TryGetInt(e, "scene", out var sn) && TryGetInt(e, "clip", out var cn))
                seen.Add((sn, cn));
        }

        var blueprint = await LoadBlueprintClipsAsync(projectId, ct).ConfigureAwait(false);
        var onDisk = IndexOnDiskClips(projectId);
        var clipJobs = await LoadClipJobsAsync(projectId, ct).ConfigureAwait(false);
        var defaultRes = GetStr(cfg, "resolution", "480p");
        var defaultModel = GetStr(cfg, "model_name", "grok-imagine-video");
        var defaultDur = GetDouble(cfg, "duration_seconds", 8);
        var assumeRef = GetBool(rates, "assume_ref_image_per_clip", true);

        var added = 0;
        var skipped = 0;
        foreach (var scene in blueprint)
        {
            var diskMap = onDisk.GetValueOrDefault(scene.SceneNumber) ?? new Dictionary<int, bool>();
            foreach (var clip in scene.Clips)
            {
                if (!diskMap.GetValueOrDefault(clip.ClipNumber))
                {
                    skipped++;
                    continue;
                }

                if (onlyMissing && seen.Contains((scene.SceneNumber, clip.ClipNumber)))
                {
                    skipped++;
                    continue;
                }

                clipJobs.TryGetValue($"{scene.SceneNumber}_{clip.ClipNumber}", out var job);
                var duration = clip.DurationSec > 0 ? clip.DurationSec : defaultDur;
                if (job is not null && job.TryGetValue("duration_sec", out var ds) &&
                    ds.TryGetDouble(out var jdur) && jdur > 0)
                    duration = jdur;

                var res = defaultRes;
                if (job is not null && job.TryGetValue("resolution", out var jr) &&
                    jr.ValueKind == JsonValueKind.String && jr.GetString() is { Length: > 0 } rs)
                    res = rs;

                var model = defaultModel;
                if (job is not null && job.TryGetValue("model", out var jm) &&
                    jm.ValueKind == JsonValueKind.String && jm.GetString() is { Length: > 0 } md)
                    model = md;

                var isExtend = string.Equals(
                    clip.Continuation, "extend_previous", StringComparison.OrdinalIgnoreCase);
                var priced = PriceVideo(duration, res, rates, assumeRef, isExtend, attempts: 1);

                var evt = new Dictionary<string, object?>
                {
                    ["kind"] = "video",
                    ["scene"] = scene.SceneNumber,
                    ["clip"] = clip.ClipNumber,
                    ["model"] = model,
                    ["request_id"] = job is not null && job.TryGetValue("request_id", out var rid)
                        ? rid.GetString() ?? ""
                        : "",
                    ["has_ref_image"] = assumeRef,
                    ["is_extend"] = isExtend,
                    ["source"] = "backfill",
                    ["duration_sec"] = priced.DurationSec,
                    ["attempts"] = 1.0,
                    ["resolution"] = res,
                    ["output_rate_per_sec"] = priced.RatePerSec,
                    ["video_output_usd"] = priced.VideoOut,
                    ["ref_image_usd"] = priced.RefImg,
                    ["extend_input_usd"] = priced.ExtendIn,
                    ["usd"] = priced.Usd,
                    ["currency"] = "USD",
                    ["extra"] = new Dictionary<string, object?> { ["backfill"] = true },
                };
                await AppendCostEventAsync(projectId, evt, save: true, ct).ConfigureAwait(false);
                seen.Add((scene.SceneNumber, clip.ClipNumber));
                added++;
            }
        }

        var summary = SummarizeLedger(await GetCostLedgerAsync(projectId, ct).ConfigureAwait(false));
        return new CostBackfillResult
        {
            Added = added,
            Skipped = skipped,
            LedgerEvents = summary.EventCount,
            ActualUsd = summary.ActualUsd,
        };
    }

    /// <summary>Record a completed native C# video gen at list rates.</summary>
    public void RecordVideoGeneration(
        string projectId,
        int scene,
        int clip,
        double durationSec,
        string resolution,
        string model,
        bool hasRefImage = false,
        bool isExtend = false,
        string? requestId = null)
    {
        RecordVideoGenerationAsync(
            projectId, scene, clip, durationSec, resolution, model, hasRefImage, isExtend, requestId)
            .GetAwaiter().GetResult();
    }

    public async Task RecordVideoGenerationAsync(
        string projectId,
        int scene,
        int clip,
        double durationSec,
        string resolution,
        string model,
        bool hasRefImage = false,
        bool isExtend = false,
        string? requestId = null,
        CancellationToken ct = default)
    {
        var cfg = await LoadConfigMapAsync(projectId, ct).ConfigureAwait(false);
        var rates = RatesFromConfig(cfg);
        var priced = PriceVideo(durationSec, resolution, rates, hasRefImage, isExtend, 1);
        await AppendCostEventAsync(projectId, new Dictionary<string, object?>
        {
            ["kind"] = "video",
            ["scene"] = scene,
            ["clip"] = clip,
            ["model"] = model,
            ["request_id"] = requestId ?? "",
            ["has_ref_image"] = hasRefImage,
            ["is_extend"] = isExtend,
            ["source"] = "list_rate",
            ["duration_sec"] = priced.DurationSec,
            ["attempts"] = 1.0,
            ["resolution"] = resolution,
            ["output_rate_per_sec"] = priced.RatePerSec,
            ["video_output_usd"] = priced.VideoOut,
            ["ref_image_usd"] = priced.RefImg,
            ["extend_input_usd"] = priced.ExtendIn,
            ["usd"] = priced.Usd,
            ["currency"] = "USD",
        }, save: true, ct).ConfigureAwait(false);
    }

    public IReadOnlyList<CostEvent> GetCostLedger(string projectId) =>
        GetCostLedgerAsync(projectId).GetAwaiter().GetResult();

    public async Task<IReadOnlyList<CostEvent>> GetCostLedgerAsync(
        string projectId,
        CancellationToken ct = default)
    {
        var raw = await GetCostLedgerRawAsync(projectId, ct).ConfigureAwait(false);
        var list = new List<CostEvent>();
        foreach (var e in raw)
            list.Add(ParseEvent(e));
        return list;
    }

    /// <summary>Record a completed image generation at list rates (character design).</summary>
    public void RecordImageGeneration(
        string projectId,
        int nImages,
        string model,
        bool quality = true,
        string? character = null) =>
        RecordImageGenerationAsync(projectId, nImages, model, quality, character)
            .GetAwaiter().GetResult();

    public async Task RecordImageGenerationAsync(
        string projectId,
        int nImages,
        string model,
        bool quality = true,
        string? character = null,
        CancellationToken ct = default)
    {
        var cfg = await LoadConfigMapAsync(projectId, ct).ConfigureAwait(false);
        var rates = RatesFromConfig(cfg);
        var n = Math.Max(0, nImages);
        var unit = GetDouble(
            rates,
            quality ? "image_output_quality" : "image_output_standard",
            quality ? 0.05 : 0.02);
        var usd = Math.Round(unit * n, 4);
        await AppendCostEventAsync(projectId, new Dictionary<string, object?>
        {
            ["kind"] = "image",
            ["model"] = model,
            ["character"] = character ?? "",
            ["n_images"] = n,
            ["unit_usd"] = unit,
            ["usd"] = usd,
            ["currency"] = "USD",
            ["source"] = "list_rate",
        }, save: true, ct).ConfigureAwait(false);
    }

    // ---- internals ----

    private List<CostScenarioRow> BuildScenarios(
        List<BlueprintSceneClips> scenes,
        Dictionary<int, Dictionary<int, bool>> onDisk,
        Dictionary<string, JsonElement> cfg,
        Dictionary<string, object?> baseRates,
        double retries,
        string draftRes,
        string heroRes)
    {
        var model = GetStr(cfg, "model_name", "grok-imagine-video");
        var rows = new List<CostScenarioRow>();
        foreach (var res in new[] { "480p", "720p", "1080p" })
        {
            var rates = RatesFromConfig(CloneCfg(cfg, res, retries));
            double full = 0, missing = 0, regen = 0;
            foreach (var scene in scenes)
            {
                var disk = onDisk.GetValueOrDefault(scene.SceneNumber) ?? new Dictionary<int, bool>();
                foreach (var clip in scene.Clips)
                {
                    var est = EstimateClip(clip, res, rates, retries);
                    full += est.Usd;
                    if (disk.GetValueOrDefault(clip.ClipNumber))
                        regen += est.Usd;
                    else
                        missing += est.Usd;
                }
            }

            rows.Add(new CostScenarioRow
            {
                Label = $"{model} @ {res}",
                Resolution = res,
                ModelName = model,
                RatePerSec = OutputRate(res, rates),
                FullFilmUsd = Math.Round(full, 2),
                RemainingMissingUsd = Math.Round(missing, 2),
                RegenOnDiskUsd = Math.Round(regen, 2),
                AssumeAvgRetries = retries,
            });
        }

        // highlight draft/hero even if already listed
        _ = draftRes;
        _ = heroRes;
        return rows;
    }

    private static (double Usd, double DurationSec) EstimateClip(
        BlueprintClip clip,
        string resolution,
        Dictionary<string, object?> rates,
        double retries)
    {
        var duration = clip.DurationSec > 0 ? clip.DurationSec : 8;
        var attempts = 1.0 + Math.Max(0, retries);
        var outRate = OutputRate(resolution, rates);
        var videoOut = duration * outRate * attempts;
        var refImg = 0.0;
        if (GetBool(rates, "assume_ref_image_per_clip", true))
            refImg = GetDouble(rates, "video_input_image", 0.002) * attempts;
        var extend = 0.0;
        if (string.Equals(clip.Continuation, "extend_previous", StringComparison.OrdinalIgnoreCase))
            extend = duration * GetDouble(rates, "video_input_per_sec", 0.01) * attempts;
        return (videoOut + refImg + extend, duration);
    }

    private static (double Usd, double DurationSec, double RatePerSec, double VideoOut, double RefImg, double ExtendIn)
        PriceVideo(
            double durationSec,
            string resolution,
            Dictionary<string, object?> rates,
            bool hasRef,
            bool isExtend,
            double attempts)
    {
        var duration = Math.Max(0, durationSec);
        attempts = Math.Max(1, attempts);
        var outRate = OutputRate(resolution, rates);
        var videoOut = duration * outRate * attempts;
        var refImg = hasRef ? GetDouble(rates, "video_input_image", 0.002) * attempts : 0;
        var extend = isExtend
            ? duration * GetDouble(rates, "video_input_per_sec", 0.01) * attempts
            : 0;
        var usd = Math.Round(videoOut + refImg + extend, 4);
        return (usd, duration, outRate, Math.Round(videoOut, 4), Math.Round(refImg, 4), Math.Round(extend, 4));
    }

    private static CostLedgerSummary SummarizeLedger(IReadOnlyList<CostEvent> events)
    {
        double total = 0, videoSec = 0;
        var byKind = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var byScene = new Dictionary<string, double>(StringComparer.Ordinal);
        var byModel = new Dictionary<string, double>(StringComparer.Ordinal);
        var videoJobs = 0;
        var imageJobs = 0;
        foreach (var e in events)
        {
            total += e.Usd;
            var kind = string.IsNullOrEmpty(e.Kind) ? "other" : e.Kind;
            byKind[kind] = byKind.GetValueOrDefault(kind) + e.Usd;
            if (e.Scene is int sn)
            {
                var key = sn.ToString(CultureInfo.InvariantCulture);
                byScene[key] = byScene.GetValueOrDefault(key) + e.Usd;
            }
            if (!string.IsNullOrEmpty(e.Model))
                byModel[e.Model] = byModel.GetValueOrDefault(e.Model) + e.Usd;
            if (string.Equals(kind, "video", StringComparison.OrdinalIgnoreCase))
            {
                videoJobs++;
                videoSec += e.DurationSec ?? 0;
            }
            else if (string.Equals(kind, "image", StringComparison.OrdinalIgnoreCase))
            {
                imageJobs++;
            }
        }

        return new CostLedgerSummary
        {
            ActualUsd = Math.Round(total, 2),
            EventCount = events.Count,
            VideoJobs = videoJobs,
            ImageJobs = imageJobs,
            VideoSec = Math.Round(videoSec, 1),
            ByKind = byKind.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2)),
            ByScene = byScene.OrderBy(kv => int.TryParse(kv.Key, out var n) ? n : 0)
                .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2)),
            ByModel = byModel.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2)),
        };
    }

    private static CostEvent ParseEvent(JsonElement e)
    {
        return new CostEvent
        {
            Id = e.TryGetProperty("id", out var id) ? id.GetString() : null,
            Ts = e.TryGetProperty("ts", out var ts) ? ts.GetString() : null,
            Kind = e.TryGetProperty("kind", out var k) ? k.GetString() ?? "other" : "other",
            Scene = TryGetInt(e, "scene", out var sn) ? sn : null,
            Clip = TryGetInt(e, "clip", out var cn) ? cn : null,
            Model = e.TryGetProperty("model", out var m) ? m.GetString() : null,
            Resolution = e.TryGetProperty("resolution", out var r) ? r.GetString() : null,
            DurationSec = TryGetDouble(e, "duration_sec", out var d) ? d : null,
            Usd = TryGetDouble(e, "usd", out var u) ? u : 0,
            Currency = e.TryGetProperty("currency", out var c) ? c.GetString() ?? "USD" : "USD",
            Source = e.TryGetProperty("source", out var s) ? s.GetString() : null,
            Character = e.TryGetProperty("character", out var ch) ? ch.GetString() : null,
            OutputRatePerSec = TryGetDouble(e, "output_rate_per_sec", out var or) ? or : null,
            HasRefImage = e.TryGetProperty("has_ref_image", out var hr) &&
                          (hr.ValueKind is JsonValueKind.True or JsonValueKind.False)
                ? hr.GetBoolean()
                : null,
            IsExtend = e.TryGetProperty("is_extend", out var ie) &&
                       (ie.ValueKind is JsonValueKind.True or JsonValueKind.False)
                ? ie.GetBoolean()
                : null,
        };
    }

    private Task<List<JsonElement>> GetCostLedgerRawAsync(
        string projectId,
        CancellationToken ct = default) =>
        GetCostLedgerRawCoreAsync(projectId, ct);

    private async Task<List<JsonElement>> GetCostLedgerRawCoreAsync(
        string projectId,
        CancellationToken ct)
    {
        var path = await StatePathAsync(projectId, ct).ConfigureAwait(false);
        if (!File.Exists(path))
            return new List<JsonElement>();
        try
        {
            await using var stream = File.OpenRead(path);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                .ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("cost_ledger", out var ledger) ||
                ledger.ValueKind != JsonValueKind.Array)
                return new List<JsonElement>();
            return ledger.EnumerateArray().Select(x => x.Clone()).ToList();
        }
        catch
        {
            return new List<JsonElement>();
        }
    }

    private async Task AppendCostEventAsync(
        string projectId,
        Dictionary<string, object?> evt,
        bool save,
        CancellationToken ct = default)
    {
        var path = await StatePathAsync(projectId, ct).ConfigureAwait(false);

        JsonDocument rawDoc;
        if (File.Exists(path))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                rawDoc = JsonDocument.Parse(bytes);
            }
            catch
            {
                rawDoc = JsonDocument.Parse("{}");
            }
        }
        else
        {
            rawDoc = JsonDocument.Parse("{}");
        }

        using (rawDoc)
        {
            var ledgerList = new List<object?>();
            if (rawDoc.RootElement.TryGetProperty("cost_ledger", out var existing) &&
                existing.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in existing.EnumerateArray())
                    ledgerList.Add(JsonSerializer.Deserialize<object>(item.GetRawText()));
            }

            var ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            evt.TryAdd("id", $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{ledgerList.Count:D4}");
            evt.TryAdd("ts", ts);
            evt.TryAdd("currency", "USD");
            ledgerList.Add(evt);
            if (ledgerList.Count > 20000)
                ledgerList = ledgerList.TakeLast(20000).ToList();

            var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in rawDoc.RootElement.EnumerateObject())
            {
                if (p.Name is "cost_ledger" or "cost_totals")
                    continue;
                merged[p.Name] = JsonSerializer.Deserialize<object>(p.Value.GetRawText());
            }

            merged["cost_ledger"] = ledgerList;
            var prevUsd = 0.0;
            var prevEvents = 0;
            if (rawDoc.RootElement.TryGetProperty("cost_totals", out var tot) &&
                tot.ValueKind == JsonValueKind.Object)
            {
                if (tot.TryGetProperty("usd", out var u) && u.TryGetDouble(out var ud))
                    prevUsd = ud;
                if (tot.TryGetProperty("events", out var ev) && ev.TryGetInt32(out var en))
                    prevEvents = en;
            }

            var addUsd = 0.0;
            if (evt.TryGetValue("usd", out var usdObj) && usdObj is not null)
                addUsd = Convert.ToDouble(usdObj, CultureInfo.InvariantCulture);

            merged["cost_totals"] = new Dictionary<string, object?>
            {
                ["usd"] = Math.Round(prevUsd + addUsd, 4),
                ["events"] = prevEvents + 1,
                ["updated_at"] = ts,
            };

            if (save)
            {
                var json = JsonSerializer.Serialize(merged, JsonDefaults.Indented);
                await File.WriteAllTextAsync(path, json + "\n", ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<string> StatePathAsync(string projectId, CancellationToken ct)
    {
        var dir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var meta = Path.Combine(dir, "project.json");
        var name = "pipeline_state.json";
        if (File.Exists(meta))
        {
            try
            {
                await using var stream = File.OpenRead(meta);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                    .ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("state_file", out var sf) &&
                    sf.GetString() is { Length: > 0 } n)
                    name = n;
            }
            catch { /* ignore */ }
        }
        return Path.Combine(dir, name);
    }

    private async Task<Dictionary<string, JsonElement>> LoadConfigMapAsync(
        string projectId,
        CancellationToken ct)
    {
        var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        return new Dictionary<string, JsonElement>(cfg, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, JsonElement> CloneCfg(
        Dictionary<string, JsonElement> cfg,
        string resolution,
        double retries)
    {
        // We don't mutate JsonElements; rates use resolution + retries passed separately.
        _ = cfg;
        _ = resolution;
        _ = retries;
        return cfg;
    }

    private async Task<List<BlueprintSceneClips>> LoadBlueprintClipsAsync(
        string projectId,
        CancellationToken ct)
    {
        var list = new List<BlueprintSceneClips>();
        using var bp = await _projects.LoadBlueprintAsync(projectId, ct).ConfigureAwait(false);
        if (bp is null ||
            !bp.RootElement.TryGetProperty("scenes", out var scenes) ||
            scenes.ValueKind != JsonValueKind.Array)
            return list;

        var defaultDur = 8.0;
        var cfg = await LoadConfigMapAsync(projectId, ct).ConfigureAwait(false);
        defaultDur = GetDouble(cfg, "duration_seconds", 8);

        foreach (var s in scenes.EnumerateArray())
        {
            var sn = s.TryGetProperty("scene_number", out var sne) && sne.TryGetInt32(out var n) ? n : 0;
            var setting = s.TryGetProperty("setting", out var set) ? set.GetString() ?? "" : "";
            var clips = new List<BlueprintClip>();
            if (s.TryGetProperty("veo_clips", out var vc) && vc.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in vc.EnumerateArray())
                {
                    var cn = c.TryGetProperty("clip_number", out var cne) && cne.TryGetInt32(out var cv) ? cv : 0;
                    if (cn <= 0) continue;
                    var dur = defaultDur;
                    if (c.TryGetProperty("duration_seconds", out var d) && d.TryGetDouble(out var dd) && dd > 0)
                        dur = dd;
                    else if (c.TryGetProperty("duration_seconds", out var di) && di.TryGetInt32(out var idi) && idi > 0)
                        dur = idi;
                    else if (c.TryGetProperty("timestamp", out var ts) && ts.GetString() is { } tss)
                    {
                        var m = TimestampDur.Match(tss);
                        if (m.Success)
                        {
                            var a = int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value);
                            var b = int.Parse(m.Groups[3].Value) * 60 + int.Parse(m.Groups[4].Value);
                            if (b > a) dur = b - a;
                        }
                    }

                    clips.Add(new BlueprintClip
                    {
                        ClipNumber = cn,
                        DurationSec = dur,
                        Continuation = c.TryGetProperty("veo_continuation_source", out var cont)
                            ? cont.GetString() ?? "none"
                            : "none",
                    });
                }
            }

            var chars = new List<string>();
            if (s.TryGetProperty("characters_on_screen", out var cos) && cos.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in cos.EnumerateArray())
                {
                    var name = x.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        chars.Add(name!);
                }
            }

            var locs = new List<string>();
            if (s.TryGetProperty("location_ids", out var lids) && lids.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in lids.EnumerateArray())
                {
                    var name = x.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        locs.Add(name!);
                }
            }

            string? primaryLoc = null;
            if (s.TryGetProperty("primary_location_id", out var pl) &&
                pl.GetString() is { Length: > 0 } plId)
            {
                primaryLoc = plId;
                if (!locs.Contains(plId, StringComparer.OrdinalIgnoreCase))
                    locs.Insert(0, plId);
            }

            list.Add(new BlueprintSceneClips
            {
                SceneNumber = sn,
                Setting = setting,
                Clips = clips,
                CharactersOnScreen = chars,
                LocationIds = locs,
                PrimaryLocationId = primaryLoc,
            });
        }

        return list.OrderBy(x => x.SceneNumber).ToList();
    }

    private Dictionary<int, Dictionary<int, bool>> IndexOnDiskClips(string projectId)
    {
        var map = new Dictionary<int, Dictionary<int, bool>>();
        var videoDir = Path.Combine(_projects.GetProjectDir(projectId), "assets", "video");
        if (!Directory.Exists(videoDir))
            return map;
        try
        {
            foreach (var f in Directory.EnumerateFiles(videoDir, "scene_*_clip_*.mp4"))
            {
                var name = Path.GetFileName(f);
                // Exact scene_01_clip_02.mp4 only (not .native.mp4 sidecars)
                if (!FfmpegRemuxService.IsExactClipFileName(name)) continue;
                var stem = Path.GetFileNameWithoutExtension(name);
                var parts = stem.Split('_');
                if (parts.Length >= 4 &&
                    int.TryParse(parts[1], out var sn) &&
                    int.TryParse(parts[3], out var cn))
                {
                    try
                    {
                        if (new FileInfo(f).Length < 1024) continue;
                    }
                    catch { continue; }

                    if (!map.TryGetValue(sn, out var inner))
                    {
                        inner = new Dictionary<int, bool>();
                        map[sn] = inner;
                    }
                    inner[cn] = true;
                }
            }
        }
        catch { /* ignore */ }
        return map;
    }

    private async Task<Dictionary<int, string>> LoadHeroMapAsync(
        string projectId,
        CancellationToken ct)
    {
        var map = new Dictionary<int, string>();
        var path = await StatePathAsync(projectId, ct).ConfigureAwait(false);
        if (!File.Exists(path)) return map;
        try
        {
            await using var stream = File.OpenRead(path);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                .ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("scene_hero", out var hero) ||
                hero.ValueKind != JsonValueKind.Object)
                return map;
            foreach (var p in hero.EnumerateObject())
            {
                if (!int.TryParse(p.Name, out var sn)) continue;
                if (p.Value.ValueKind == JsonValueKind.Object &&
                    p.Value.TryGetProperty("resolution", out var r) &&
                    r.GetString() is { Length: > 0 } res)
                    map[sn] = res;
                else if (p.Value.ValueKind is JsonValueKind.True)
                    map[sn] = "720p";
            }
        }
        catch { /* ignore */ }
        return map;
    }

    private async Task<Dictionary<string, Dictionary<string, JsonElement>>> LoadClipJobsAsync(
        string projectId,
        CancellationToken ct)
    {
        var map = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
        var path = await StatePathAsync(projectId, ct).ConfigureAwait(false);
        if (!File.Exists(path)) return map;
        try
        {
            await using var stream = File.OpenRead(path);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                .ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("clip_jobs", out var jobs) ||
                jobs.ValueKind != JsonValueKind.Object)
                return map;
            foreach (var p in jobs.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object) continue;
                var inner = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var q in p.Value.EnumerateObject())
                    inner[q.Name] = q.Value.Clone();
                map[p.Name] = inner;
            }
        }
        catch { /* ignore */ }
        return map;
    }

    private static Dictionary<string, object?> RatesFromConfig(Dictionary<string, JsonElement> cfg)
    {
        var rates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["currency"] = "USD",
            ["video_output_per_sec"] = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["480p"] = 0.05,
                ["720p"] = 0.07,
                ["1080p"] = 0.25,
            },
            ["video_input_image"] = 0.002,
            ["video_input_per_sec"] = 0.01,
            ["image_output_quality"] = 0.05,
            ["image_output_standard"] = 0.02,
            ["assume_ref_image_per_clip"] = true,
            ["assume_extend_fraction"] = 0.0,
            ["assume_avg_retries"] = 0.0,
        };

        if (cfg.TryGetValue("cost_estimates", out var ce) && ce.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in ce.EnumerateObject())
            {
                if (p.NameEquals("video_output_per_sec") && p.Value.ValueKind == JsonValueKind.Object)
                {
                    var table = (Dictionary<string, double>)rates["video_output_per_sec"]!;
                    foreach (var r in p.Value.EnumerateObject())
                    {
                        if (r.Value.TryGetDouble(out var v))
                            table[r.Name] = v;
                    }
                }
                else if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var d))
                    rates[p.Name] = d;
                else if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    rates[p.Name] = p.Value.GetBoolean();
                else if (p.Value.ValueKind == JsonValueKind.String)
                    rates[p.Name] = p.Value.GetString();
            }
        }

        return rates;
    }

    private static double OutputRate(string resolution, Dictionary<string, object?> rates)
    {
        var res = (resolution ?? "720p").ToLowerInvariant().Trim();
        if (rates.TryGetValue("video_output_per_sec", out var t) &&
            t is Dictionary<string, double> table)
        {
            if (table.TryGetValue(res, out var r)) return r;
            if (table.TryGetValue("720p", out var d)) return d;
        }
        return 0.07;
    }

    private static string GetStr(Dictionary<string, JsonElement> cfg, string key, string fallback) =>
        cfg.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? fallback
            : fallback;

    private static double GetDouble(Dictionary<string, JsonElement> cfg, string key, double fallback) =>
        cfg.TryGetValue(key, out var el) && el.TryGetDouble(out var v) ? v : fallback;

    private static double GetDouble(Dictionary<string, object?> rates, string key, double fallback)
    {
        if (!rates.TryGetValue(key, out var v) || v is null) return fallback;
        return Convert.ToDouble(v, CultureInfo.InvariantCulture);
    }

    private static bool GetBool(Dictionary<string, object?> rates, string key, bool fallback)
    {
        if (!rates.TryGetValue(key, out var v) || v is null) return fallback;
        return v is bool b ? b : Convert.ToBoolean(v, CultureInfo.InvariantCulture);
    }

    private static string GetRawKind(JsonElement e) =>
        e.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";

    private static bool TryGetInt(JsonElement e, string name, out int v)
    {
        v = 0;
        if (!e.TryGetProperty(name, out var p)) return false;
        if (p.TryGetInt32(out v)) return true;
        if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out v)) return true;
        return false;
    }

    private static bool TryGetDouble(JsonElement e, string name, out double v)
    {
        v = 0;
        if (!e.TryGetProperty(name, out var p)) return false;
        if (p.TryGetDouble(out v)) return true;
        if (p.ValueKind == JsonValueKind.String &&
            double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            return true;
        return false;
    }

    private sealed class BlueprintSceneClips
    {
        public int SceneNumber { get; set; }
        public string Setting { get; set; } = "";
        public List<BlueprintClip> Clips { get; set; } = new();
        public List<string> CharactersOnScreen { get; set; } = new();
        public List<string> LocationIds { get; set; } = new();
        public string? PrimaryLocationId { get; set; }
    }

    private sealed class BlueprintClip
    {
        public int ClipNumber { get; set; }
        public double DurationSec { get; set; }
        public string Continuation { get; set; } = "none";
    }
}
