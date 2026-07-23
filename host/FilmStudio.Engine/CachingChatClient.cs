using System.Security.Cryptography;
using System.Text;
using FilmStudio.Core.Options;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FilmStudio.Engine;

/// <summary>
/// Decorates an <see cref="IChatClient"/> with an on-disk response cache keyed by a hash of
/// the full request (cache version, model, temperature, system prompt, user prompt). All 15+
/// classifiers and planning services share one <see cref="IChatClient"/>, so wrapping it here
/// caches every caller without touching them.
///
/// The payoff isn't request speed (tokenizing locally wouldn't change that either - the cost is
/// network round-trip plus the provider's own inference, and providers tokenize server-side
/// regardless of what we send) - it's skipping the round-trip entirely on a repeat, and getting
/// an exactly reproducible response for it instead of re-rolling the dice. That matters for
/// Stage2 replans after an unrelated edit, retry loops, and evals.
///
/// Deliberately skips temperature above 0 by default (see
/// <see cref="FilmStudioOptions.ChatCacheNonZeroTemperature"/>): nonzero temperature is normally
/// requested precisely to get varied responses across calls, so caching it would silently defeat
/// the caller's intent.
/// </summary>
public sealed class CachingChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly ILogger<CachingChatClient> _log;
    private readonly string _cacheDir;
    private readonly bool _enabled;
    private readonly bool _cacheNonZeroTemperature;
    private readonly string _cacheVersion;

    public CachingChatClient(
        IChatClient inner,
        IOptions<FilmStudioOptions> opts,
        ILogger<CachingChatClient> log)
    {
        _inner = inner;
        _log = log;
        var o = opts.Value;
        _enabled = o.ChatCacheEnabled;
        _cacheNonZeroTemperature = o.ChatCacheNonZeroTemperature;
        _cacheVersion = string.IsNullOrWhiteSpace(o.ChatCacheVersion) ? "1" : o.ChatCacheVersion;
        var root = o.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            root = Directory.GetCurrentDirectory();
        _cacheDir = Path.Combine(root, ".filmstudio", "chat_cache");
    }

    public bool IsConfigured => _inner.IsConfigured;

    /// <summary>Root of the on-disk cache, for admin/debug tooling.</summary>
    public string CacheDir => _cacheDir;

    /// <summary>
    /// Deletes every cached response. Use when a provider silently changes a model's behavior
    /// under an unchanged model id and you'd rather reclaim disk space than wait for a
    /// <see cref="FilmStudioOptions.ChatCacheVersion"/> bump to strand the old entries.
    /// </summary>
    public int ClearCache()
    {
        if (!Directory.Exists(_cacheDir)) return 0;
        var files = Directory.GetFiles(_cacheDir, "*.txt", SearchOption.AllDirectories);
        foreach (var f in files)
        {
            try { File.Delete(f); }
            catch (Exception ex) { _log.LogWarning(ex, "Chat cache clear: failed to delete {File}", f); }
        }
        return files.Length;
    }

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string model = "grok-4.5",
        double temperature = 0.2,
        CancellationToken ct = default,
        string? mode = null)
    {
        var cacheable = _enabled && (temperature <= 0.0001 || _cacheNonZeroTemperature);
        if (!cacheable)
            return await _inner.CompleteAsync(systemPrompt, userPrompt, model, temperature, ct, mode)
                .ConfigureAwait(false);

        var key = ComputeKey(_cacheVersion, model, temperature, systemPrompt, userPrompt);
        var path = CachePath(key);

        if (File.Exists(path))
        {
            try
            {
                var cached = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                _log.LogDebug("Chat cache hit: mode={Mode} key={Key}", mode, key);
                return cached;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Chat cache read failed, falling back to live call: key={Key}", key);
            }
        }

        var response = await _inner.CompleteAsync(systemPrompt, userPrompt, model, temperature, ct, mode)
            .ConfigureAwait(false);

        await WriteCacheAsync(path, response, key, ct).ConfigureAwait(false);
        return response;
    }

    private async Task WriteCacheAsync(string path, string response, string key, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write-then-move so a crash or concurrent classifier reading the same key never
            // sees a partial file.
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(tmp, response, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Chat cache write failed: key={Key}", key);
        }
    }

    // Fields are joined with a control character (code point 31, ASCII "unit separator") instead
    // of a literal character in source, so nothing here can collide with real prompt text and
    // reshuffle two different requests into the same hash.
    private static readonly char FieldSeparator = (char)31;

    private static string ComputeKey(
        string cacheVersion, string model, double temperature, string systemPrompt, string userPrompt)
    {
        var sb = new StringBuilder();
        sb.Append(cacheVersion).Append(FieldSeparator);
        sb.Append(model).Append(FieldSeparator);
        sb.Append(temperature.ToString("R")).Append(FieldSeparator);
        sb.Append(systemPrompt).Append(FieldSeparator);
        sb.Append(userPrompt);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    // Two-level shard so a long-lived cache doesn't dump tens of thousands of files in one dir.
    private string CachePath(string key) =>
        Path.Combine(_cacheDir, key[..2], key + ".txt");
}
