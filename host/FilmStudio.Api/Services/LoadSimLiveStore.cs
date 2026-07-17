using System.Collections.Concurrent;
using FilmStudio.Core.Models;

namespace FilmStudio.Api.Services;

/// <summary>In-memory live LoadSim telemetry for the admin dashboard.</summary>
public sealed class LoadSimLiveStore
{
    private readonly object _gate = new();
    private readonly List<LoadSimProgressDto> _history = new();
    private LoadSimProgressDto? _latest;
    private const int MaxHistory = 120; // ~4 min at 2s interval

    public void Publish(LoadSimProgressDto dto)
    {
        if (dto is null) return;
        if (string.IsNullOrWhiteSpace(dto.RunId))
            dto.RunId = Guid.NewGuid().ToString("N")[..12];
        dto.At = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            _latest = Clone(dto);
            _history.Add(Clone(dto));
            while (_history.Count > MaxHistory)
                _history.RemoveAt(0);
        }
    }

    public LoadSimLiveStateDto GetState()
    {
        lock (_gate)
        {
            var latest = _latest is null ? null : Clone(_latest);
            var active = latest is not null &&
                         string.Equals(latest.Status, "running", StringComparison.OrdinalIgnoreCase) &&
                         (DateTimeOffset.UtcNow - latest.At).TotalSeconds < 15;

            return new LoadSimLiveStateDto
            {
                Active = active,
                Latest = latest,
                History = _history.Select(Clone).ToList(),
            };
        }
    }

    private static LoadSimProgressDto Clone(LoadSimProgressDto s) => new()
    {
        RunId = s.RunId,
        Status = s.Status,
        Users = s.Users,
        DurationSec = s.DurationSec,
        ElapsedSec = s.ElapsedSec,
        Scenario = s.Scenario,
        ProjectId = s.ProjectId,
        BaseUrl = s.BaseUrl,
        ActionsTotal = s.ActionsTotal,
        ActionsPerSec = s.ActionsPerSec,
        Errors = s.Errors,
        ErrorRate = s.ErrorRate,
        Intentional409 = s.Intentional409,
        P50Ms = s.P50Ms,
        P95Ms = s.P95Ms,
        BrowseP50Ms = s.BrowseP50Ms,
        BrowseP95Ms = s.BrowseP95Ms,
        JobsSubmitted = s.JobsSubmitted,
        JobsRejected = s.JobsRejected,
        Jobs5xx = s.Jobs5xx,
        HealthOk = s.HealthOk,
        HealthFail = s.HealthFail,
        PeakApiInFlight = s.PeakApiInFlight,
        ConfiguredMaxVideoInFlight = s.ConfiguredMaxVideoInFlight,
        ActionsByType = s.ActionsByType is null
            ? new Dictionary<string, int>()
            : new Dictionary<string, int>(s.ActionsByType),
        Passed = s.Passed,
        At = s.At,
    };
}
