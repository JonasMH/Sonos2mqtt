using System.Xml.Linq;

namespace Sonos2Mqtt.Client;

/// <summary>
/// One visible speaker in the household, as the topology reports it.
/// </summary>
public record SonosZoneMember(string Uuid, string Name, Uri BaseAddress, string CoordinatorUuid, string GroupName)
{
    public bool IsCoordinator => Uuid == CoordinatorUuid;
}

public static class SonosTopology
{
    /// <summary>
    /// Parses the ZoneGroupState from ZoneGroupTopology.GetZoneGroupState. Bonded surrounds and subs are
    /// Invisible (or nested as Satellite elements) and cannot be controlled on their own, so they are left out.
    /// </summary>
    public static IReadOnlyList<SonosZoneMember> Parse(string zoneGroupState)
    {
        if (string.IsNullOrWhiteSpace(zoneGroupState))
        {
            return [];
        }

        var document = XDocument.Parse(zoneGroupState);
        var members = new List<SonosZoneMember>();

        // Newer firmware wraps ZoneGroups in a ZoneGroupState element, older firmware does not.
        foreach (var group in document.Descendants("ZoneGroup"))
        {
            var coordinatorUuid = (string?)group.Attribute("Coordinator") ?? "";
            var visible = group.Elements("ZoneGroupMember")
                .Where(x => (string?)x.Attribute("Invisible") != "1")
                .ToList();

            var coordinatorName = visible
                .Where(x => (string?)x.Attribute("UUID") == coordinatorUuid)
                .Select(x => (string?)x.Attribute("ZoneName"))
                .FirstOrDefault() ?? "";
            var groupName = visible.Count > 1 ? $"{coordinatorName} + {visible.Count - 1}" : coordinatorName;

            foreach (var member in visible)
            {
                var uuid = (string?)member.Attribute("UUID");
                var location = (string?)member.Attribute("Location");
                if (uuid == null || !Uri.TryCreate(location, UriKind.Absolute, out var locationUri))
                {
                    continue;
                }

                members.Add(new SonosZoneMember(
                    uuid,
                    (string?)member.Attribute("ZoneName") ?? uuid,
                    new Uri(locationUri.GetLeftPart(UriPartial.Authority)),
                    coordinatorUuid,
                    groupName));
            }
        }

        return members;
    }
}
