using System.Globalization;

namespace Sonos2Mqtt.Client;

public class SonosSpeakerClient(SonosSoapClient soap, Uri baseAddress) : ISonosSpeakerClient
{
    private static readonly KeyValuePair<string, string> InstanceId = new("InstanceID", "0");
    private static readonly KeyValuePair<string, string> MasterChannel = new("Channel", "Master");

    public Uri BaseAddress { get; } = baseAddress;

    public Task<IReadOnlyDictionary<string, string>> InvokeAsync(SonosService service, string action, IEnumerable<KeyValuePair<string, string>> arguments, CancellationToken cancellationToken)
    {
        return soap.InvokeAsync(BaseAddress, service, action, arguments, cancellationToken);
    }

    public Task PlayAsync(CancellationToken cancellationToken) =>
        AVTransportAsync("Play", cancellationToken, ("Speed", "1"));

    public Task PauseAsync(CancellationToken cancellationToken) => AVTransportAsync("Pause", cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => AVTransportAsync("Stop", cancellationToken);
    public Task NextAsync(CancellationToken cancellationToken) => AVTransportAsync("Next", cancellationToken);
    public Task PreviousAsync(CancellationToken cancellationToken) => AVTransportAsync("Previous", cancellationToken);

    public Task SeekAsync(string unit, string target, CancellationToken cancellationToken) =>
        AVTransportAsync("Seek", cancellationToken, ("Unit", unit), ("Target", target));

    public Task SetAVTransportUriAsync(string uri, string metadata, CancellationToken cancellationToken) =>
        AVTransportAsync("SetAVTransportURI", cancellationToken, ("CurrentURI", uri), ("CurrentURIMetaData", metadata));

    public Task AddUriToQueueAsync(string uri, string metadata, int desiredFirstTrackNumberEnqueued, bool enqueueAsNext, CancellationToken cancellationToken) =>
        AVTransportAsync("AddURIToQueue", cancellationToken,
            ("EnqueuedURI", uri),
            ("EnqueuedURIMetaData", metadata),
            ("DesiredFirstTrackNumberEnqueued", desiredFirstTrackNumberEnqueued.ToString(CultureInfo.InvariantCulture)),
            ("EnqueueAsNext", enqueueAsNext ? "1" : "0"));

    public Task RemoveAllTracksFromQueueAsync(CancellationToken cancellationToken) =>
        AVTransportAsync("RemoveAllTracksFromQueue", cancellationToken);

    public Task SetPlayModeAsync(string playMode, CancellationToken cancellationToken) =>
        AVTransportAsync("SetPlayMode", cancellationToken, ("NewPlayMode", playMode));

    public Task ConfigureSleepTimerAsync(string duration, CancellationToken cancellationToken) =>
        AVTransportAsync("ConfigureSleepTimer", cancellationToken, ("NewSleepTimerDuration", duration));

    public Task BecomeCoordinatorOfStandaloneGroupAsync(CancellationToken cancellationToken) =>
        AVTransportAsync("BecomeCoordinatorOfStandaloneGroup", cancellationToken);

    public async Task<TransportInfo> GetTransportInfoAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(SonosService.AVTransport, "GetTransportInfo", [InstanceId], cancellationToken);
        return new TransportInfo(result.GetValueOrDefault("CurrentTransportState", ""));
    }

    public async Task<MediaInfo> GetMediaInfoAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(SonosService.AVTransport, "GetMediaInfo", [InstanceId], cancellationToken);
        return new MediaInfo(
            ParseInt(result.GetValueOrDefault("NrTracks")),
            result.GetValueOrDefault("MediaDuration", ""),
            result.GetValueOrDefault("CurrentURI", ""),
            result.GetValueOrDefault("CurrentURIMetaData", ""));
    }

    public async Task<PositionInfo> GetPositionInfoAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(SonosService.AVTransport, "GetPositionInfo", [InstanceId], cancellationToken);
        return new PositionInfo(
            ParseInt(result.GetValueOrDefault("Track")),
            result.GetValueOrDefault("TrackDuration", ""),
            result.GetValueOrDefault("TrackMetaData", ""),
            result.GetValueOrDefault("TrackURI", ""),
            result.GetValueOrDefault("RelTime", ""));
    }

    public async Task<int> GetVolumeAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(SonosService.RenderingControl, "GetVolume", [InstanceId, MasterChannel], cancellationToken);
        return ParseInt(result.GetValueOrDefault("CurrentVolume"));
    }

    public Task SetVolumeAsync(int volume, CancellationToken cancellationToken) =>
        InvokeAsync(SonosService.RenderingControl, "SetVolume",
            [InstanceId, MasterChannel, new("DesiredVolume", volume.ToString(CultureInfo.InvariantCulture))], cancellationToken);

    public Task SetRelativeVolumeAsync(int adjustment, CancellationToken cancellationToken) =>
        InvokeAsync(SonosService.RenderingControl, "SetRelativeVolume",
            [InstanceId, MasterChannel, new("Adjustment", adjustment.ToString(CultureInfo.InvariantCulture))], cancellationToken);

    public async Task<bool> GetMuteAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(SonosService.RenderingControl, "GetMute", [InstanceId, MasterChannel], cancellationToken);
        return result.GetValueOrDefault("CurrentMute") == "1";
    }

    public Task SetMuteAsync(bool mute, CancellationToken cancellationToken) =>
        InvokeAsync(SonosService.RenderingControl, "SetMute",
            [InstanceId, MasterChannel, new("DesiredMute", mute ? "1" : "0")], cancellationToken);

    public Task SetBassAsync(int bass, CancellationToken cancellationToken) =>
        InvokeAsync(SonosService.RenderingControl, "SetBass",
            [InstanceId, new("DesiredBass", bass.ToString(CultureInfo.InvariantCulture))], cancellationToken);

    public Task SetTrebleAsync(int treble, CancellationToken cancellationToken) =>
        InvokeAsync(SonosService.RenderingControl, "SetTreble",
            [InstanceId, new("DesiredTreble", treble.ToString(CultureInfo.InvariantCulture))], cancellationToken);

    public async Task<string> GetZoneGroupStateAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(SonosService.ZoneGroupTopology, "GetZoneGroupState", [], cancellationToken);
        return result.GetValueOrDefault("ZoneGroupState", "");
    }

    private Task AVTransportAsync(string action, CancellationToken cancellationToken, params (string Name, string Value)[] arguments)
    {
        return InvokeAsync(SonosService.AVTransport, action, [InstanceId, .. arguments.Select(x => KeyValuePair.Create(x.Name, x.Value))], cancellationToken);
    }

    private static int ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
