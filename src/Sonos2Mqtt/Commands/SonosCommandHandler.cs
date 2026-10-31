using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using ToMqttNet;

namespace Sonos2Mqtt.Commands;

/// <summary>
/// Takes control messages off MQTT and queues them on their speaker. Nothing runs on the MQTT receive path
/// itself - a notification takes seconds, and the client would hold every other message back until it returned.
/// </summary>
public class SonosCommandHandler(
    IMqttConnectionService mqtt,
    SonosTopicHelper topics,
    SonosSpeakerRegistry registry,
    SonosCommandExecutor executor,
    SonosMetrics metrics,
    ILogger<SonosCommandHandler> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting {hostedService}", GetType().Name);

        mqtt.OnApplicationMessageReceivedAsync += HandleMessageAsync;
        await mqtt.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(topics.GetControlTopicFilter()).Build());
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        mqtt.OnApplicationMessageReceivedAsync -= HandleMessageAsync;
        return Task.CompletedTask;
    }

    private Task HandleMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic;
        if (!topics.TryGetControlTarget(topic, out var target))
        {
            return Task.CompletedTask;
        }

        var payload = args.ApplicationMessage.ConvertPayloadToString();
        if (!TryParseControl(payload, out var control))
        {
            logger.LogWarning("Ignoring control message on {topic}, expected {{\"command\": ..., \"input\": ...}}: {payload}", topic, payload);
            metrics.Command("invalid", "rejected");
            return Task.CompletedTask;
        }

        if (!registry.TryGetSpeaker(target, out var speaker))
        {
            logger.LogWarning("Ignoring {command} for {target}, no such speaker in the household", control.Command, target);
            metrics.Command(control.Command, "unknown_speaker");
            return Task.CompletedTask;
        }

        if (!speaker.TryEnqueue(cancellationToken => ExecuteAsync(speaker, control, cancellationToken)))
        {
            logger.LogWarning("Dropping {command} for {name}, the speaker just left the household", control.Command, speaker.Name);
        }

        return Task.CompletedTask;
    }

    private async Task ExecuteAsync(SonosSpeaker speaker, SonosControl control, CancellationToken cancellationToken)
    {
        try
        {
            var result = await executor.ExecuteAsync(speaker, control.Command, control.Input, cancellationToken);
            metrics.Command(control.Command, "ok");
            logger.LogDebug("Ran {command} on {name}", control.Command, speaker.Name);

            if (result != null && control.ReplyTopic != null)
            {
                await mqtt.PublishAsync(new MqttApplicationMessageBuilder()
                    .WithTopic(topics.GetReplyTopic(speaker.Uuid, control.ReplyTopic))
                    .WithPayload(JsonSerializer.Serialize(result, SonosJsonSerializerContext.Default.IReadOnlyDictionaryStringString))
                    .Build());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            metrics.Command(control.Command, "failed");
            logger.LogWarning("{command} on {name} ({uuid}) failed: {message}", control.Command, speaker.Name, speaker.Uuid, ex.Message);

            await mqtt.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(topics.GetErrorTopic(speaker.Uuid))
                .WithPayload(JsonSerializer.Serialize(new SonosErrorPayload { Command = control.Command, Error = ex.Message }, SonosJsonSerializerContext.Default.SonosErrorPayload))
                .Build());
        }
    }

    internal record SonosControl(string Command, JsonElement Input, string? ReplyTopic);

    internal static bool TryParseControl(string payload, out SonosControl control)
    {
        control = null!;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("command", out var command)
                || command.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(command.GetString()))
            {
                return false;
            }

            var input = root.TryGetProperty("input", out var inputElement) ? inputElement.Clone() : default;
            var replyTopic = root.TryGetProperty("replyTopic", out var reply) && reply.ValueKind == JsonValueKind.String ? reply.GetString() : null;

            control = new SonosControl(command.GetString()!, input, replyTopic);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
