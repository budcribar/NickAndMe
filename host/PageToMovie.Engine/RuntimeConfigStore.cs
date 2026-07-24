using System.Text.Json;
using System.Text.Json.Serialization;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

public interface IRuntimeConfigStore
{
    RuntimeConfigDto Get();
    Task<RuntimeConfigDto> UpdateAsync(
        RuntimeConfigUpdateRequest req,
        string adminUserId,
        CancellationToken ct = default);
    string ConfigPath { get; }
}

/// <summary>
/// File-backed runtime capacity/fakes config with hot-apply onto <see cref="PageToMovieOptions"/>.
/// </summary>
public sealed class RuntimeConfigStore : IRuntimeConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IOptions<PageToMovieOptions> _opts;
    private readonly ILogger<RuntimeConfigStore> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly string _auditPath;
    private DateTimeOffset? _updatedAt;
    private string? _updatedBy;

    public RuntimeConfigStore(IOptions<PageToMovieOptions> opts, ILogger<RuntimeConfigStore> log)
    {
        _opts = opts;
        _log = log;
        var root = opts.Value.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            root = Directory.GetCurrentDirectory();
        var dir = Path.Combine(root, ".PageToMovie");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "runtime-config.json");
        _auditPath = Path.Combine(dir, "admin_config_audit.jsonl");
        // Startup: sync load is fine (once, before requests)
        TryLoadAndApply();
    }

    public string ConfigPath => _path;

    public RuntimeConfigDto Get()
    {
        var o = _opts.Value;
        var cap = o.Capacity ?? new CapacityOptions();
        var f = o.Fakes ?? new FakesOptions();
        return new RuntimeConfigDto
        {
            Capacity = new CapacityRuntimeDto
            {
                MaxVideoInFlight = cap.MaxVideoInFlight,
                MaxVideoInFlightPerUser = cap.MaxVideoInFlightPerUser,
                MaxFfmpegInFlight = cap.MaxFfmpegInFlight,
                MaxQueuePerUser = cap.MaxQueuePerUser,
            },
            Fakes = new FakesRuntimeDto
            {
                VideoMode = f.VideoMode,
                VideoDelayMs = f.VideoDelayMs,
                FailRate = f.FailRate,
                RateLimitEveryN = f.RateLimitEveryN,
            },
            UseFakes = o.UseFakes,
            RestartRequired = new List<string> { "UseFakes" },
            ConfigPath = _path,
            UpdatedAt = _updatedAt,
            UpdatedBy = _updatedBy,
        };
    }

    public async Task<RuntimeConfigDto> UpdateAsync(
        RuntimeConfigUpdateRequest req,
        string adminUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var o = _opts.Value;
            o.Capacity ??= new CapacityOptions();
            o.Fakes ??= new FakesOptions();
            var before = Get();

            if (req.Capacity is { } c)
            {
                o.Capacity.MaxVideoInFlight = Math.Clamp(c.MaxVideoInFlight, 1, 64);
                o.Capacity.MaxVideoInFlightPerUser = Math.Clamp(c.MaxVideoInFlightPerUser, 1, 16);
                o.Capacity.MaxFfmpegInFlight = Math.Clamp(c.MaxFfmpegInFlight, 1, 16);
                o.Capacity.MaxQueuePerUser = Math.Clamp(c.MaxQueuePerUser, 1, 100);
            }

            if (req.Fakes is { } f)
            {
                o.Fakes.VideoMode = string.IsNullOrWhiteSpace(f.VideoMode)
                    ? "MergeRealistic"
                    : f.VideoMode.Trim();
                o.Fakes.VideoDelayMs = Math.Clamp(f.VideoDelayMs, 0, 600_000);
                // NaN/Infinity must not poison options (Math.Clamp throws on NaN)
                var failRate = double.IsFinite(f.FailRate) ? f.FailRate : 0;
                o.Fakes.FailRate = Math.Clamp(failRate, 0, 1);
                o.Fakes.RateLimitEveryN = Math.Max(0, f.RateLimitEveryN);
            }

            if (req.UseFakes is bool uf)
                o.UseFakes = uf;

            _updatedAt = DateTimeOffset.UtcNow;
            _updatedBy = adminUserId;
            await PersistAsync(ct).ConfigureAwait(false);
            await AppendAuditAsync(adminUserId, before, Get(), ct).ConfigureAwait(false);
            _log.LogInformation("Runtime config updated by {User}", adminUserId);
            return Get();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void TryLoadAndApply()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var dto = JsonSerializer.Deserialize<RuntimeConfigFile>(json, JsonOpts);
            if (dto is null) return;
            var o = _opts.Value;
            o.Capacity ??= new CapacityOptions();
            o.Fakes ??= new FakesOptions();
            if (dto.Capacity is { } c)
            {
                o.Capacity.MaxVideoInFlight = Math.Clamp(c.MaxVideoInFlight, 1, 64);
                o.Capacity.MaxVideoInFlightPerUser = Math.Clamp(c.MaxVideoInFlightPerUser, 1, 16);
                o.Capacity.MaxFfmpegInFlight = Math.Clamp(c.MaxFfmpegInFlight, 1, 16);
                o.Capacity.MaxQueuePerUser = Math.Clamp(c.MaxQueuePerUser, 1, 100);
            }
            if (dto.Fakes is { } f)
            {
                if (!string.IsNullOrWhiteSpace(f.VideoMode))
                    o.Fakes.VideoMode = f.VideoMode;
                o.Fakes.VideoDelayMs = Math.Clamp(f.VideoDelayMs, 0, 600_000);
                var failRate = double.IsFinite(f.FailRate) ? f.FailRate : 0;
                o.Fakes.FailRate = Math.Clamp(failRate, 0, 1);
                o.Fakes.RateLimitEveryN = Math.Max(0, f.RateLimitEveryN);
            }
            if (dto.UseFakes is bool uf)
                o.UseFakes = uf;
            _updatedAt = dto.UpdatedAt;
            _updatedBy = dto.UpdatedBy;
            _log.LogInformation("Loaded runtime config from {Path}", _path);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load runtime config from {Path}", _path);
        }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        var o = _opts.Value;
        var file = new RuntimeConfigFile
        {
            Capacity = new CapacityRuntimeDto
            {
                MaxVideoInFlight = o.Capacity?.MaxVideoInFlight ?? 4,
                MaxVideoInFlightPerUser = o.Capacity?.MaxVideoInFlightPerUser ?? 2,
                MaxFfmpegInFlight = o.Capacity?.MaxFfmpegInFlight ?? 2,
                MaxQueuePerUser = o.Capacity?.MaxQueuePerUser ?? 5,
            },
            Fakes = new FakesRuntimeDto
            {
                VideoMode = o.Fakes?.VideoMode ?? "MergeRealistic",
                VideoDelayMs = o.Fakes?.VideoDelayMs ?? 200,
                FailRate = o.Fakes?.FailRate ?? 0,
                RateLimitEveryN = o.Fakes?.RateLimitEveryN ?? 0,
            },
            UseFakes = o.UseFakes,
            UpdatedAt = _updatedAt,
            UpdatedBy = _updatedBy,
        };
        var tmp = _path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(file, JsonOpts), ct)
            .ConfigureAwait(false);
        File.Move(tmp, _path, overwrite: true);
    }

    private async Task AppendAuditAsync(
        string user,
        RuntimeConfigDto before,
        RuntimeConfigDto after,
        CancellationToken ct)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTimeOffset.UtcNow,
                user,
                before,
                after,
            }, JsonOpts);
            await File.AppendAllTextAsync(_auditPath, line + Environment.NewLine, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to append config audit");
        }
    }

    private sealed class RuntimeConfigFile
    {
        public CapacityRuntimeDto? Capacity { get; set; }
        public FakesRuntimeDto? Fakes { get; set; }
        public bool? UseFakes { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
    }
}
