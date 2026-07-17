namespace FilmStudio.LoadSim;

public sealed class SimOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:5088";
    public int Users { get; set; } = 20;
    public int DurationSec { get; set; } = 120;
    public string Scenario { get; set; } = "mixed"; // browse | play | gen | remux | mixed
    /// <summary>Project id used by VUs (default isolated sandbox, not real Buster).</summary>
    public string ProjectId { get; set; } = ProjectSandbox.DefaultSandboxId;
    /// <summary>Source project to copy when preparing sandbox (default Buster).</summary>
    public string SourceProjectId { get; set; } = ProjectSandbox.DefaultSourceId;
    /// <summary>Repo root containing projects/. Auto-detected if empty.</summary>
    public string? WorkspaceRoot { get; set; }
    /// <summary>
    /// Optional: copy source → sandbox before run.
    /// Default false — <c>projects/LoadSimBuster</c> is checked into git.
    /// </summary>
    public bool PrepareSandbox { get; set; }
    /// <summary>With prepare: delete and recopy sandbox from source.</summary>
    public bool RefreshSandbox { get; set; }
    /// <summary>Allow targeting a real project id (e.g. Buster) without sandbox.</summary>
    public bool AllowRealProject { get; set; }
    public bool SharedProject { get; set; } = true;
    public int ThinkTimeMs { get; set; } = 200;
    public double GenWeight { get; set; } = 0.10;
    public double PlayWeight { get; set; } = 0.35;
    public double BrowseWeight { get; set; } = 0.45;
    public double ReviewWeight { get; set; } = 0.05;
    public double RemuxWeight { get; set; } = 0.05;
    public int MaxGenPerUser { get; set; } = 1;
    public bool ForceLockCollisions { get; set; }
    public string OutPath { get; set; } = "loadsim-results.json";
    public double MaxErrorRate { get; set; } = 0.01;
    public double MaxBrowseP95Ms { get; set; } = 500;
    public bool RequireFakes { get; set; } = true;
    public bool IKnowWhatImDoing { get; set; }
    public int WarmupSec { get; set; } = 0;
    /// <summary>Seconds to wait for API /health before giving up (multi-start race).</summary>
    public int WaitForApiSec { get; set; } = 90;

    public static SimOptions Parse(string[] args)
    {
        var o = new SimOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i])
            {
                case "--baseUrl": o.BaseUrl = Next().TrimEnd('/'); break;
                case "--users": o.Users = int.Parse(Next()); break;
                case "--duration": o.DurationSec = int.Parse(Next()); break;
                case "--scenario": o.Scenario = Next(); break;
                case "--project":
                case "--projectId": o.ProjectId = Next(); break;
                case "--sourceProject": o.SourceProjectId = Next(); break;
                case "--workspace":
                case "--workspaceRoot": o.WorkspaceRoot = Next(); break;
                case "--prepareSandbox":
                    // flag form or true/false
                    if (i + 1 < args.Length && (args[i + 1] is "true" or "false"))
                        o.PrepareSandbox = bool.Parse(Next());
                    else
                        o.PrepareSandbox = true;
                    break;
                case "--refreshSandbox": o.RefreshSandbox = true; o.PrepareSandbox = true; break;
                case "--no-prepareSandbox": o.PrepareSandbox = false; break;
                case "--allowRealProject": o.AllowRealProject = true; break;
                case "--projectPrefix": o.ProjectId = Next(); o.SharedProject = false; break;
                case "--sharedProject": o.SharedProject = bool.Parse(Next()); break;
                case "--thinkTimeMs": o.ThinkTimeMs = int.Parse(Next()); break;
                case "--genWeight": o.GenWeight = double.Parse(Next()); break;
                case "--playWeight": o.PlayWeight = double.Parse(Next()); break;
                case "--browseWeight": o.BrowseWeight = double.Parse(Next()); break;
                case "--reviewWeight": o.ReviewWeight = double.Parse(Next()); break;
                case "--remuxWeight": o.RemuxWeight = double.Parse(Next()); break;
                case "--maxGenPerUser": o.MaxGenPerUser = int.Parse(Next()); break;
                case "--forceLockCollisions": o.ForceLockCollisions = true; break;
                case "--out": o.OutPath = Next(); break;
                case "--maxErrorRate": o.MaxErrorRate = double.Parse(Next()); break;
                case "--maxBrowseP95Ms": o.MaxBrowseP95Ms = double.Parse(Next()); break;
                case "--requireFakes": o.RequireFakes = bool.Parse(Next()); break;
                case "--i-know-what-im-doing": o.IKnowWhatImDoing = true; break;
                case "--warmupSec": o.WarmupSec = int.Parse(Next()); break;
                case "--waitForApiSec": o.WaitForApiSec = int.Parse(Next()); break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }

        o.Users = Math.Clamp(o.Users, 1, 500);
        o.DurationSec = Math.Clamp(o.DurationSec, 5, 86_400);
        o.ThinkTimeMs = Math.Clamp(o.ThinkTimeMs, 0, 60_000);
        o.WaitForApiSec = Math.Clamp(o.WaitForApiSec, 0, 600);
        return o;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            FilmStudio.LoadSim — concurrent virtual users against FilmStudio.Api

            Options:
              --baseUrl URL              default http://127.0.0.1:5088
              --users N                  virtual users (default 20)
              --duration SEC             run length (default 120)
              --scenario NAME            browse|play|gen|remux|mixed (default mixed)
              --project ID               project id (default LoadSimBuster, checked into git)
              --sourceProject ID         only with --prepareSandbox (default Buster)
              --workspace PATH           only with --prepareSandbox (auto-detect if omitted)
              --prepareSandbox           optional: (re)copy sandbox from source project
              --refreshSandbox           with prepare: force full recopy
              --allowRealProject         allow targeting real Buster/NickAndMe
              --sharedProject true|false share one project (default true)
              --thinkTimeMs N            pause between actions (default 200)
              --genWeight W              mixed-scenario weight (default 0.10)
              --playWeight W
              --browseWeight W
              --reviewWeight W
              --remuxWeight W
              --maxGenPerUser N          cap gen submits per VU (default 1)
              --forceLockCollisions      all VUs target scene 1 (stress locks)
              --out PATH                 results JSON (default loadsim-results.json)
              --maxErrorRate R           gate: error rate excl. 409 (default 0.01)
              --maxBrowseP95Ms N         gate: browse p95 (default 500)
              --requireFakes true|false  refuse gen without UseFakes unless --i-know-what-im-doing
              --i-know-what-im-doing     allow gen against real keys
              --waitForApiSec N          wait for /health (default 90; multi-start with Api)
              --warmupSec N              extra delay after health ok (default 0)

            Exit codes: 0 = gates pass, 1 = gates fail, 2 = setup error
            """);
    }
}
