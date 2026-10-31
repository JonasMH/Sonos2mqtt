using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sonos2Mqtt.Client;
using Sonos2Mqtt.Commands;

namespace Sonos2Mqtt.Test;

/// <summary>
/// The payloads are the ones the Home Assistant automations publish.
/// </summary>
public class SonosCommandExecutorTests
{
    private readonly SonosSpeakerRegistry _registry = new();
    private readonly SonosCommandExecutor _sut;

    private readonly FakeSpeakerClient _kitchenClient = new("192.168.0.9");
    private readonly FakeSpeakerClient _bathroomClient = new("192.168.0.252");
    private readonly SonosSpeaker _kitchen;
    private readonly SonosSpeaker _bathroom;

    public SonosCommandExecutorTests()
    {
        var options = Options.Create(new SonosOptions { NotificationPollInterval = TimeSpan.FromMilliseconds(1) });
        _sut = new SonosCommandExecutor(_registry, new SonosNotificationPlayer(options, NullLogger<SonosNotificationPlayer>.Instance), options);

        _kitchen = new SonosSpeaker(new SonosZoneMember("RINCON_38420B22579101400", "Kitchen", _kitchenClient.BaseAddress, "RINCON_38420B22579101400", "Kitchen + 1"), _kitchenClient);
        _bathroom = new SonosSpeaker(new SonosZoneMember("RINCON_542A1B5A6FC201400", "Master Bad", _bathroomClient.BaseAddress, "RINCON_38420B22579101400", "Kitchen + 1"), _bathroomClient);
        _registry.TryAdd(_kitchen);
        _registry.TryAdd(_bathroom);
    }

    private Task<IReadOnlyDictionary<string, string>?> RunAsync(SonosSpeaker speaker, string payload)
    {
        Assert.True(SonosCommandHandler.TryParseControl(payload, out var control));
        return _sut.ExecuteAsync(speaker, control.Command, control.Input, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task VolumeGoesToTheSpeakerItself()
    {
        await RunAsync(_bathroom, """{"command": "volume", "input": 42}""");

        Assert.Equal(["SetVolume(42)"], _bathroomClient.Calls);
        Assert.Empty(_kitchenClient.Calls);
    }

    [Fact]
    public async Task PlayAndPauseGoToTheCoordinator()
    {
        await RunAsync(_bathroom, """{"command": "play"}""");
        await RunAsync(_bathroom, """{"command": "pause"}""");

        Assert.Equal(["Play", "Pause"], _kitchenClient.Calls);
        Assert.Empty(_bathroomClient.Calls);
    }

    [Fact]
    public async Task ShowerSequenceQueuesThePlaylist()
    {
        await RunAsync(_bathroom, """{"command": "adv-command", "input": {"cmd": "AVTransportService.RemoveAllTracksFromQueue"}}""");
        await RunAsync(_bathroom, """{"command": "switchtoqueue"}""");
        await RunAsync(_bathroom, """{"command": "adv-command", "input": {"cmd": "AddUriToQueue", "val": "spotify:playlist:7fmF9tvzT2dYgZ8QKbwTY3"}}""");

        var (uri, metadata) = SonosMetadata.Guess("spotify:playlist:7fmF9tvzT2dYgZ8QKbwTY3", "2311");
        Assert.Equal(
        [
            "AVTransportService.RemoveAllTracksFromQueue(InstanceID=0)",
            "SetAVTransportURI(x-rincon-queue:RINCON_542A1B5A6FC201400#0, )",
            $"AddURIToQueue({uri}, {metadata}, 0, True)",
        ], _bathroomClient.Calls);
    }

    [Fact]
    public async Task NotifyPlaysOnTheSpeakerItself()
    {
        _bathroomClient.UpcomingTransportStates.Enqueue(TransportStates.Playing);
        _bathroomClient.UpcomingTransportStates.Enqueue(TransportStates.Stopped);

        await RunAsync(_bathroom, """
            {"command": "notify", "input": {"trackUri": "https://ha.home.jonasmhansen.com/local/ringbell01.mp3",
             "onlyWhenPlaying": false, "timeout": 10, "volume": 100, "delayMs": 700}}
            """);

        Assert.Equal("SetAVTransportURI(https://ha.home.jonasmhansen.com/local/ringbell01.mp3, )", _bathroomClient.Calls[0]);
        Assert.Equal("SetVolume(100)", _bathroomClient.Calls[1]);
        Assert.Empty(_kitchenClient.Calls);
    }

    [Fact]
    public void NotificationInputIsParsed()
    {
        using var document = JsonDocument.Parse("""{"trackUri": "x", "onlyWhenPlaying": true, "timeout": 10, "volume": 15, "delayMs": 700}""");

        var notification = SonosCommandExecutor.ParseNotification(document.RootElement);

        Assert.Equal(new SonosNotification("x", OnlyWhenPlaying: true, Timeout: TimeSpan.FromSeconds(10), Volume: 15, DelayMs: 700), notification);
    }

    [Fact]
    public async Task RawUpnpActionKeepsGivenArguments()
    {
        var result = await RunAsync(_kitchen, """{"command": "adv-command", "input": {"cmd": "RenderingControlService.SetLoudness", "val": {"Channel": "Master", "DesiredLoudness": true}}}""");

        Assert.Equal(["RenderingControlService.SetLoudness(InstanceID=0, Channel=Master, DesiredLoudness=1)"], _kitchenClient.Calls);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task JoinGroupJoinsTheTargetsCoordinator()
    {
        await RunAsync(_bathroom, """{"command": "joingroup", "input": "kitchen"}""");

        Assert.Equal(["SetAVTransportURI(x-rincon:RINCON_38420B22579101400, )"], _bathroomClient.Calls);
    }

    [Theory]
    [InlineData("""{"command": "volume", "input": 101}""")]
    [InlineData("""{"command": "volume"}""")]
    [InlineData("""{"command": "sleep", "input": 90}""")]
    public async Task BadInputIsRejected(string payload)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(_kitchen, payload));
        Assert.Empty(_kitchenClient.Calls);
    }

    [Fact]
    public async Task UnknownCommandIsRejected()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => RunAsync(_kitchen, """{"command": "dance"}"""));
    }

    [Theory]
    [InlineData("""{"command": "play"}""", true)]
    [InlineData("""{"command": "volume", "input": 42, "replyTopic": "r"}""", true)]
    [InlineData("""{"input": 42}""", false)]
    [InlineData("""play""", false)]
    [InlineData("""[]""", false)]
    public void ControlPayloadsAreValidated(string payload, bool valid)
    {
        Assert.Equal(valid, SonosCommandHandler.TryParseControl(payload, out _));
    }
}
