using ToMqttNet;

namespace Sonos2Mqtt;

/// <summary>
/// Topics are sonos2mqtt's, so existing automations keep working: state on {NodeId}/{uuid}, commands on
/// {NodeId}/{uuid}/control.
/// </summary>
public class SonosTopicHelper(IMqttConnectionService mqttConnection)
{
    private readonly IMqttConnectionService _mqttConnection = mqttConnection;

    public string GetStateTopic(string uuid) => $"{_mqttConnection.MqttOptions.NodeId}/{uuid}";
    public string GetErrorTopic(string uuid) => $"{_mqttConnection.MqttOptions.NodeId}/{uuid}/error";
    public string GetReplyTopic(string uuid, string replyTopic) => $"{_mqttConnection.MqttOptions.NodeId}/{uuid}/{replyTopic}";
    public string GetControlTopicFilter() => $"{_mqttConnection.MqttOptions.NodeId}/+/control";

    /// <summary>
    /// Pulls the speaker (uuid or name) out of a control topic.
    /// </summary>
    public bool TryGetControlTarget(string topic, out string target)
    {
        var prefix = $"{_mqttConnection.MqttOptions.NodeId}/";
        const string suffix = "/control";

        if (topic.Length > prefix.Length + suffix.Length
            && topic.StartsWith(prefix, StringComparison.Ordinal)
            && topic.EndsWith(suffix, StringComparison.Ordinal))
        {
            target = topic[prefix.Length..^suffix.Length];
            if (!target.Contains('/'))
            {
                return true;
            }
        }

        target = "";
        return false;
    }
}
