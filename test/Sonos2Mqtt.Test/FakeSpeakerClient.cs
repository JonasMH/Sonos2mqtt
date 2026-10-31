using Sonos2Mqtt.Client;

namespace Sonos2Mqtt.Test;

/// <summary>
/// Records every write as a readable line and answers reads from settable state.
/// </summary>
public class FakeSpeakerClient(string host = "192.168.0.36") : ISonosSpeakerClient
{
    public List<string> Calls { get; } = [];

    /// <summary>
    /// States handed out by GetTransportInfo once Play has been called, one per call; the last one repeats.
    /// Before that, GetTransportInfo returns <see cref="TransportState"/>.
    /// </summary>
    public Queue<string> UpcomingTransportStates { get; } = new();

    public string TransportState { get; set; } = TransportStates.Stopped;
    public MediaInfo Media { get; set; } = new(0, "NOT_IMPLEMENTED", "", "");
    public PositionInfo Position { get; set; } = new(0, "", "", "", "");
    public int Volume { get; set; } = 20;
    public bool Muted { get; set; }

    /// <summary>
    /// Actions that throw a UPnP fault instead of succeeding.
    /// </summary>
    public HashSet<string> FailingActions { get; } = [];

    public Uri BaseAddress { get; } = new($"http://{host}:1400");

    private Task Record(string call)
    {
        var action = call.Split('(')[0];
        if (FailingActions.Contains(action))
        {
            throw new SonosSoapException(SonosService.AVTransport, action, 701, $"{action} failed");
        }
        Calls.Add(call);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> InvokeAsync(SonosService service, string action, IEnumerable<KeyValuePair<string, string>> arguments, CancellationToken cancellationToken)
    {
        Calls.Add($"{service.Name}.{action}({string.Join(", ", arguments.Select(x => $"{x.Key}={x.Value}"))})");
        return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["Result"] = "ok" });
    }

    private bool _played;

    public Task PlayAsync(CancellationToken cancellationToken)
    {
        _played = true;
        return Record("Play");
    }

    public Task PauseAsync(CancellationToken cancellationToken) => Record("Pause");
    public Task StopAsync(CancellationToken cancellationToken) => Record("Stop");
    public Task NextAsync(CancellationToken cancellationToken) => Record("Next");
    public Task PreviousAsync(CancellationToken cancellationToken) => Record("Previous");
    public Task SeekAsync(string unit, string target, CancellationToken cancellationToken) => Record($"Seek({unit}, {target})");
    public Task SetAVTransportUriAsync(string uri, string metadata, CancellationToken cancellationToken) => Record($"SetAVTransportURI({uri}, {metadata})");
    public Task AddUriToQueueAsync(string uri, string metadata, int desiredFirstTrackNumberEnqueued, bool enqueueAsNext, CancellationToken cancellationToken) =>
        Record($"AddURIToQueue({uri}, {metadata}, {desiredFirstTrackNumberEnqueued}, {enqueueAsNext})");
    public Task RemoveAllTracksFromQueueAsync(CancellationToken cancellationToken) => Record("RemoveAllTracksFromQueue");
    public Task SetPlayModeAsync(string playMode, CancellationToken cancellationToken) => Record($"SetPlayMode({playMode})");
    public Task ConfigureSleepTimerAsync(string duration, CancellationToken cancellationToken) => Record($"ConfigureSleepTimer({duration})");
    public Task BecomeCoordinatorOfStandaloneGroupAsync(CancellationToken cancellationToken) => Record("BecomeCoordinatorOfStandaloneGroup");

    public Task<TransportInfo> GetTransportInfoAsync(CancellationToken cancellationToken)
    {
        if (_played && UpcomingTransportStates.Count > 0)
        {
            TransportState = UpcomingTransportStates.Count > 1 ? UpcomingTransportStates.Dequeue() : UpcomingTransportStates.Peek();
        }
        return Task.FromResult(new TransportInfo(TransportState));
    }

    public Task<MediaInfo> GetMediaInfoAsync(CancellationToken cancellationToken) => Task.FromResult(Media);
    public Task<PositionInfo> GetPositionInfoAsync(CancellationToken cancellationToken) => Task.FromResult(Position);
    public Task<int> GetVolumeAsync(CancellationToken cancellationToken) => Task.FromResult(Volume);
    public Task SetVolumeAsync(int volume, CancellationToken cancellationToken) => Record($"SetVolume({volume})");
    public Task SetRelativeVolumeAsync(int adjustment, CancellationToken cancellationToken) => Record($"SetRelativeVolume({adjustment})");
    public Task<bool> GetMuteAsync(CancellationToken cancellationToken) => Task.FromResult(Muted);
    public Task SetMuteAsync(bool mute, CancellationToken cancellationToken) => Record($"SetMute({mute})");
    public Task SetBassAsync(int bass, CancellationToken cancellationToken) => Record($"SetBass({bass})");
    public Task SetTrebleAsync(int treble, CancellationToken cancellationToken) => Record($"SetTreble({treble})");
    public Task<string> GetZoneGroupStateAsync(CancellationToken cancellationToken) => Task.FromResult("");
}
