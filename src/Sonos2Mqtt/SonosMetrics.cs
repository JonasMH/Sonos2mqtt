using System.Diagnostics.Metrics;

namespace Sonos2Mqtt;

public class SonosMetrics
{
    public const string MeterName = "Sonos2Mqtt";

    private readonly Counter<long> _soapRequests;
    private readonly Counter<long> _commands;
    private readonly Counter<long> _statePublishes;
    private readonly Counter<long> _events;
    private readonly Counter<long> _subscriptions;
    private int _reachableSpeakers;
    private int _unreachableSpeakers;

    public SonosMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _soapRequests = meter.CreateCounter<long>("sonos_soap_requests_total", description: "UPnP requests sent to speakers, by service, action and result");
        _commands = meter.CreateCounter<long>("sonos_commands_total", description: "Control commands received over MQTT, by command and result");
        _statePublishes = meter.CreateCounter<long>("sonos_state_publishes_total", description: "Speaker state changes published to MQTT");
        _events = meter.CreateCounter<long>("sonos_events_received_total", description: "UPnP event notifications received from speakers");
        _subscriptions = meter.CreateCounter<long>("sonos_event_subscriptions_total", description: "UPnP event subscribe and renew requests, by kind and result");
        meter.CreateObservableGauge("sonos_speakers_reachable", () => _reachableSpeakers, description: "Speakers in the topology that answer");
        meter.CreateObservableGauge("sonos_speakers_unreachable", () => _unreachableSpeakers, description: "Speakers in the topology that do not answer");
    }

    public void SoapRequest(string service, string action, string result)
    {
        _soapRequests.Add(1,
            new KeyValuePair<string, object?>("service", service),
            new KeyValuePair<string, object?>("action", action),
            new KeyValuePair<string, object?>("result", result));
    }

    public void Command(string command, string result)
    {
        _commands.Add(1,
            new KeyValuePair<string, object?>("command", command),
            new KeyValuePair<string, object?>("result", result));
    }

    public void StatePublished() => _statePublishes.Add(1);

    public void EventReceived() => _events.Add(1);

    public void Subscription(string kind, string result)
    {
        _subscriptions.Add(1,
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("result", result));
    }

    public void SetSpeakerCounts(int reachable, int unreachable)
    {
        _reachableSpeakers = reachable;
        _unreachableSpeakers = unreachable;
    }
}
