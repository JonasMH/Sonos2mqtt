using System.Diagnostics;
using Sonos2Mqtt.Client;

namespace Sonos2Mqtt.Test;

public class SonosEventTests
{
    private static SonosSpeaker CreateSpeaker()
    {
        var client = new FakeSpeakerClient();
        return new SonosSpeaker(new SonosZoneMember("RINCON_A", "Bedroom", client.BaseAddress, "RINCON_A", "Bedroom"), client);
    }

    [Theory]
    [InlineData("Second-1800", 1800)]
    [InlineData("second-60", 60)]
    public void TimeoutHeaderIsParsed(string header, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), SonosEventSubscriber.ParseTimeout(header));
    }

    [Theory]
    [InlineData("infinite")]
    [InlineData("Second-")]
    [InlineData("Second-0")]
    public void UnusableTimeoutHeaderIsIgnored(string header)
    {
        Assert.Null(SonosEventSubscriber.ParseTimeout(header));
    }

    [Fact]
    public async Task WakeCutsTheWaitShort()
    {
        var speaker = CreateSpeaker();
        var stopwatch = Stopwatch.StartNew();

        speaker.Wake();
        await speaker.WaitForWakeAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task BurstOfEventsIsOneWakeUp()
    {
        var speaker = CreateSpeaker();
        speaker.Wake();
        speaker.Wake();
        speaker.Wake();

        await speaker.WaitForWakeAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var stopwatch = Stopwatch.StartNew();
        await speaker.WaitForWakeAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(90));
    }

    [Fact]
    public void EventsCountOnlyWhileSubscribedAndArriving()
    {
        var speaker = CreateSpeaker();
        Assert.False(speaker.HasEvents);

        speaker.SubscribedUntil = DateTimeOffset.UtcNow.AddMinutes(30);
        Assert.False(speaker.HasEvents);

        speaker.EventsArriving = true;
        Assert.True(speaker.HasEvents);

        speaker.SubscribedUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.False(speaker.HasEvents);
    }
}
