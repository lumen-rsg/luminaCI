using System.Text;
using Lumina.BuildService.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class BuildLogStreamHubTests
{
    [Fact]
    public async Task Append_ReplaysBoundedSnapshotAndCompletesLiveReader()
    {
        using var hub = Hub(("BuildStreaming:MaxLogBufferChars", "5"));
        var jobId = Guid.NewGuid();
        hub.Start(jobId);
        var subscription = await hub.SubscribeAsync(jobId);

        hub.Append(jobId, new object(), new StringBuilder(), "123456\n");

        Assert.True(subscription.IsLive);
        Assert.Equal("123456", await subscription.Reader!.ReadAsync());
        var replay = await hub.SubscribeAsync(jobId);
        Assert.Equal("3456\n", replay.ExistingLogs);
        hub.Complete(jobId);
        Assert.False(await subscription.Reader.WaitToReadAsync());
        hub.Unsubscribe(jobId, subscription.Reader);
        hub.Unsubscribe(jobId, subscription.Reader);
        hub.Unsubscribe(jobId, replay.Reader);
    }

    [Fact]
    public async Task Subscribe_EnforcesGlobalLimitAndReleasesSlotExactlyOnce()
    {
        using var hub = Hub(("BuildStreaming:MaxConcurrentSseSubscribers", "1"));
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        hub.Start(firstId, "first");
        hub.Start(secondId, "second");
        var first = await hub.SubscribeAsync(firstId);

        var denied = await hub.SubscribeAsync(secondId);

        Assert.True(first.IsLive);
        Assert.False(denied.IsLive);
        Assert.True(denied.SubscriberLimitReached);
        Assert.Equal("second", denied.ExistingLogs);
        hub.Unsubscribe(firstId, first.Reader);
        hub.Unsubscribe(firstId, first.Reader);
        var second = await hub.SubscribeAsync(secondId);
        Assert.True(second.IsLive);
        hub.Unsubscribe(secondId, second.Reader);
    }

    [Fact]
    public async Task UpdateSnapshot_PublishesOnlyNewSuffix()
    {
        using var hub = Hub();
        var jobId = Guid.NewGuid();
        hub.Start(jobId, "line-1\n");
        var subscription = await hub.SubscribeAsync(jobId);

        hub.UpdateSnapshot(jobId, "line-1\nline-2\n");

        Assert.Equal("line-2", await subscription.Reader!.ReadAsync());
        hub.UpdateSnapshot(jobId, "replacement");
        Assert.False(subscription.Reader.TryRead(out _));
        hub.Unsubscribe(jobId, subscription.Reader);
    }

    private static BuildLogStreamHub Hub(params (string Key, string Value)[] values) => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(item =>
                new KeyValuePair<string, string?>(item.Key, item.Value)))
            .Build());
}
