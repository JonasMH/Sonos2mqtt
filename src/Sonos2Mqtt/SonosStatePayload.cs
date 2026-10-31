using Sonos2Mqtt.Client;

namespace Sonos2Mqtt;

/// <summary>
/// The retained state on {NodeId}/{uuid}. Field names and shapes follow sonos2mqtt's, including the
/// channel-keyed volume and mute objects ("volume": {"Master": 12}), so automations reading
/// .transportState or .volume.Master need no change.
/// </summary>
public class SonosStatePayload
{
    public required string Uuid { get; init; }
    public required string Name { get; init; }
    public required string GroupName { get; init; }
    public required string CoordinatorUuid { get; init; }
    public string? TransportState { get; init; }
    public Dictionary<string, int>? Volume { get; init; }
    public Dictionary<string, bool>? Mute { get; init; }
    public SonosTrack? CurrentTrack { get; init; }

    /// <summary>
    /// When the state last changed, in Unix milliseconds.
    /// </summary>
    public long Ts { get; set; }
}

public class SonosErrorPayload
{
    public required string Command { get; init; }
    public required string Error { get; init; }
}
