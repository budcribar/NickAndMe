using PageToMovie.Api.Hubs;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Microsoft.AspNetCore.SignalR;
// JobHubEvents lives in PageToMovie.Core.Models

namespace PageToMovie.Api.Services;

public sealed class SignalRJobProgressSink : IJobProgressSink
{
    private readonly IHubContext<JobHub> _hub;

    public SignalRJobProgressSink(IHubContext<JobHub> hub) => _hub = hub;

    public async Task OnJobUpdatedAsync(JobSnapshot snapshot, CancellationToken ct = default)
    {
        // Multi-user: only job + owner groups (clients join user:{id} on connect).
        if (!string.IsNullOrWhiteSpace(snapshot.JobId))
            await _hub.Clients.Group($"job:{snapshot.JobId}")
                .SendAsync(JobHubEvents.JobUpdated, snapshot, ct);

        if (!string.IsNullOrWhiteSpace(snapshot.UserId))
            await _hub.Clients.Group($"user:{snapshot.UserId}")
                .SendAsync(JobHubEvents.JobUpdated, snapshot, ct);
    }

    public async Task OnJobLogAsync(string message, CancellationToken ct = default)
    {
        // Progress text also arrives via JobUpdated.Message on user/job groups.
        // JobLog is optional detail; avoid Clients.All for multi-user isolation.
        await Task.CompletedTask;
        _ = message;
    }
}
