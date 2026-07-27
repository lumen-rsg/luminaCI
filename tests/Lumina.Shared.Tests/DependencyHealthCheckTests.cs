using Lumina.Web.Shared.Health;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Lumina.Shared.Tests;

public class DependencyHealthCheckTests
{
    [Fact]
    public async Task DistributedCacheCheck_round_trips_and_removes_probe()
    {
        var cache = new RecordingDistributedCache();
        var check = new DistributedCacheHealthCheck(cache);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Empty(cache.Values);
        Assert.Equal(1, cache.RemoveCount);
    }

    [Fact]
    public async Task WritableDirectoriesCheck_probes_every_directory_without_leaving_files()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lumina-health-test-{Guid.NewGuid():N}");
        var directories = new[] { Path.Combine(root, "one"), Path.Combine(root, "two") };

        try
        {
            var check = new WritableDirectoriesHealthCheck(directories);

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Healthy, result.Status);
            Assert.All(directories, directory => Assert.Empty(Directory.EnumerateFiles(directory)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class RecordingDistributedCache : IDistributedCache
    {
        public Dictionary<string, byte[]> Values { get; } = new();
        public int RemoveCount { get; private set; }

        public byte[]? Get(string key) => Values.GetValueOrDefault(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            Task.FromResult(Get(key));

        public void Refresh(string key) { }

        public Task RefreshAsync(string key, CancellationToken token = default) =>
            Task.CompletedTask;

        public void Remove(string key)
        {
            Values.Remove(key);
            RemoveCount++;
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
            Values[key] = value;

        public Task SetAsync(
            string key,
            byte[] value,
            DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }
    }
}
