using Lumina.Shared.Events;
using Lumina.SourceService.Services;
using MassTransit;

namespace Lumina.SourceService.Consumers;

public sealed class RepositorySnapshotRequestedConsumer
    : IConsumer<RepositorySnapshotRequested>
{
    private readonly SourceFetchQueue _queue;

    public RepositorySnapshotRequestedConsumer(SourceFetchQueue queue)
    {
        _queue = queue;
    }

    public Task Consume(ConsumeContext<RepositorySnapshotRequested> context) =>
        _queue.EnqueueRepositorySnapshotAsync(
            context.Message,
            context.CancellationToken);
}
