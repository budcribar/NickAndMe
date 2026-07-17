namespace FilmStudio.Core.Models;

/// <summary>Hot-editable server settings (Phase D admin config).</summary>
public sealed class RuntimeConfigDto
{
    public CapacityRuntimeDto Capacity { get; set; } = new();
    public FakesRuntimeDto Fakes { get; set; } = new();
    public bool UseFakes { get; set; }
    /// <summary>Settings that need process restart to fully apply.</summary>
    public List<string> RestartRequired { get; set; } = new();
    public string? ConfigPath { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class CapacityRuntimeDto
{
    public int MaxVideoInFlight { get; set; } = 4;
    public int MaxVideoInFlightPerUser { get; set; } = 2;
    public int MaxFfmpegInFlight { get; set; } = 2;
    public int MaxQueuePerUser { get; set; } = 5;
}

public sealed class FakesRuntimeDto
{
    public string VideoMode { get; set; } = "MergeRealistic";
    public int VideoDelayMs { get; set; } = 200;
    public double FailRate { get; set; }
    public int RateLimitEveryN { get; set; }
}

public sealed class RuntimeConfigUpdateRequest
{
    public CapacityRuntimeDto? Capacity { get; set; }
    public FakesRuntimeDto? Fakes { get; set; }
    public bool? UseFakes { get; set; }
}

public sealed class AdminCancelJobRequest
{
    public string? JobId { get; set; }
}

public sealed class AdminReleaseLockRequest
{
    public string Resource { get; set; } = "";
    public bool Force { get; set; } = true;
}
