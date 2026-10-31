using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using Sonos2Mqtt.Client;
using ToMqttNet;

namespace Sonos2Mqtt;

/// <summary>
/// Keeps the speaker registry in step with the household topology, polls every speaker for its state and runs
/// each speaker's command queue.
/// </summary>
/// <remarks>
/// Every speaker is handled on its own: a speaker that is listed in the topology but does not answer (unplugged,
/// off the network) is logged once and retried now and then, and never holds up the others. That is the
/// failure that crashed sonos2mqtt on startup.
/// </remarks>
public class SonosHouseholdService(
    SonosSpeakerRegistry registry,
    SonosSoapClient soap,
    IMqttConnectionService mqtt,
    SonosTopicHelper topics,
    SonosEventSubscriber events,
    SonosMetrics metrics,
    IOptions<SonosOptions> options,
    ILogger<SonosHouseholdService> logger) : BackgroundService
{
    private readonly SonosOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Executing HostedService {hostedService}", GetType().Name);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshTopologyAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Refreshing the Sonos topology failed");
                }

                await Task.Delay(_options.TopologyInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var speaker in registry.Speakers)
            {
                speaker.Retire();
            }
        }
    }

    private async Task RefreshTopologyAsync(CancellationToken cancellationToken)
    {
        var members = await GetTopologyAsync(cancellationToken);
        if (members == null)
        {
            logger.LogWarning("No Sonos speaker answered with the topology; tried {seeds}", string.Join(", ", GetTopologySources()));
            return;
        }

        foreach (var member in members)
        {
            if (registry.TryGetByUuid(member.Uuid, out var existing))
            {
                if (existing.Member.BaseAddress != member.BaseAddress)
                {
                    logger.LogInformation("{name} ({uuid}) moved to {address}", member.Name, member.Uuid, member.BaseAddress);
                    existing.Client = new SonosSpeakerClient(soap, member.BaseAddress);
                }
                existing.Member = member;
                continue;
            }

            var speaker = new SonosSpeaker(member, new SonosSpeakerClient(soap, member.BaseAddress));
            if (registry.TryAdd(speaker))
            {
                logger.LogInformation("Found {name} ({uuid}) at {address}", member.Name, member.Uuid, member.BaseAddress);
                var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, speaker.Lifetime.Token).Token;
                _ = Task.Run(() => PollAsync(speaker, lifetime), CancellationToken.None);
                _ = Task.Run(() => RunCommandsAsync(speaker, lifetime), CancellationToken.None);
                if (events.Enabled)
                {
                    _ = Task.Run(() => events.RunAsync(speaker, lifetime), CancellationToken.None);
                }
            }
        }

        var current = members.Select(x => x.Uuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in registry.Speakers.Where(x => !current.Contains(x.Uuid)).ToList())
        {
            if (registry.TryRemove(gone.Uuid, out var removed))
            {
                logger.LogInformation("{name} ({uuid}) left the household", removed.Name, removed.Uuid);
                removed.Retire();
                await PublishAvailabilityAsync(removed, false);
            }
        }

        UpdateSpeakerCounts();
    }

    /// <summary>
    /// The seeds, then every speaker already known - so the topology survives a seed being unplugged.
    /// </summary>
    private IEnumerable<Uri> GetTopologySources()
    {
        return _options.SeedHosts
            .Select(ToBaseAddress)
            .Concat(registry.Speakers.Where(x => x.Reachable != false).Select(x => x.Member.BaseAddress))
            .Distinct();
    }

    private async Task<IReadOnlyList<SonosZoneMember>?> GetTopologyAsync(CancellationToken cancellationToken)
    {
        foreach (var source in GetTopologySources())
        {
            try
            {
                var state = await new SonosSpeakerClient(soap, source).GetZoneGroupStateAsync(cancellationToken);
                var members = SonosTopology.Parse(state);
                if (members.Count > 0)
                {
                    return members;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("Getting the topology from {source} failed: {message}", source, ex.Message);
            }
        }

        return null;
    }

    internal static Uri ToBaseAddress(string host)
    {
        var withScheme = host.Contains("://", StringComparison.Ordinal) ? host : $"http://{host}";
        var builder = new UriBuilder(withScheme);
        if (!host.Contains("://", StringComparison.Ordinal) && builder.Uri.IsDefaultPort)
        {
            builder.Port = 1400;
        }
        return new Uri(builder.Uri.GetLeftPart(UriPartial.Authority));
    }

    private async Task PollAsync(SonosSpeaker speaker, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            SonosStatePayload? state = null;
            try
            {
                state = await ReadStateAsync(speaker, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (speaker.Reachable != false)
                {
                    logger.LogWarning("{name} ({uuid}) at {address} is not answering, retrying every {interval}: {message}",
                        speaker.Name, speaker.Uuid, speaker.Member.BaseAddress, _options.UnreachableRetryInterval, ex.Message);
                }
            }

            // Publishing is kept apart from reading, so an MQTT hiccup is not mistaken for the speaker going away.
            try
            {
                if (state != null && speaker.Reachable == false)
                {
                    logger.LogInformation("{name} ({uuid}) is answering again", speaker.Name, speaker.Uuid);
                }
                await SetReachableAsync(speaker, state != null);

                if (state != null)
                {
                    await PublishIfChangedAsync(speaker, state);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Publishing the state of {name} ({uuid}) failed: {message}", speaker.Name, speaker.Uuid, ex.Message);
            }

            var wait = state == null ? _options.UnreachableRetryInterval
                : speaker.HasEvents ? _options.EventPollInterval
                : _options.PollInterval;

            // An event cuts the wait short - including for an unreachable speaker, which only sends one once it is
            // back.
            await speaker.WaitForWakeAsync(wait, cancellationToken);
        }
    }

    private async Task SetReachableAsync(SonosSpeaker speaker, bool reachable)
    {
        if (speaker.Reachable != reachable)
        {
            speaker.Reachable = reachable;
            UpdateSpeakerCounts();
            await PublishAvailabilityAsync(speaker, reachable);
        }
    }

    /// <summary>
    /// Retained "online" or "offline" on {NodeId}/{uuid}/availability. The state topic keeps the last state a
    /// speaker reported, so this is what tells a subscriber the speaker stopped answering.
    /// </summary>
    private Task PublishAvailabilityAsync(SonosSpeaker speaker, bool online)
    {
        return mqtt.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topics.GetAvailabilityTopic(speaker.Uuid))
            .WithPayload(online ? "online" : "offline")
            .WithRetainFlag()
            .Build());
    }

    private void UpdateSpeakerCounts()
    {
        var speakers = registry.Speakers.ToList();
        metrics.SetSpeakerCounts(speakers.Count(x => x.Reachable == true), speakers.Count(x => x.Reachable == false));
    }

    private static async Task<SonosStatePayload> ReadStateAsync(SonosSpeaker speaker, CancellationToken cancellationToken)
    {
        var client = speaker.Client;
        var transport = client.GetTransportInfoAsync(cancellationToken);
        var volume = client.GetVolumeAsync(cancellationToken);
        var mute = client.GetMuteAsync(cancellationToken);
        var position = client.GetPositionInfoAsync(cancellationToken);

        var member = speaker.Member;
        return new SonosStatePayload
        {
            Uuid = member.Uuid,
            Name = member.Name,
            GroupName = member.GroupName,
            CoordinatorUuid = member.CoordinatorUuid,
            TransportState = (await transport).State,
            Volume = new() { ["Master"] = await volume },
            Mute = new() { ["Master"] = await mute },
            CurrentTrack = SonosMetadata.ParseTrack(await position, client.BaseAddress),
        };
    }

    /// <summary>
    /// State is retained and only published when something other than the timestamp changed, so subscribers
    /// see one message per change rather than one per poll.
    /// </summary>
    private async Task PublishIfChangedAsync(SonosSpeaker speaker, SonosStatePayload state)
    {
        var comparable = JsonSerializer.Serialize(state, SonosJsonSerializerContext.Default.SonosStatePayload);
        if (comparable == speaker.LastPublishedState)
        {
            return;
        }

        state.Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await mqtt.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topics.GetStateTopic(speaker.Uuid))
            .WithPayload(JsonSerializer.Serialize(state, SonosJsonSerializerContext.Default.SonosStatePayload))
            .WithRetainFlag()
            .Build());

        speaker.LastPublishedState = comparable;
        metrics.StatePublished();
    }

    private async Task RunCommandsAsync(SonosSpeaker speaker, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var command in speaker.Commands.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await command(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Commands report their own failures; this only catches what slipped past that.
                    logger.LogError(ex, "Command for {name} ({uuid}) failed", speaker.Name, speaker.Uuid);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
