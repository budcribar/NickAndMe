using System.Collections.Concurrent;

namespace PageToMovie.Api.Auth;

/// <summary>Simple in-memory login brute-force limiter (Phase D).</summary>
public sealed class LoginRateLimiter
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxAttempts;
    private readonly TimeSpan _window;

    public LoginRateLimiter(int maxAttempts = 10, int windowSeconds = 300)
    {
        _maxAttempts = Math.Max(3, maxAttempts);
        _window = TimeSpan.FromSeconds(Math.Max(30, windowSeconds));
    }

    public bool IsBlocked(string key, out int retryAfterSec)
    {
        key = Normalize(key);
        retryAfterSec = 0;
        if (!_windows.TryGetValue(key, out var w))
            return false;
        lock (w)
        {
            Prune(w);
            if (w.Failures.Count < _maxAttempts)
                return false;
            var oldest = w.Failures[0];
            var until = oldest + _window;
            if (until <= DateTimeOffset.UtcNow)
            {
                w.Failures.Clear();
                return false;
            }
            retryAfterSec = (int)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds);
            return true;
        }
    }

    public void RecordFailure(string key)
    {
        key = Normalize(key);
        var w = _windows.GetOrAdd(key, _ => new Window());
        lock (w)
        {
            Prune(w);
            // Cap history — once blocked, further failures must not grow memory unbounded
            if (w.Failures.Count >= _maxAttempts)
                return;
            w.Failures.Add(DateTimeOffset.UtcNow);
        }
    }

    public void RecordSuccess(string key)
    {
        key = Normalize(key);
        if (_windows.TryGetValue(key, out var w))
        {
            lock (w) w.Failures.Clear();
        }
    }

    private void Prune(Window w)
    {
        var cutoff = DateTimeOffset.UtcNow - _window;
        w.Failures.RemoveAll(t => t < cutoff);
    }

    private static string Normalize(string key) =>
        string.IsNullOrWhiteSpace(key) ? "unknown" : key.Trim().ToLowerInvariant();

    private sealed class Window
    {
        public List<DateTimeOffset> Failures { get; } = new();
    }
}
