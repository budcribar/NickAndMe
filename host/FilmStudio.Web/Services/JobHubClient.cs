using FilmStudio.Core.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace FilmStudio.Web.Services;

public sealed class JobHubClient : IAsyncDisposable
{
    private readonly EngineApiOptions _opts;
    private readonly AdminSessionService? _session;
    private HubConnection? _connection;

    public event Action<JobSnapshot>? JobUpdated;
    public event Action<string>? JobLog;
    public event Action<object?>? AdminState;

    public bool IsConnected =>
        _connection?.State == HubConnectionState.Connected;

    public JobHubClient(IOptions<EngineApiOptions> opts, AdminSessionService? session = null)
    {
        _opts = opts.Value;
        _session = session;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_connection is { State: HubConnectionState.Connected or HubConnectionState.Connecting })
            return;

        if (_connection is not null)
        {
            try { await _connection.DisposeAsync(); } catch { /* ignore */ }
            _connection = null;
        }

        var baseUrl = (_opts.BaseUrl ?? "http://127.0.0.1:5088").TrimEnd('/');
        var userId = _session?.UserId ?? "local";
        var url = $"{baseUrl}/hubs/jobs?userId={Uri.EscapeDataString(userId)}";

        _connection = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                if (!string.IsNullOrWhiteSpace(_session?.Token))
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(_session!.Token);
                }
                options.Headers[AuthHeaderUserId] = userId;
            })
            .WithAutomaticReconnect()
            .Build();

        _connection.On<JobSnapshot>(JobHubEvents.JobUpdated, snap => JobUpdated?.Invoke(snap));
        _connection.On<string>(JobHubEvents.JobLog, line => JobLog?.Invoke(line));
        _connection.On<object>(JobHubEvents.AdminState, payload => AdminState?.Invoke(payload));

        await _connection.StartAsync(ct);
    }

    /// <summary>Best-effort connect — SignalR is optional for browse-only pages, so failures are swallowed.</summary>
    public async Task EnsureStartedAsync()
    {
        if (IsConnected) return;
        try { await StartAsync(); } catch { /* optional */ }
    }

    private const string AuthHeaderUserId = "X-User-Id";

    public async Task StopAsync()
    {
        if (_connection is null) return;
        await _connection.StopAsync();
        await _connection.DisposeAsync();
        _connection = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
