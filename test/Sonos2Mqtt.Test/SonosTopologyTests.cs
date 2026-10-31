using Sonos2Mqtt.Client;

namespace Sonos2Mqtt.Test;

public class SonosTopologyTests
{
    // Shaped like a real GetZoneGroupState answer, trimmed to the attributes that matter: a standalone speaker,
    // a two-room group, and a home theatre whose surround is an invisible member and whose sub is a Satellite.
    private const string ZoneGroupState =
        "<ZoneGroupState><ZoneGroups>"
        + "<ZoneGroup Coordinator=\"RINCON_38420B960A1201400\" ID=\"RINCON_38420B960A1201400:1909540435\">"
        + "<ZoneGroupMember UUID=\"RINCON_38420B960A1201400\" Location=\"http://192.168.0.36:1400/xml/device_description.xml\" ZoneName=\"Bedroom\" SoftwareVersion=\"97.2-81\"/>"
        + "</ZoneGroup>"
        + "<ZoneGroup Coordinator=\"RINCON_38420B22579101400\" ID=\"RINCON_38420B22579101400:1333370617\">"
        + "<ZoneGroupMember UUID=\"RINCON_38420B22579101400\" Location=\"http://192.168.0.9:1400/xml/device_description.xml\" ZoneName=\"Kitchen\"/>"
        + "<ZoneGroupMember UUID=\"RINCON_542A1B5A6FC201400\" Location=\"http://192.168.0.252:1400/xml/device_description.xml\" ZoneName=\"Master Bad\"/>"
        + "</ZoneGroup>"
        + "<ZoneGroup Coordinator=\"RINCON_000E58B89AFA01400\" ID=\"RINCON_000E58B89AFA01400:3995406074\">"
        + "<ZoneGroupMember UUID=\"RINCON_000E58B89AFA01400\" Location=\"http://192.168.0.39:1400/xml/device_description.xml\" ZoneName=\"Living Room\">"
        + "<Satellite UUID=\"RINCON_SUB000000001400\" Location=\"http://192.168.0.40:1400/xml/device_description.xml\" ZoneName=\"Living Room\" Invisible=\"1\"/>"
        + "</ZoneGroupMember>"
        + "<ZoneGroupMember UUID=\"RINCON_SURROUND00001400\" Location=\"http://192.168.0.41:1400/xml/device_description.xml\" ZoneName=\"Living Room\" Invisible=\"1\"/>"
        + "</ZoneGroup>"
        + "</ZoneGroups><VanishedDevices></VanishedDevices></ZoneGroupState>";

    [Fact]
    public void ParsesVisibleMembers()
    {
        var members = SonosTopology.Parse(ZoneGroupState);

        Assert.Equal(["Bedroom", "Kitchen", "Master Bad", "Living Room"], members.Select(x => x.Name));
    }

    [Fact]
    public void BaseAddressIsTheLocationsAuthority()
    {
        var bedroom = SonosTopology.Parse(ZoneGroupState).Single(x => x.Name == "Bedroom");

        Assert.Equal(new Uri("http://192.168.0.36:1400"), bedroom.BaseAddress);
        Assert.Equal("RINCON_38420B960A1201400", bedroom.Uuid);
        Assert.True(bedroom.IsCoordinator);
        Assert.Equal("Bedroom", bedroom.GroupName);
    }

    [Fact]
    public void GroupMembersShareCoordinatorAndGroupName()
    {
        var members = SonosTopology.Parse(ZoneGroupState);
        var kitchen = members.Single(x => x.Name == "Kitchen");
        var bathroom = members.Single(x => x.Name == "Master Bad");

        Assert.Equal("RINCON_38420B22579101400", bathroom.CoordinatorUuid);
        Assert.False(bathroom.IsCoordinator);
        Assert.Equal("Kitchen + 1", kitchen.GroupName);
        Assert.Equal("Kitchen + 1", bathroom.GroupName);
    }

    [Fact]
    public void OlderFirmwareWithoutTheWrapperParses()
    {
        var members = SonosTopology.Parse(
            "<ZoneGroups><ZoneGroup Coordinator=\"RINCON_A\" ID=\"RINCON_A:1\">"
            + "<ZoneGroupMember UUID=\"RINCON_A\" Location=\"http://10.0.0.2:1400/xml/device_description.xml\" ZoneName=\"Office\"/>"
            + "</ZoneGroup></ZoneGroups>");

        Assert.Equal("Office", Assert.Single(members).Name);
    }

    [Fact]
    public void EmptyStateHasNoMembers()
    {
        Assert.Empty(SonosTopology.Parse(""));
    }
}
