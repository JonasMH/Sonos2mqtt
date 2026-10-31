using Microsoft.Extensions.Options;
using Sonos2Mqtt.Client;

namespace Sonos2Mqtt;

/// <summary>
/// The notify command's input, as sonos2mqtt took it.
/// </summary>
/// <param name="TrackUri">What to play; guessed into URI and metadata unless <paramref name="Metadata"/> is given.</param>
/// <param name="OnlyWhenPlaying">Skip the notification if the speaker is not playing already.</param>
/// <param name="Timeout">Longest the notification may play before the speaker is put back.</param>
/// <param name="Volume">Volume to play the notification at, 1-100. The old volume is put back afterwards.</param>
/// <param name="DelayMs">Pause after changing volume and between restore steps, for speakers that need it.</param>
public record SonosNotification(
    string TrackUri,
    string? Metadata = null,
    bool OnlyWhenPlaying = false,
    TimeSpan? Timeout = null,
    int? Volume = null,
    int? DelayMs = null);

/// <summary>
/// Plays a clip on a speaker and puts back whatever it was doing - a port of node-sonos-ts's PlayNotification.
/// </summary>
/// <remarks>
/// Commands for a speaker run one at a time, so a second notification waits for the first to be restored and
/// then takes its own snapshot. sonos2mqtt played queued notifications back to back and restored once.
/// </remarks>
public class SonosNotificationPlayer(IOptions<SonosOptions> options, ILogger<SonosNotificationPlayer> logger)
{
    /// <summary>
    /// How long a notification gets to start before a speaker still reporting STOPPED counts as done - the clip
    /// failed to load, most likely.
    /// </summary>
    private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(3);

    private record Snapshot(string TransportState, MediaInfo Media, PositionInfo Position, int Volume);

    /// <returns>Whether the notification played.</returns>
    public async Task<bool> PlayAsync(ISonosSpeakerClient speaker, SonosNotification notification, CancellationToken cancellationToken)
    {
        if (notification.Volume is < 1 or > 100)
        {
            throw new ArgumentException("Volume needs to be between 1 and 100");
        }
        if (notification.DelayMs is < 1 or > 4000)
        {
            throw new ArgumentException("Delay (if specified) should be between 1 and 4000");
        }

        var snapshot = await TakeSnapshotAsync(speaker, cancellationToken);

        if (notification.OnlyWhenPlaying && !TransportStates.IsActive(snapshot.TransportState))
        {
            logger.LogInformation("Skipping notification {trackUri}, the speaker is not playing", notification.TrackUri);
            return false;
        }

        var (uri, metadata) = notification.Metadata != null
            ? (notification.TrackUri, notification.Metadata)
            : SonosMetadata.Guess(notification.TrackUri, options.Value.SpotifyRegion);

        logger.LogInformation("Playing notification {trackUri}", notification.TrackUri);

        await TryAsync("Starting the notification", () => speaker.SetAVTransportUriAsync(uri, metadata, cancellationToken));

        var volumeChanged = notification.Volume is int volume && volume != snapshot.Volume;
        if (volumeChanged)
        {
            await TryAsync("Setting the notification volume", () => speaker.SetVolumeAsync(notification.Volume!.Value, cancellationToken));
            await DelayAsync(notification.DelayMs, cancellationToken);
        }

        await TryAsync("Playing the notification", () => speaker.PlayAsync(cancellationToken));
        await WaitForPlaybackToEndAsync(speaker, notification.Timeout ?? options.Value.DefaultNotificationTimeout, cancellationToken);

        await RestoreAsync(speaker, snapshot, volumeChanged, notification.DelayMs, cancellationToken);
        return true;
    }

    private static async Task<Snapshot> TakeSnapshotAsync(ISonosSpeakerClient speaker, CancellationToken cancellationToken)
    {
        var transport = speaker.GetTransportInfoAsync(cancellationToken);
        var media = speaker.GetMediaInfoAsync(cancellationToken);
        var position = speaker.GetPositionInfoAsync(cancellationToken);
        var volume = speaker.GetVolumeAsync(cancellationToken);

        return new Snapshot((await transport).State, await media, await position, await volume);
    }

    private async Task WaitForPlaybackToEndAsync(ISonosSpeakerClient speaker, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var seenPlaying = false;

        while (DateTimeOffset.UtcNow - started < timeout)
        {
            await Task.Delay(options.Value.NotificationPollInterval, cancellationToken);

            string state;
            try
            {
                state = (await speaker.GetTransportInfoAsync(cancellationToken)).State;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Reading the transport state during a notification failed");
                continue;
            }

            if (TransportStates.IsActive(state))
            {
                seenPlaying = true;
            }
            else if (seenPlaying || DateTimeOffset.UtcNow - started > StartGrace)
            {
                return;
            }
        }

        logger.LogInformation("Notification still playing after {timeout}, restoring anyway", timeout);
    }

    /// <summary>
    /// Put back volume, source, track and position, and resume if the speaker was playing. Every step is tried
    /// even if an earlier one failed - seeking fails on radio and some music services, and that should not
    /// leave the speaker silent.
    /// </summary>
    private async Task RestoreAsync(ISonosSpeakerClient speaker, Snapshot snapshot, bool volumeChanged, int? delayMs, CancellationToken cancellationToken)
    {
        if (volumeChanged)
        {
            await TryAsync("Restoring the volume", () => speaker.SetVolumeAsync(snapshot.Volume, cancellationToken));
            await DelayAsync(delayMs, cancellationToken);
        }

        if (!string.IsNullOrEmpty(snapshot.Media.CurrentUri))
        {
            await TryAsync("Restoring the source", () => speaker.SetAVTransportUriAsync(snapshot.Media.CurrentUri, snapshot.Media.CurrentUriMetaData, cancellationToken));
            await DelayAsync(delayMs, cancellationToken);
        }

        if (snapshot.Position.Track > 1 && snapshot.Media.NrTracks > 1)
        {
            await TryAsync("Restoring the track", () => speaker.SeekAsync("TRACK_NR", snapshot.Position.Track.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken));
            await DelayAsync(delayMs, cancellationToken);
        }

        if (!string.IsNullOrEmpty(snapshot.Position.RelTime)
            && snapshot.Position.RelTime != "NOT_IMPLEMENTED"
            && snapshot.Media.MediaDuration != "0:00:00"
            && !SonosMetadata.IsBroadcast(snapshot.Media.CurrentUriMetaData))
        {
            await TryAsync("Restoring the position", () => speaker.SeekAsync("REL_TIME", snapshot.Position.RelTime, cancellationToken));
            await DelayAsync(delayMs, cancellationToken);
        }

        if (TransportStates.IsActive(snapshot.TransportState))
        {
            await TryAsync("Resuming playback", () => speaker.PlayAsync(cancellationToken));
        }
    }

    private async Task TryAsync(string step, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (SonosSoapException ex)
        {
            logger.LogDebug("{step} failed: {message}", step, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("{step} failed: {message}", step, ex.Message);
        }
    }

    private static Task DelayAsync(int? delayMs, CancellationToken cancellationToken) =>
        delayMs is int ms ? Task.Delay(ms, cancellationToken) : Task.CompletedTask;
}
