using FilmStudio.Core.Options;
using FilmStudio.Engine;
using FilmStudio.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FilmStudio.Tests;

public sealed class CachingChatClientTests : IDisposable
{
    private readonly string _workspaceRoot =
        Path.Combine(Path.GetTempPath(), "fs-chatcache-tests-" + Guid.NewGuid().ToString("N"));

    public CachingChatClientTests() => Directory.CreateDirectory(_workspaceRoot);

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best effort */ }
    }

    private sealed class CountingChatClient : IChatClient
    {
        public int Calls { get; private set; }
        public bool IsConfigured => true;

        public Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            string model = "grok-4.5",
            double temperature = 0.2,
            CancellationToken ct = default,
            string? mode = null)
        {
            Calls++;
            return Task.FromResult($"response-{Calls}");
        }
    }

    private CachingChatClient MakeSut(
        CountingChatClient inner, bool enabled = true, bool cacheNonZero = false, string cacheVersion = "1")
    {
        var opts = Options.Create(new FilmStudioOptions
        {
            WorkspaceRoot = _workspaceRoot,
            ChatCacheEnabled = enabled,
            ChatCacheNonZeroTemperature = cacheNonZero,
            ChatCacheVersion = cacheVersion,
        });
        return new CachingChatClient(inner, opts, NullLogger<CachingChatClient>.Instance);
    }

    [Fact]
    public async Task IdenticalRequest_SecondCallIsCacheHit_InnerCalledOnce()
    {
        var inner = new CountingChatClient();
        var sut = MakeSut(inner);

        var first = await sut.CompleteAsync("sys", "user beat text", "grok-4.5", temperature: 0);
        var second = await sut.CompleteAsync("sys", "user beat text", "grok-4.5", temperature: 0);

        Assert.Equal(1, inner.Calls);
        Assert.Equal(first, second);
        Assert.Equal("response-1", second);
    }

    [Fact]
    public async Task DifferentUserPrompt_IsCacheMiss()
    {
        var inner = new CountingChatClient();
        var sut = MakeSut(inner);

        await sut.CompleteAsync("sys", "beat A", "grok-4.5", temperature: 0);
        await sut.CompleteAsync("sys", "beat B", "grok-4.5", temperature: 0);

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task DifferentModel_IsCacheMiss()
    {
        var inner = new CountingChatClient();
        var sut = MakeSut(inner);

        await sut.CompleteAsync("sys", "same text", "grok-4.5", temperature: 0);
        await sut.CompleteAsync("sys", "same text", "grok-4.5-mini", temperature: 0);

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task NonZeroTemperature_BypassesCacheByDefault()
    {
        var inner = new CountingChatClient();
        var sut = MakeSut(inner);

        await sut.CompleteAsync("sys", "same text", "grok-4.5", temperature: 0.7);
        await sut.CompleteAsync("sys", "same text", "grok-4.5", temperature: 0.7);

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task NonZeroTemperature_CachesWhenOptedIn()
    {
        var inner = new CountingChatClient();
        var sut = MakeSut(inner, cacheNonZero: true);

        await sut.CompleteAsync("sys", "same text", "grok-4.5", temperature: 0.7);
        await sut.CompleteAsync("sys", "same text", "grok-4.5", temperature: 0.7);

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Disabled_NeverCaches()
    {
        var inner = new CountingChatClient();
        var sut = MakeSut(inner, enabled: false);

        await sut.CompleteAsync("sys", "same text", "grok-4.5", temperature: 0);
        await sut.CompleteAsync("sys", "same text", "grok-4.5", temperature: 0);

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task CacheSurvivesAcrossClientInstances_OnDisk()
    {
        var inner1 = new CountingChatClient();
        await MakeSut(inner1).CompleteAsync("sys", "beat text", "grok-4.5", temperature: 0);
        Assert.Equal(1, inner1.Calls);

        // Fresh client instance, same workspace root — simulates a process restart reusing the
        // on-disk cache (the "repeatable across runs" property).
        var inner2 = new CountingChatClient();
        var second = await MakeSut(inner2).CompleteAsync("sys", "beat text", "grok-4.5", temperature: 0);

        Assert.Equal(0, inner2.Calls);
        Assert.Equal("response-1", second);
    }

    [Fact]
    public async Task IsConfigured_DelegatesToInner()
    {
        var inner = new CountingChatClient();
        var sut = MakeSut(inner);
        Assert.Equal(inner.IsConfigured, sut.IsConfigured);
    }

    [Fact]
    public async Task BumpingCacheVersion_InvalidatesExistingEntries()
    {
        var inner1 = new CountingChatClient();
        await MakeSut(inner1, cacheVersion: "1").CompleteAsync("sys", "beat text", "grok-4.5", temperature: 0);
        Assert.Equal(1, inner1.Calls);

        // A provider changed the model's behavior under the same model id — operator bumps
        // ChatCacheVersion. Same request, new version: must be a live call, not a stale hit.
        var inner2 = new CountingChatClient();
        var afterBump = await MakeSut(inner2, cacheVersion: "2")
            .CompleteAsync("sys", "beat text", "grok-4.5", temperature: 0);

        Assert.Equal(1, inner2.Calls);
        Assert.Equal("response-1", afterBump); // inner2's own first call, not inner1's cached one
    }

    [Fact]
    public async Task ClearCache_RemovesEntries_SubsequentCallIsLive()
    {
        var inner = new CountingChatClient();
        var sut = MakeSut(inner);
        await sut.CompleteAsync("sys", "beat text", "grok-4.5", temperature: 0);
        Assert.Equal(1, inner.Calls);

        var removed = sut.ClearCache();
        Assert.Equal(1, removed);

        await sut.CompleteAsync("sys", "beat text", "grok-4.5", temperature: 0);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public void ClearCache_EmptyCache_ReturnsZero()
    {
        var sut = MakeSut(new CountingChatClient());
        Assert.Equal(0, sut.ClearCache());
    }
}
