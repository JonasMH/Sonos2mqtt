using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Sonos2Mqtt.Client;

namespace Sonos2Mqtt.Commands;

/// <summary>
/// Runs sonos2mqtt's control commands - {"command": "...", "input": ...} - against a speaker.
/// </summary>
/// <remarks>
/// Transport commands (play, pause, next, seek, ...) go to the group's coordinator, as in sonos2mqtt. Commands
/// that change what the speaker itself plays (setavtransporturi, the queue, notify) go to the speaker, which
/// takes it out of its group if it was in one.
/// </remarks>
public class SonosCommandExecutor(
    SonosSpeakerRegistry registry,
    SonosNotificationPlayer notifications,
    IOptions<SonosOptions> options)
{
    /// <summary>
    /// node-sonos-ts device methods that adv-command accepted, mapped onto the command that does the same.
    /// </summary>
    private static readonly Dictionary<string, string> DeviceMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AddUriToQueue"] = "queue",
        ["JoinGroup"] = "joingroup",
        ["Next"] = "next",
        ["Pause"] = "pause",
        ["Play"] = "play",
        ["PlayNotification"] = "notify",
        ["Previous"] = "previous",
        ["SeekPosition"] = "seek",
        ["SeekTrack"] = "selecttrack",
        ["SetAVTransportURI"] = "setavtransporturi",
        ["SetVolume"] = "volume",
        ["Stop"] = "stop",
        ["SwitchToLineIn"] = "switchtoline",
        ["SwitchToQueue"] = "switchtoqueue",
        ["SwitchToTV"] = "switchtotv",
        ["TogglePlayback"] = "toggle",
    };

    /// <returns>The raw UPnP response for adv-command service calls, otherwise null.</returns>
    public async Task<IReadOnlyDictionary<string, string>?> ExecuteAsync(SonosSpeaker speaker, string command, JsonElement input, CancellationToken cancellationToken)
    {
        var device = speaker.Client;
        var coordinator = registry.GetCoordinator(speaker).Client;
        var spotifyRegion = options.Value.SpotifyRegion;

        switch (command.ToLowerInvariant())
        {
            case "adv-command":
                return await ExecuteAdvancedCommandAsync(speaker, input, cancellationToken);
            case "command":
                var nested = RequireString(input, "cmd");
                if (nested is "adv-command" or "command")
                {
                    throw new ArgumentException($"'{nested}' cannot be wrapped in 'command'");
                }
                return await ExecuteAsync(speaker, nested, GetProperty(input, "val"), cancellationToken);

            case "play":
                await coordinator.PlayAsync(cancellationToken);
                break;
            case "pause":
                await coordinator.PauseAsync(cancellationToken);
                break;
            case "stop":
                await coordinator.StopAsync(cancellationToken);
                break;
            case "next":
                await coordinator.NextAsync(cancellationToken);
                break;
            case "previous":
                await coordinator.PreviousAsync(cancellationToken);
                break;
            case "toggle":
                var state = await coordinator.GetTransportInfoAsync(cancellationToken);
                await (TransportStates.IsActive(state.State) ? coordinator.PauseAsync(cancellationToken) : coordinator.PlayAsync(cancellationToken));
                break;
            case "seek":
                await coordinator.SeekAsync("REL_TIME", RequireString(input), cancellationToken);
                break;
            case "selecttrack":
                await coordinator.SeekAsync("TRACK_NR", RequireInt(input).ToString(CultureInfo.InvariantCulture), cancellationToken);
                break;
            case "playmode":
                await coordinator.SetPlayModeAsync(RequireString(input), cancellationToken);
                break;

            case "volume":
                var volume = RequireInt(input);
                if (volume is < 0 or > 100)
                {
                    throw new ArgumentException("Volume should be between 0 and 100");
                }
                await device.SetVolumeAsync(volume, cancellationToken);
                break;
            case "volumeup":
                await device.SetRelativeVolumeAsync(OptionalInt(input) ?? 4, cancellationToken);
                break;
            case "volumedown":
                await device.SetRelativeVolumeAsync(-(OptionalInt(input) ?? 4), cancellationToken);
                break;
            case "mute":
                await device.SetMuteAsync(true, cancellationToken);
                break;
            case "unmute":
                await device.SetMuteAsync(false, cancellationToken);
                break;
            case "setbass":
                await device.SetBassAsync(RequireInt(input), cancellationToken);
                break;
            case "settreble":
                await device.SetTrebleAsync(RequireInt(input), cancellationToken);
                break;
            case "sleep":
                await device.ConfigureSleepTimerAsync(SleepDuration(input), cancellationToken);
                break;

            case "setavtransporturi":
                var (uri, metadata) = SonosMetadata.Guess(RequireString(input), spotifyRegion);
                await device.SetAVTransportUriAsync(uri, metadata, cancellationToken);
                break;
            case "queue":
                await QueueAsync(device, input, spotifyRegion, cancellationToken);
                break;
            case "clearqueue":
                await device.RemoveAllTracksFromQueueAsync(cancellationToken);
                break;
            case "switchtoqueue":
                await device.SetAVTransportUriAsync($"x-rincon-queue:{speaker.Uuid}#0", "", cancellationToken);
                break;
            case "switchtoline":
                await device.SetAVTransportUriAsync($"x-rincon-stream:{speaker.Uuid}", "", cancellationToken);
                break;
            case "switchtotv":
                await device.SetAVTransportUriAsync($"x-sonos-htastream:{speaker.Uuid}:spdif", "", cancellationToken);
                break;
            case "joingroup":
                var groupName = RequireString(input);
                if (!registry.TryGetSpeaker(groupName, out var target))
                {
                    throw new ArgumentException($"No speaker called '{groupName}' to join");
                }
                await device.SetAVTransportUriAsync($"x-rincon:{target.Member.CoordinatorUuid}", "", cancellationToken);
                break;
            case "leavegroup":
                await device.BecomeCoordinatorOfStandaloneGroupAsync(cancellationToken);
                break;

            case "notify":
                await notifications.PlayAsync(device, ParseNotification(input), cancellationToken);
                break;

            default:
                throw new NotSupportedException($"Command '{command}' not implemented");
        }

        return null;
    }

    /// <summary>
    /// adv-command is {"cmd": ..., "val": ...}. cmd is either a node-sonos-ts device method ("AddUriToQueue") or
    /// a raw UPnP action as "Service.Action" ("AVTransportService.RemoveAllTracksFromQueue"), where val is an
    /// object of the action's arguments. InstanceID defaults to 0 on the services that take it.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> ExecuteAdvancedCommandAsync(SonosSpeaker speaker, JsonElement input, CancellationToken cancellationToken)
    {
        var cmd = RequireString(input, "cmd");
        var val = GetProperty(input, "val");

        if (DeviceMethods.TryGetValue(cmd, out var mapped))
        {
            return await ExecuteAsync(speaker, mapped, val, cancellationToken);
        }

        var dot = cmd.IndexOf('.');
        if (dot <= 0 || !SonosService.TryGetByName(cmd[..dot], out var service))
        {
            throw new NotSupportedException($"adv-command '{cmd}' not implemented");
        }

        var arguments = new List<KeyValuePair<string, string>>();
        if (val.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in val.EnumerateObject())
            {
                arguments.Add(new(property.Name, ToUpnpValue(property.Value)));
            }
        }
        if (service.TakesInstanceId && !arguments.Any(x => x.Key == "InstanceID"))
        {
            arguments.Insert(0, new("InstanceID", "0"));
        }

        return await speaker.Client.InvokeAsync(service, cmd[(dot + 1)..], arguments, cancellationToken);
    }

    private static async Task QueueAsync(ISonosSpeakerClient device, JsonElement input, string spotifyRegion, CancellationToken cancellationToken)
    {
        string trackUri;
        var position = 0;
        var enqueueAsNext = true;

        if (input.ValueKind == JsonValueKind.Object)
        {
            trackUri = RequireString(input, "trackUri");
            position = OptionalInt(GetProperty(input, "positionInQueue")) ?? 0;
            enqueueAsNext = GetProperty(input, "enqueueAsNext").ValueKind != JsonValueKind.False;
        }
        else
        {
            trackUri = RequireString(input);
        }

        var (uri, metadata) = SonosMetadata.Guess(trackUri, spotifyRegion);
        await device.AddUriToQueueAsync(uri, metadata, position, enqueueAsNext, cancellationToken);
    }

    internal static SonosNotification ParseNotification(JsonElement input)
    {
        var timeout = GetProperty(input, "timeout");
        return new SonosNotification(
            RequireString(input, "trackUri"),
            Metadata: GetProperty(input, "metadata").ValueKind == JsonValueKind.String ? GetProperty(input, "metadata").GetString() : null,
            OnlyWhenPlaying: GetProperty(input, "onlyWhenPlaying").ValueKind == JsonValueKind.True,
            Timeout: timeout.ValueKind == JsonValueKind.Number ? TimeSpan.FromSeconds(timeout.GetDouble()) : null,
            Volume: OptionalInt(GetProperty(input, "volume")),
            DelayMs: OptionalInt(GetProperty(input, "delayMs")));
    }

    private static string SleepDuration(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String)
        {
            return input.GetString()!;
        }

        var minutes = RequireInt(input);
        if (minutes is < 1 or > 60)
        {
            throw new ArgumentException("Sleep should be between 1 and 60 minutes, or an hh:mm:ss string");
        }
        return $"00:{minutes:00}:00";
    }

    private static JsonElement GetProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string RequireString(JsonElement element, string? property = null)
    {
        var value = property == null ? element : GetProperty(element, property);
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString())
            ? value.GetString()!
            : throw new ArgumentException(property == null ? "Input should be a string" : $"Input needs a '{property}' string");
    }

    private static int RequireInt(JsonElement element) =>
        OptionalInt(element) ?? throw new ArgumentException("Input should be a number");

    /// <summary>
    /// A number, or a string holding one - templates in Home Assistant render numbers as strings easily.
    /// </summary>
    private static int? OptionalInt(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetInt32(out var value) => value,
        JsonValueKind.String when int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
        _ => null,
    };

    private static string ToUpnpValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        _ => value.GetRawText(),
    };
}
