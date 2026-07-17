namespace FilmStudio.Core.Models;

/// <summary>Live progress posted by FilmStudio.LoadSim during a run.</summary>
public sealed class LoadSimProgressDto
{
    public string RunId { get; set; } = "";
    public string Status { get; set; } = "running"; // running | finished
    public int Users { get; set; }
    public int DurationSec { get; set; }
    public double ElapsedSec { get; set; }
    public string Scenario { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string BaseUrl { get; set; } = "";

    public int ActionsTotal { get; set; }
    public double ActionsPerSec { get; set; }
    public int Errors { get; set; }
    public double ErrorRate { get; set; }
    public int Intentional409 { get; set; }

    public long P50Ms { get; set; }
    public long P95Ms { get; set; }
    public long BrowseP50Ms { get; set; }
    public long BrowseP95Ms { get; set; }

    public int JobsSubmitted { get; set; }
    public int JobsRejected { get; set; }
    public int Jobs5xx { get; set; }
    public int HealthOk { get; set; }
    public int HealthFail { get; set; }

    public int PeakApiInFlight { get; set; }
    public int ConfiguredMaxVideoInFlight { get; set; }

    public Dictionary<string, int> ActionsByType { get; set; } = new();
    public bool? Passed { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Admin view: latest + history for charts.</summary>
public sealed class LoadSimLiveStateDto
{
    public bool Active { get; set; }
    public LoadSimProgressDto? Latest { get; set; }
    public List<LoadSimProgressDto> History { get; set; } = new();
}
