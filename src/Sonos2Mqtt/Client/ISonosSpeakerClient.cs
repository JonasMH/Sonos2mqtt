namespace Sonos2Mqtt.Client;

public record TransportInfo(string State);

public record MediaInfo(int NrTracks, string MediaDuration, string CurrentUri, string CurrentUriMetaData);

public record PositionInfo(int Track, string TrackDuration, string TrackMetaData, string TrackUri, string RelTime);

public static class TransportStates
{
    public const string Playing = "PLAYING";
    public const string Transitioning = "TRANSITIONING";
    public const string Stopped = "STOPPED";
    public const string PausedPlayback = "PAUSED_PLAYBACK";

    /// <summary>
    /// Whether the speaker is playing or about to - the two states sonos2mqtt treated as "playing".
    /// </summary>
    public static bool IsActive(string? state) => state is Playing or Transitioning;
}

/// <summary>
/// The actions this bridge uses on a single speaker. AVTransport actions only make sense on a group's
/// coordinator, except the ones that change what the speaker itself is playing (SetAVTransportURI and the queue).
/// </summary>
public interface ISonosSpeakerClient
{
    Uri BaseAddress { get; }

    Task<IReadOnlyDictionary<string, string>> InvokeAsync(SonosService service, string action, IEnumerable<KeyValuePair<string, string>> arguments, CancellationToken cancellationToken);

    Task PlayAsync(CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task NextAsync(CancellationToken cancellationToken);
    Task PreviousAsync(CancellationToken cancellationToken);
    Task SeekAsync(string unit, string target, CancellationToken cancellationToken);
    Task SetAVTransportUriAsync(string uri, string metadata, CancellationToken cancellationToken);
    Task AddUriToQueueAsync(string uri, string metadata, int desiredFirstTrackNumberEnqueued, bool enqueueAsNext, CancellationToken cancellationToken);
    Task RemoveAllTracksFromQueueAsync(CancellationToken cancellationToken);
    Task SetPlayModeAsync(string playMode, CancellationToken cancellationToken);
    Task ConfigureSleepTimerAsync(string duration, CancellationToken cancellationToken);
    Task BecomeCoordinatorOfStandaloneGroupAsync(CancellationToken cancellationToken);
    Task<TransportInfo> GetTransportInfoAsync(CancellationToken cancellationToken);
    Task<MediaInfo> GetMediaInfoAsync(CancellationToken cancellationToken);
    Task<PositionInfo> GetPositionInfoAsync(CancellationToken cancellationToken);

    Task<int> GetVolumeAsync(CancellationToken cancellationToken);
    Task SetVolumeAsync(int volume, CancellationToken cancellationToken);
    Task SetRelativeVolumeAsync(int adjustment, CancellationToken cancellationToken);
    Task<bool> GetMuteAsync(CancellationToken cancellationToken);
    Task SetMuteAsync(bool mute, CancellationToken cancellationToken);
    Task SetBassAsync(int bass, CancellationToken cancellationToken);
    Task SetTrebleAsync(int treble, CancellationToken cancellationToken);

    Task<string> GetZoneGroupStateAsync(CancellationToken cancellationToken);
}
