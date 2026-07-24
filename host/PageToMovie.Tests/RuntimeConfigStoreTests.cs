using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public class RuntimeConfigStoreTests
{
    [Fact]
    public async Task UpdateAsync_persists_and_hot_applies_capacity()
    {
        var root = Path.Combine(Path.GetTempPath(), "PageToMovie-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var opts = Options.Create(new PageToMovieOptions
            {
                WorkspaceRoot = root,
                Capacity = new CapacityOptions { MaxVideoInFlight = 1 },
            });
            var store = new RuntimeConfigStore(opts, NullLogger<RuntimeConfigStore>.Instance);
            var updated = await store.UpdateAsync(new RuntimeConfigUpdateRequest
            {
                Capacity = new CapacityRuntimeDto
                {
                    MaxVideoInFlight = 8,
                    MaxVideoInFlightPerUser = 2,
                    MaxFfmpegInFlight = 3,
                    MaxQueuePerUser = 10,
                },
            }, "admin");

            Assert.Equal(8, updated.Capacity.MaxVideoInFlight);
            Assert.Equal(8, opts.Value.Capacity!.MaxVideoInFlight);
            Assert.True(File.Exists(store.ConfigPath));

            // Reload from file into a fresh options object
            var opts2 = Options.Create(new PageToMovieOptions
            {
                WorkspaceRoot = root,
                Capacity = new CapacityOptions { MaxVideoInFlight = 1 },
            });
            var store2 = new RuntimeConfigStore(opts2, NullLogger<RuntimeConfigStore>.Instance);
            Assert.Equal(8, opts2.Value.Capacity!.MaxVideoInFlight);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}
