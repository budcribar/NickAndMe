using FilmStudio.Core.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace FilmStudio.Web.Services;

public sealed class JobHubClient : IAsyncDisposable
{
    private readonly EngineApiOptions _opts;
    private HubConnection? _connection;

    public event Action<JobSnapshot>? JobUpdated;
    public event Action<string>? JobLog;

    public bool IsConnected =>
        _connection?.State == HubConnectionState.Connected;

    public JobHubClient(IOptions<EngineApiOptions> opts) => _opts = opts.Value;

    public async Task StartAsync(CancellationToken ct = default)
    {
        // Already live
        if (_connection is { State: HubConnectionState.Connected or HubConnectionState.Connecting })
            return;

        // Stale connection after drop — rebuild
        if (_connection is not null)
        {
            try { await _connection.DisposeAsync(); } catch { /* ignore */ }
            _connection = null;
        }

        var baseUrl = (_opts.BaseUrl ?? "http://127.0.0.1:5088").TrimEnd('/');
        _connection = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/hubs/jobs")
            .WithAutomaticReconnect()
            .Build();

        _connection.On<JobSnapshot>(JobHubEvents.JobUpdated, snap => JobUpdated?.Invoke(snap));
        _connection.On<string>(JobHubEvents.JobLog, line => JobLog?.Invoke(line));

        await _connection.StartAsync(ct);
    }

    public async Task StopAsync()
    {
        if (_connection is null) return;
        await _connection.StopAsync();
        await _connection.DisposeAsync();
        _connection = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
