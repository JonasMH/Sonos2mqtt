using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sonos2Mqtt.Client;

namespace Sonos2Mqtt.Test;

public class SonosNotificationPlayerTests
{
    private const string Clip = "https://ha.home.jonasmhansen.com/local/halloween/monster.mp3";

    private readonly FakeSpeakerClient _speaker = new();
    private readonly SonosNotificationPlayer _sut = new(
        Options.Create(new SonosOptions { NotificationPollInterval = TimeSpan.FromMilliseconds(1) }),
        NullLogger<SonosNotificationPlayer>.Instance);

    [Fact]
    public async Task IdleSpeakerPlaysClipAndGetsItsSourceAndVolumeBack()
    {
        _speaker.Volume = 20;
        _speaker.Media = new MediaInfo(12, "NOT_IMPLEMENTED", "x-rincon-queue:RINCON_38420B960A1201400#0", "");
        _speaker.Position = new PositionInfo(4, "0:03:00", "", "x-sonos-spotify:abc", "0:01:10");
        _speaker.UpcomingTransportStates.Enqueue(TransportStates.Transitioning);
        _speaker.UpcomingTransportStates.Enqueue(TransportStates.Playing);
        _speaker.UpcomingTransportStates.Enqueue(TransportStates.Stopped);

        var played = await _sut.PlayAsync(_speaker, new SonosNotification(Clip, Timeout: TimeSpan.FromSeconds(10), Volume: 60, DelayMs: 1), TestContext.Current.CancellationToken);

        Assert.True(played);
        Assert.Equal(
        [
            $"SetAVTransportURI({Clip}, )",
            "SetVolume(60)",
            "Play",
            "SetVolume(20)",
            "SetAVTransportURI(x-rincon-queue:RINCON_38420B960A1201400#0, )",
            "Seek(TRACK_NR, 4)",
            "Seek(REL_TIME, 0:01:10)",
        ], _speaker.Calls);
    }

    [Fact]
    public async Task PlayingSpeakerResumesAfterwards()
    {
        _speaker.TransportState = TransportStates.Playing;
        _speaker.Media = new MediaInfo(1, "0:00:00", "x-sonosapi-stream:s1234?sid=254", "<upnp:class>object.item.audioItem.audioBroadcast</upnp:class>");
        _speaker.Position = new PositionInfo(1, "0:00:00", "", "x-sonosapi-stream:s1234?sid=254", "0:12:00");
        _speaker.UpcomingTransportStates.Enqueue(TransportStates.Playing);
        _speaker.UpcomingTransportStates.Enqueue(TransportStates.Stopped);

        await _sut.PlayAsync(_speaker, new SonosNotification(Clip), TestContext.Current.CancellationToken);

        // Radio: no volume given so none restored, no track or position to seek back to, but it plays on.
        Assert.Equal(
        [
            $"SetAVTransportURI({Clip}, )",
            "Play",
            "SetAVTransportURI(x-sonosapi-stream:s1234?sid=254, <upnp:class>object.item.audioItem.audioBroadcast</upnp:class>)",
            "Play",
        ], _speaker.Calls);
    }

    [Fact]
    public async Task OnlyWhenPlayingSkipsAnIdleSpeaker()
    {
        var played = await _sut.PlayAsync(_speaker, new SonosNotification(Clip, OnlyWhenPlaying: true), TestContext.Current.CancellationToken);

        Assert.False(played);
        Assert.Empty(_speaker.Calls);
    }

    [Fact]
    public async Task SameVolumeIsNotSetOrRestored()
    {
        _speaker.Volume = 60;
        _speaker.UpcomingTransportStates.Enqueue(TransportStates.Playing);
        _speaker.UpcomingTransportStates.Enqueue(TransportStates.Stopped);

        await _sut.PlayAsync(_speaker, new SonosNotification(Clip, Volume: 60), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(_speaker.Calls, x => x.StartsWith("SetVolume", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TimeoutCutsALongClipShort()
    {
        _speaker.Volume = 20;
        _speaker.UpcomingTransportStates.Enqueue(TransportStates.Playing);

        await _sut.PlayAsync(_speaker, new SonosNotification(Clip, Timeout: TimeSpan.FromMilliseconds(50), Volume: 60), TestContext.Current.CancellationToken);

        Assert.Equal("SetVolume(20)", _speaker.Calls[3]);
    }

    [Fact]
    public async Task FailedRestoreStepDoesNotStopTheRest()
    {
        _speaker.TransportState = TransportStates.Playing;
        _speaker.Media = new MediaInfo(12, "NOT_IMPLEMENTED", "x-rincon-queue:RINCON_X#0", "");
        _speaker.Position = new PositionInfo(4, "0:03:00", "", "", "0:01:10");
        _speaker.UpcomingTransportStates.Enqueue(TransportStates.Playing);
        _speaker.UpcomingTransportStates.Enqueue(TransportStates.Stopped);
        _speaker.FailingActions.Add("Seek");

        await _sut.PlayAsync(_speaker, new SonosNotification(Clip), TestContext.Current.CancellationToken);

        Assert.Equal("Play", _speaker.Calls[^1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task VolumeOutOfRangeIsRejected(int volume)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.PlayAsync(_speaker, new SonosNotification(Clip, Volume: volume), TestContext.Current.CancellationToken));
        Assert.Empty(_speaker.Calls);
    }
}
