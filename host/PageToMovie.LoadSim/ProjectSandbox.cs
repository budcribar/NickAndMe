using System.Text.Json;
using System.Text.Json.Nodes;

namespace PageToMovie.LoadSim;

/// <summary>
/// Optional maintenance helper: (re)copy Buster → LoadSimBuster.
/// Normal LoadSim runs use the checked-in <c>projects/LoadSimBuster</c> and skip this.
/// </summary>
public static class ProjectSandbox
{
    public const string DefaultSourceId = "Buster";
    public const string DefaultSandboxId = "LoadSimBuster";

    private static readonly string[] SkipDirectoryNames =
    {
        "_review_1fps",
        "_review_5fps",
        "_preview_frames",
        ".git",
    };

    private static readonly string[] SkipFileSuffixes =
    {
        ".bak",
        ".bak_",
    };

    /// <summary>
    /// Ensure sandbox project exists. Returns absolute path to sandbox project dir.
    /// </summary>
    public static string Ensure(
        string workspaceRoot,
        string sourceProjectId,
        string sandboxProjectId,
        bool refresh)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            throw new InvalidOperationException($"Workspace root not found: {workspaceRoot}");

        var sourceDir = Path.Combine(workspaceRoot, "projects", sourceProjectId);
        if (!Directory.Exists(sourceDir))
            throw new InvalidOperationException(
                $"Source project not found: {sourceDir}. Expected projects/{sourceProjectId} under workspace.");

        var destDir = Path.Combine(workspaceRoot, "projects", sandboxProjectId);
        var marker = Path.Combine(destDir, ".loadsim-sandbox");

        if (Directory.Exists(destDir) && !refresh)
        {
            if (!File.Exists(marker))
            {
                // Existing folder without marker — still ok if project.json exists
                var meta = Path.Combine(destDir, "project.json");
                if (!File.Exists(meta))
                    throw new InvalidOperationException(
                        $"Directory exists but is not a LoadSim sandbox: {destDir}. Use --refreshSandbox to rebuild.");
            }
            Console.WriteLine($"  sandbox: reuse {destDir}");
            return destDir;
        }

        if (refresh && Directory.Exists(destDir))
        {
            Console.WriteLine($"  sandbox: refreshing {destDir}");
            Directory.Delete(destDir, recursive: true);
        }

        Console.WriteLine($"  sandbox: copying {sourceProjectId} → {sandboxProjectId} …");
        Directory.CreateDirectory(destDir);
        CopyDirectory(sourceDir, destDir);

        RewriteProjectMeta(destDir, sandboxProjectId, sourceProjectId);
        File.WriteAllText(marker,
            $"source={sourceProjectId}{Environment.NewLine}" +
            $"created={DateTimeOffset.UtcNow:O}{Environment.NewLine}" +
            $"note=LoadSim-only sandbox; safe to delete and regenerate{Environment.NewLine}");

        Console.WriteLine($"  sandbox: ready at {destDir}");
        return destDir;
    }

    public static string? FindWorkspaceRoot(string? explicitRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot) && Directory.Exists(explicitRoot))
            return Path.GetFullPath(explicitRoot);

        foreach (var start in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory,
                 })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var projects = Path.Combine(dir.FullName, "projects");
                if (Directory.Exists(projects) &&
                    Directory.Exists(Path.Combine(projects, DefaultSourceId)))
                    return dir.FullName;
                // host/PageToMovie.LoadSim → walk up to repo root
                if (Directory.Exists(Path.Combine(dir.FullName, "host")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "projects", DefaultSourceId)))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }

        return null;
    }

    private static void RewriteProjectMeta(string destDir, string sandboxId, string sourceId)
    {
        var metaPath = Path.Combine(destDir, "project.json");
        if (!File.Exists(metaPath))
        {
            File.WriteAllText(metaPath, JsonSerializer.Serialize(new
            {
                id = sandboxId,
                title = $"LoadSim sandbox (from {sourceId})",
                blueprint_file = "blueprint.clips.grok.json",
                scenes_file = "scenes.json",
                config_file = "pipeline_config.json",
                state_file = "pipeline_state.json",
                description = "Isolated LoadSim project — do not use as a real production project.",
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(metaPath)) as JsonObject ?? new JsonObject();
            node["id"] = sandboxId;
            node["title"] = $"LoadSim sandbox (from {sourceId})";
            node["description"] =
                "Isolated LoadSim project copied from " + sourceId +
                ". Gen/remux/review mutate only this folder. Safe to delete.";
            File.WriteAllText(metaPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // keep copied file if rewrite fails
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            if (ShouldSkipFile(name)) continue;
            File.Copy(file, Path.Combine(destDir, name), overwrite: true);
        }

        foreach (var sub in Directory.EnumerateDirectories(sourceDir))
        {
            var name = Path.GetFileName(sub);
            if (ShouldSkipDirectory(name)) continue;
            var destSub = Path.Combine(destDir, name);
            Directory.CreateDirectory(destSub);
            CopyDirectory(sub, destSub);
        }
    }

    private static bool ShouldSkipDirectory(string name)
    {
        foreach (var skip in SkipDirectoryNames)
        {
            if (string.Equals(name, skip, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool ShouldSkipFile(string name)
    {
        // backups and partials
        if (name.Contains(".bak", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            return true;
        // optional: skip huge PDFs (not needed for gen/play under fakes)
        if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}
