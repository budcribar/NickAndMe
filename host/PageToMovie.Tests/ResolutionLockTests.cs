using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Guardrail against silently mixing video resolutions within one project
/// (FilmJobService.DetermineLockedResolution / GetLockedResolutionAsync).
/// </summary>
public class ResolutionLockTests
{
    private static CostEvent VideoEvent(int scene, int clip, string? resolution) =>
        new() { Kind = "video", Scene = scene, Clip = clip, Resolution = resolution };

    [Fact]
    public void No_on_disk_clips_returns_null()
    {
        var locked = FilmJobService.DetermineLockedResolution(
            Array.Empty<(int, int)>(),
            new[] { VideoEvent(1, 1, "720p") });
        Assert.Null(locked);
    }

    [Fact]
    public void Single_consistent_resolution_locks()
    {
        var onDisk = new[] { (1, 1), (1, 2) };
        var ledger = new[] { VideoEvent(1, 1, "720p"), VideoEvent(1, 2, "720p") };
        Assert.Equal("720p", FilmJobService.DetermineLockedResolution(onDisk, ledger));
    }

    [Fact]
    public void Normalizes_resolution_formats_before_comparing()
    {
        var onDisk = new[] { (1, 1), (1, 2) };
        // "720" and "720p" should be treated as the same resolution.
        var ledger = new[] { VideoEvent(1, 1, "720"), VideoEvent(1, 2, "720p") };
        Assert.Equal("720p", FilmJobService.DetermineLockedResolution(onDisk, ledger));
    }

    [Fact]
    public void Mixed_resolutions_among_on_disk_clips_fails_open_to_null()
    {
        var onDisk = new[] { (1, 1), (1, 2) };
        var ledger = new[] { VideoEvent(1, 1, "480p"), VideoEvent(1, 2, "720p") };
        Assert.Null(FilmJobService.DetermineLockedResolution(onDisk, ledger));
    }

    [Fact]
    public void Ignores_ledger_entries_for_clips_no_longer_on_disk()
    {
        // Clip 2 was deleted (not on disk) — its stale ledger entry at a different
        // resolution must not spoil the lock for the clip that remains.
        var onDisk = new[] { (1, 1) };
        var ledger = new[] { VideoEvent(1, 1, "720p"), VideoEvent(1, 2, "480p") };
        Assert.Equal("720p", FilmJobService.DetermineLockedResolution(onDisk, ledger));
    }

    [Fact]
    public void No_matching_ledger_entries_fails_open_to_null()
    {
        var onDisk = new[] { (1, 1) };
        var ledger = Array.Empty<CostEvent>();
        Assert.Null(FilmJobService.DetermineLockedResolution(onDisk, ledger));
    }

    [Fact]
    public void Ignores_ledger_entries_with_missing_resolution()
    {
        var onDisk = new[] { (1, 1) };
        var ledger = new[] { VideoEvent(1, 1, null) };
        Assert.Null(FilmJobService.DetermineLockedResolution(onDisk, ledger));
    }
}
