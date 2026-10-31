namespace Sonos2Mqtt.Client;

/// <summary>
/// A UPnP service on a speaker. <see cref="Name"/> is the name sonos2mqtt's adv-command uses
/// ("AVTransportService.RemoveAllTracksFromQueue").
/// </summary>
public sealed class SonosService
{
    public static readonly SonosService AVTransport = new("AVTransportService", "/MediaRenderer/AVTransport/Control", "urn:schemas-upnp-org:service:AVTransport:1", takesInstanceId: true);
    public static readonly SonosService RenderingControl = new("RenderingControlService", "/MediaRenderer/RenderingControl/Control", "urn:schemas-upnp-org:service:RenderingControl:1", takesInstanceId: true);
    public static readonly SonosService GroupRenderingControl = new("GroupRenderingControlService", "/MediaRenderer/GroupRenderingControl/Control", "urn:schemas-upnp-org:service:GroupRenderingControl:1", takesInstanceId: true);
    public static readonly SonosService ZoneGroupTopology = new("ZoneGroupTopologyService", "/ZoneGroupTopology/Control", "urn:schemas-upnp-org:service:ZoneGroupTopology:1", takesInstanceId: false);
    public static readonly SonosService DeviceProperties = new("DevicePropertiesService", "/DeviceProperties/Control", "urn:schemas-upnp-org:service:DeviceProperties:1", takesInstanceId: false);
    public static readonly SonosService ContentDirectory = new("ContentDirectoryService", "/MediaServer/ContentDirectory/Control", "urn:schemas-upnp-org:service:ContentDirectory:1", takesInstanceId: false);

    public static readonly IReadOnlyList<SonosService> All = [AVTransport, RenderingControl, GroupRenderingControl, ZoneGroupTopology, DeviceProperties, ContentDirectory];

    private SonosService(string name, string controlPath, string urn, bool takesInstanceId)
    {
        Name = name;
        ControlPath = controlPath;
        Urn = urn;
        TakesInstanceId = takesInstanceId;
    }

    public string Name { get; }
    public string ControlPath { get; }
    public string Urn { get; }

    /// <summary>
    /// Every action on the service takes InstanceID, which is always 0 on Sonos.
    /// </summary>
    public bool TakesInstanceId { get; }

    /// <summary>
    /// Finds a service by name, with or without the "Service" suffix and ignoring case.
    /// </summary>
    public static bool TryGetByName(string name, out SonosService service)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Name[..^"Service".Length], name, StringComparison.OrdinalIgnoreCase))
            {
                service = candidate;
                return true;
            }
        }
        service = null!;
        return false;
    }

    public override string ToString() => Name;
}
