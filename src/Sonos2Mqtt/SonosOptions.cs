namespace Sonos2Mqtt;

public class SonosOptions
{
    public const string Section = "Sonos";

    /// <summary>
    /// Speakers to ask for the household topology, as "host" or "host:port". They are tried in order and one
    /// answer is enough - the rest of the household is found through it. Speakers found earlier are tried after
    /// these, so the list only has to be right on startup.
    /// </summary>
    public List<string> SeedHosts { get; set; } = [];

    /// <summary>
    /// How often each reachable speaker is polled for its state. State is only published when it changed.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How often a speaker that stopped answering is tried again. Kept well above <see cref="PollInterval"/>
    /// so an unplugged speaker costs a request now and then, not every second.
    /// </summary>
    public TimeSpan UnreachableRetryInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How often the household topology (which speakers exist, their addresses and groups) is refreshed.
    /// </summary>
    public TimeSpan TopologyInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Timeout for a single request to a speaker.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Longest a notification may play before the speaker is put back, when the command does not give a
    /// timeout of its own.
    /// </summary>
    public TimeSpan DefaultNotificationTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often the transport state is checked while waiting for a notification to finish.
    /// </summary>
    public TimeSpan NotificationPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Where speakers can reach this bridge to push events, e.g. "http://192.168.0.31:31604". Speakers then report
    /// changes as they happen and polling drops to <see cref="EventPollInterval"/>. Without it, every speaker is
    /// polled every <see cref="PollInterval"/>.
    /// </summary>
    public string? CallbackAddress { get; set; }

    /// <summary>
    /// How often a speaker with current event subscriptions is still polled, in case an event got lost.
    /// </summary>
    public TimeSpan EventPollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long event subscriptions are asked for. They are renewed halfway through.
    /// </summary>
    public TimeSpan SubscriptionTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Spotify account region used in the metadata of Spotify URIs. 2311 is Europe, 3079 is North America.
    /// </summary>
    public string SpotifyRegion { get; set; } = "2311";
}
