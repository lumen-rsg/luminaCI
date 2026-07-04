using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using Lumina.Shared.Extensions;
using Xunit;

namespace Lumina.Shared.Tests;

/// <summary>
/// Smoke tests for the RedisCacheService serialization contract: the cache must
/// round-trip cache-safe DTOs and must NEVER silently persist a value that
/// System.Text.Json cannot reproduce (the EF-entity-with-navigation-properties
/// failure mode that previously made pipelines/builds "disappear").
/// </summary>
public class RedisCacheServiceSerializationTests
{
    /// <summary>
    /// Minimal in-process IDistributedCache. We are testing the serialization
    /// contract of RedisCacheService, not Redis itself, so an in-memory store is
    /// sufficient and keeps the test free of external infrastructure.
    /// </summary>
    private sealed class InMemoryDistributedCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _store = new();

        public byte[]? Get(string key) => _store.TryGetValue(key, out var v) ? v : null;

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _store[key] = value;

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            _store[key] = value;
            return Task.CompletedTask;
        }

        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key) => _store.TryRemove(key, out _);
        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            _store.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public bool Contains(string key) => _store.ContainsKey(key);
    }

    private static RedisCacheService NewService(out InMemoryDistributedCache store)
    {
        store = new InMemoryDistributedCache();
        return new RedisCacheService(store, new NullLogger<RedisCacheService>());
    }

    // A plain, cache-safe DTO — no navigation properties, no cycles.
    private sealed record PipelineSummaryDto(Guid Id, string Name, int StepCount, DateTime CreatedAt);

    /// <summary>
    /// Reproduces the EF Core entity shape that originally broke caching: a
    /// parent with a collection of children, where each child holds a back-
    /// reference to the parent. With ReferenceHandler.IgnoreCycles the cycle is
    /// tolerated but the child's back-reference is dropped, so the round trip is
    /// lossy and the guard must reject it.
    /// </summary>
    private sealed class CyclicEntity
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public List<CyclicChild> Children { get; set; } = new();
    }

    private sealed class CyclicChild
    {
        public Guid Id { get; set; }
        public CyclicEntity? Parent { get; set; } // navigation property -> cycle
    }

    [Fact]
    public async Task GetOrSetAsync_RoundTripsPlainDto()
    {
        var svc = NewService(out var store);
        var dto = new PipelineSummaryDto(Guid.NewGuid(), "my-package", 3, DateTime.UtcNow);

        var first = await svc.GetOrSetAsync<PipelineSummaryDto?>("dto:1", () => Task.FromResult<PipelineSummaryDto?>(dto));
        var second = await svc.GetOrSetAsync<PipelineSummaryDto?>("dto:1", () => throw new Exception("factory must not run on cache hit"));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(dto, second);
        Assert.True(store.Contains("dto:1"));
    }

    [Fact]
    public async Task GetOrSetAsync_RejectsCyclicEntityAndDoesNotCorruptCache()
    {
        // This is the regression for the silent-corruption bug: an EF entity
        // with navigation properties must NOT be stored, because deserializing
        // it would yield null/default and make the data "disappear".
        var svc = NewService(out var store);
        var parent = new CyclicEntity { Id = Guid.NewGuid(), Name = "pkg" };
        var child = new CyclicChild { Id = Guid.NewGuid(), Parent = parent };
        parent.Children.Add(child);

        var result = await svc.GetOrSetAsync<CyclicEntity?>("entity:1", () => Task.FromResult<CyclicEntity?>(parent));

        // The factory value is still returned (fail-closed, not fail-dead).
        Assert.Same(parent, result);
        // ...but nothing was written to the cache, so no future hit can return
        // a silently-corrupted value.
        Assert.False(store.Contains("entity:1"));
    }

    [Fact]
    public async Task GetOrSetAsync_NullFactoryResultIsNotStored()
    {
        var svc = NewService(out var store);
        var result = await svc.GetOrSetAsync<PipelineSummaryDto?>("null:1", () => Task.FromResult<PipelineSummaryDto?>(null));

        Assert.Null(result);
        Assert.False(store.Contains("null:1"));
    }

    [Fact]
    public async Task GetOrSetAsync_RoundTripsDtoList()
    {
        var svc = NewService(out var store);
        var list = new List<PipelineSummaryDto>
        {
            new(Guid.NewGuid(), "a", 1, DateTime.UtcNow),
            new(Guid.NewGuid(), "b", 2, DateTime.UtcNow),
        };

        var first = await svc.GetOrSetAsync<List<PipelineSummaryDto>?>("list:1", () => Task.FromResult<List<PipelineSummaryDto>?>(list));
        var second = await svc.GetOrSetAsync<List<PipelineSummaryDto>?>("list:1", () => throw new Exception("factory must not run on cache hit"));

        Assert.NotNull(second);
        Assert.Equal(list.Count, second!.Count);
        Assert.Equal(list, second);
    }
}
