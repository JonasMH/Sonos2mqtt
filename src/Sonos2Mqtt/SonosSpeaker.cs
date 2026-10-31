using System.Collections.Concurrent;
using System.Threading.Channels;
using Sonos2Mqtt.Client;

namespace Sonos2Mqtt;

/// <summary>
/// A speaker in the household and everything kept about it between polls. Commands for the speaker go through
/// its own queue and run one at a time, in the order they arrived, so a notification is not interleaved with a
/// play or a volume change and a multi-step sequence lands in order.
/// </summary>
public class SonosSpeaker(SonosZoneMember member, ISonosSpeakerClient client)
{
    private readonly Channel<Func<CancellationToken, Task>> _commands =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });

    // Holds at most one pending wake-up, so a burst of events (TRANSITIONING, then PLAYING) costs one extra read
    // rather than one per event.
    private readonly Channel<bool> _wake =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public string Uuid => Member.Uuid;
    public string Name => Member.Name;

    /// <summary>
    /// Replaced on every topology refresh; groups and names change while the bridge runs.
    /// </summary>
    public SonosZoneMember Member { get; set; } = member;

    /// <summary>
    /// Replaced when the speaker turns up at a new address.
    /// </summary>
    public ISonosSpeakerClient Client { get; set; } = client;

    /// <summary>
    /// Null until the first poll, then whether the last poll got an answer.
    /// </summary>
    public bool? Reachable { get; set; }

    /// <summary>
    /// The last state published, without its timestamp, to tell whether anything changed.
    /// </summary>
    public string? LastPublishedState { get; set; }

    /// <summary>
    /// Until when the speaker's event subscriptions are known to be current. While they are, the speaker pushes
    /// changes and polling only has to be a safety net.
    /// </summary>
    public DateTimeOffset SubscribedUntil { get; set; } = DateTimeOffset.MinValue;

    /// <summary>
    /// Whether an event has arrived since the last new subscription. A speaker sends one straight after
    /// subscribing, so this is what proves events can actually reach the bridge.
    /// </summary>
    public bool EventsArriving { get; set; }

    public bool HasEvents => EventsArriving && SubscribedUntil > DateTimeOffset.UtcNow;

    public CancellationTokenSource Lifetime { get; } = new();

    /// <summary>
    /// Ask for the speaker's state to be read now, because it said something changed.
    /// </summary>
    public void Wake() => _wake.Writer.TryWrite(true);

    /// <summary>
    /// Waits for <paramref name="timeout"/> or until <see cref="Wake"/> is called, whichever comes first.
    /// </summary>
    public async Task WaitForWakeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await _wake.Reader.ReadAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    public ChannelReader<Func<CancellationToken, Task>> Commands => _commands.Reader;

    public bool TryEnqueue(Func<CancellationToken, Task> command) => _commands.Writer.TryWrite(command);

    public void Retire()
    {
        _commands.Writer.TryComplete();
        Lifetime.Cancel();
    }
}

public class SonosSpeakerRegistry
{
    private readonly ConcurrentDictionary<string, SonosSpeaker> _speakers = new(StringComparer.OrdinalIgnoreCase);

    public ICollection<SonosSpeaker> Speakers => _speakers.Values;

    public bool TryAdd(SonosSpeaker speaker) => _speakers.TryAdd(speaker.Uuid, speaker);

    public bool TryRemove(string uuid, out SonosSpeaker speaker) => _speakers.TryRemove(uuid, out speaker!);

    public bool TryGetByUuid(string uuid, out SonosSpeaker speaker) => _speakers.TryGetValue(uuid, out speaker!);

    /// <summary>
    /// Finds a speaker by uuid or, failing that, by its room name - both ignoring case.
    /// </summary>
    public bool TryGetSpeaker(string uuidOrName, out SonosSpeaker speaker)
    {
        if (_speakers.TryGetValue(uuidOrName, out speaker!))
        {
            return true;
        }

        speaker = _speakers.Values.FirstOrDefault(x => string.Equals(x.Name, uuidOrName, StringComparison.OrdinalIgnoreCase))!;
        return speaker != null;
    }

    /// <summary>
    /// The speaker that runs the group <paramref name="speaker"/> is in. Transport commands have to go there.
    /// Falls back to the speaker itself if the coordinator is not known (yet).
    /// </summary>
    public SonosSpeaker GetCoordinator(SonosSpeaker speaker)
    {
        return _speakers.TryGetValue(speaker.Member.CoordinatorUuid, out var coordinator) ? coordinator : speaker;
    }
}
