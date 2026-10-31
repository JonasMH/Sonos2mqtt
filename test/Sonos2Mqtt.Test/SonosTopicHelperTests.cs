using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Packets;
using ToMqttNet;

namespace Sonos2Mqtt.Test;

public class MqttConnectionServiceMock : IMqttConnectionService
{
    public MqttConnectionOptions MqttOptions => new()
    {
        NodeId = "sonos"
    };
    public event Func<MqttApplicationMessageReceivedEventArgs, Task>? OnApplicationMessageReceivedAsync;
    public event Func<MqttClientConnectedEventArgs, Task>? OnConnectAsync;
    public event Func<MqttClientDisconnectedEventArgs, Task>? OnDisconnectAsync;

    public Task PublishAsync(MqttApplicationMessage applicationMessages)
    {
        throw new NotImplementedException();
    }

    public Task SubscribeAsync(params MqttTopicFilter[] topics)
    {
        throw new NotImplementedException();
    }

    public Task UnsubscribeAsync(params string[] topics)
    {
        throw new NotImplementedException();
    }
}

public class SonosTopicHelperTests
{
    private readonly SonosTopicHelper _sut = new(new MqttConnectionServiceMock());

    [Fact]
    public void TopicsMatchSonos2Mqtt()
    {
        Assert.Equal("sonos/RINCON_38420B960A1201400", _sut.GetStateTopic("RINCON_38420B960A1201400"));
        Assert.Equal("sonos/+/control", _sut.GetControlTopicFilter());
    }

    [Theory]
    [InlineData("sonos/RINCON_38420B960A1201400/control", true, "RINCON_38420B960A1201400")]
    [InlineData("sonos/Master Bad/control", true, "Master Bad")]
    [InlineData("sonos/RINCON_38420B960A1201400", false, "")]
    [InlineData("sonos//control", false, "")]
    [InlineData("other/RINCON_38420B960A1201400/control", false, "")]
    [InlineData("sonos/a/b/control", false, "")]
    public void ControlTargetIsExtracted(string topic, bool matches, string target)
    {
        Assert.Equal(matches, _sut.TryGetControlTarget(topic, out var actual));
        Assert.Equal(target, actual);
    }
}
