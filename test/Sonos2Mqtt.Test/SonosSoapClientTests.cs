using Sonos2Mqtt.Client;

namespace Sonos2Mqtt.Test;

public class SonosSoapClientTests
{
    [Fact]
    public void EnvelopeEscapesArguments()
    {
        var envelope = SonosSoapClient.BuildEnvelope(SonosService.AVTransport, "SetAVTransportURI",
        [
            new("InstanceID", "0"),
            new("CurrentURI", "x-rincon-cpcontainer:1006206cspotify:playlist:1?sid=9&flags=8300&sn=7"),
            new("CurrentURIMetaData", "<DIDL-Lite><item id=\"1\"/></DIDL-Lite>"),
        ]);

        Assert.Contains("<u:SetAVTransportURI xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">", envelope);
        Assert.Contains("<InstanceID>0</InstanceID>", envelope);
        Assert.Contains("<CurrentURI>x-rincon-cpcontainer:1006206cspotify:playlist:1?sid=9&amp;flags=8300&amp;sn=7</CurrentURI>", envelope);
        Assert.Contains("<CurrentURIMetaData>&lt;DIDL-Lite&gt;&lt;item id=&quot;1&quot;/&gt;&lt;/DIDL-Lite&gt;</CurrentURIMetaData>", envelope);
    }

    [Fact]
    public void ResponseValuesAreUnescaped()
    {
        var result = SonosSoapClient.ParseResponse(
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>"
            + "<u:GetZoneGroupStateResponse xmlns:u=\"urn:schemas-upnp-org:service:ZoneGroupTopology:1\">"
            + "<ZoneGroupState>&lt;ZoneGroupState&gt;&lt;/ZoneGroupState&gt;</ZoneGroupState>"
            + "</u:GetZoneGroupStateResponse></s:Body></s:Envelope>");

        Assert.Equal("<ZoneGroupState></ZoneGroupState>", result["ZoneGroupState"]);
    }

    [Fact]
    public void FaultCarriesTheUpnpErrorCode()
    {
        var fault = SonosSoapClient.ParseFault(SonosService.AVTransport, "Play", 500,
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><faultcode>s:Client</faultcode>"
            + "<faultstring>UPnPError</faultstring><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\">"
            + "<errorCode>701</errorCode></UPnPError></detail></s:Fault></s:Body></s:Envelope>");

        Assert.Equal(701, fault.ErrorCode);
        Assert.Equal("AVTransportService.Play failed: UPnP error 701 (Transition not available)", fault.Message);
    }

    [Fact]
    public void FaultWithoutSoapBodyFallsBackToHttpStatus()
    {
        var fault = SonosSoapClient.ParseFault(SonosService.RenderingControl, "GetVolume", 503, "Service Unavailable");

        Assert.Null(fault.ErrorCode);
        Assert.Equal("RenderingControlService.GetVolume failed: HTTP 503", fault.Message);
    }

    [Theory]
    [InlineData("AVTransportService")]
    [InlineData("AVTransport")]
    [InlineData("avtransportservice")]
    public void ServiceIsFoundWithOrWithoutSuffix(string name)
    {
        Assert.True(SonosService.TryGetByName(name, out var service));
        Assert.Same(SonosService.AVTransport, service);
    }

    [Theory]
    [InlineData("192.168.0.9", "http://192.168.0.9:1400/")]
    [InlineData("192.168.0.9:1400", "http://192.168.0.9:1400/")]
    [InlineData("192.168.0.9:1443", "http://192.168.0.9:1443/")]
    [InlineData("http://192.168.0.9:1400/xml/device_description.xml", "http://192.168.0.9:1400/")]
    public void SeedHostsBecomeBaseAddresses(string host, string expected)
    {
        Assert.Equal(new Uri(expected), SonosHouseholdService.ToBaseAddress(host));
    }
}
