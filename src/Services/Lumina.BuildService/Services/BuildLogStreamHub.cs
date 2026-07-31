using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace Lumina.BuildService.Services;

public sealed record BuildLogSubscription(
    bool IsLive,
    string ExistingLogs,
    ChannelReader<string>? Reader,
    bool SubscriberLimitReached);

public interface IBuildLogStreamHub
{
    int MaximumBufferCharacters { get; }
    void Start(Guid buildJobId, string existingLogs = "");
    void Append(Guid buildJobId, object logLock, StringBuilder builder, string text);
    void UpdateSnapshot(Guid buildJobId, string logs);
    void Publish(Guid buildJobId, string line);
    Task<BuildLogSubscription> SubscribeAsync(Guid buildJobId);
    void Unsubscribe(Guid buildJobId, ChannelReader<string>? reader);
    void Complete(Guid buildJobId);
}

/// <summary>
/// Process-wide bounded live-log hub shared by every executor backend. Database
/// logs remain authoritative across restarts; this service only owns the live
/// replay window and bounded SSE channels for the current process.
/// </summary>
public sealed class BuildLogStreamHub : IBuildLogStreamHub, IDisposable
{
    private readonly ConcurrentDictionary<Guid, BuildLogState> _states = [];
    private readonly ConcurrentDictionary<ChannelReader<string>, byte> _liveReaders = [];
    private readonly SemaphoreSlim _subscriberGate;
    private readonly int _channelCapacity;

    public BuildLogStreamHub(IConfiguration configuration)
    {
        MaximumBufferCharacters = ReadPositive(
            configuration,
            "BuildStreaming:MaxLogBufferChars",
            2 * 1024 * 1024);
        var maximumSubscribers = ReadPositive(
            configuration,
            "BuildStreaming:MaxConcurrentSseSubscribers",
            64);
        _channelCapacity = ReadPositive(
            configuration,
            "BuildStreaming:SseChannelCapacity",
            1024);
        _subscriberGate = new SemaphoreSlim(maximumSubscribers, maximumSubscribers);
    }

    public int MaximumBufferCharacters { get; }

    public void Start(Guid buildJobId, string existingLogs = "")
    {
        var state = _states.GetOrAdd(buildJobId, _ => new BuildLogState());
        lock (state.Sync)
        {
            if (!state.Active)
                state.Buffer = Tail(existingLogs);
            state.Active = true;
        }
    }

    public void Append(Guid buildJobId, object logLock, StringBuilder builder, string text)
    {
        string snapshot;
        lock (logLock)
        {
            builder.Append(text);
            if (builder.Length > MaximumBufferCharacters)
                builder.Remove(0, builder.Length - MaximumBufferCharacters);
            snapshot = builder.ToString();
        }
        SetSnapshot(buildJobId, snapshot);
        PublishLines(buildJobId, text);
    }

    public void UpdateSnapshot(Guid buildJobId, string logs)
    {
        var state = _states.GetOrAdd(buildJobId, _ => new BuildLogState { Active = true });
        string delta;
        lock (state.Sync)
        {
            var previous = state.Buffer;
            var next = Tail(logs);
            var previousOffset = next.StartsWith(previous, StringComparison.Ordinal)
                ? 0
                : next.LastIndexOf(previous, StringComparison.Ordinal);
            delta = previous.Length == 0
                ? next
                : previousOffset >= 0
                    ? next[(previousOffset + previous.Length)..]
                    : string.Empty;
            state.Buffer = next;
        }
        if (delta.Length > 0)
            PublishLines(buildJobId, delta);
    }

    public void Publish(Guid buildJobId, string line)
    {
        if (!_states.TryGetValue(buildJobId, out var state))
            return;
        lock (state.Sync)
        {
            foreach (var channel in state.Subscribers)
                channel.Writer.TryWrite(line);
        }
    }

    public async Task<BuildLogSubscription> SubscribeAsync(Guid buildJobId)
    {
        if (!await _subscriberGate.WaitAsync(TimeSpan.Zero))
        {
            var deniedSnapshot = _states.TryGetValue(buildJobId, out var deniedState)
                ? Snapshot(deniedState)
                : string.Empty;
            return new BuildLogSubscription(false, deniedSnapshot, null, true);
        }

        if (!_states.TryGetValue(buildJobId, out var state))
        {
            _subscriberGate.Release();
            return new BuildLogSubscription(false, string.Empty, null, false);
        }

        Channel<string>? channel = null;
        string snapshot;
        lock (state.Sync)
        {
            snapshot = state.Buffer;
            if (state.Active)
            {
                channel = Channel.CreateBounded<string>(new BoundedChannelOptions(_channelCapacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest
                });
                state.Subscribers.Add(channel);
                _liveReaders.TryAdd(channel.Reader, 0);
            }
        }

        if (channel == null)
            _subscriberGate.Release();
        return new BuildLogSubscription(channel != null, snapshot, channel?.Reader, false);
    }

    public void Unsubscribe(Guid buildJobId, ChannelReader<string>? reader)
    {
        if (reader == null)
            return;
        if (!_liveReaders.TryRemove(reader, out _))
            return;
        if (_states.TryGetValue(buildJobId, out var state))
        {
            lock (state.Sync)
            {
                state.Subscribers.RemoveAll(channel => ReferenceEquals(channel.Reader, reader));
            }
        }
        _subscriberGate.Release();
    }

    public void Complete(Guid buildJobId)
    {
        if (!_states.TryRemove(buildJobId, out var state))
            return;
        List<Channel<string>> subscribers;
        lock (state.Sync)
        {
            state.Active = false;
            subscribers = state.Subscribers.ToList();
            state.Subscribers.Clear();
        }
        foreach (var channel in subscribers)
            channel.Writer.TryComplete();
    }

    public void Dispose() => _subscriberGate.Dispose();

    private void SetSnapshot(Guid buildJobId, string logs)
    {
        var state = _states.GetOrAdd(buildJobId, _ => new BuildLogState { Active = true });
        lock (state.Sync)
            state.Buffer = Tail(logs);
    }

    private void PublishLines(Guid buildJobId, string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0)
                Publish(buildJobId, trimmed);
        }
    }

    private string Tail(string value) => value.Length <= MaximumBufferCharacters
        ? value
        : value[^MaximumBufferCharacters..];

    private static string Snapshot(BuildLogState state)
    {
        lock (state.Sync)
            return state.Buffer;
    }

    private static int ReadPositive(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], out var value) && value > 0 ? value : fallback;

    private sealed class BuildLogState
    {
        public object Sync { get; } = new();
        public string Buffer { get; set; } = string.Empty;
        public bool Active { get; set; }
        public List<Channel<string>> Subscribers { get; } = [];
    }
}
