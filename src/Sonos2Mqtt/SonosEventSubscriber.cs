using System.Globalization;
using Microsoft.Extensions.Options;
using Sonos2Mqtt.Client;

namespace Sonos2Mqtt;

/// <summary>
/// Keeps UPnP event subscriptions open on each speaker, so speakers say when something changed instead of
/// waiting for the next poll.
/// </summary>
/// <remarks>
/// Events are only a wake-up call: a NOTIFY makes the speaker's poll loop read its state right away, through the
/// same requests polling uses. The event bodies are not parsed, so there is one source of truth for state, and a
/// lost event only costs latency until the next (slow) poll.
/// </remarks>
public class SonosEventSubscriber(
    IHttpClientFactory httpClientFactory,
    SonosSpeakerRegistry registry,
    SonosMetrics metrics,
    IOptions<SonosOptions> options,
    ILogger<SonosEventSubscriber> logger)
{
    public const string CallbackPath = "/events/{uuid}";

    /// <summary>
    /// Transport state and track come from AVTransport, volume and mute from RenderingControl.
    /// </summary>
    private static readonly string[] EventPaths = ["/MediaRenderer/AVTransport/Event", "/MediaRenderer/RenderingControl/Event"];

    /// <summary>
    /// How long to wait for the event a speaker sends right after a new subscription before warning that events
    /// are not getting through.
    /// </summary>
    private static readonly TimeSpan FirstEventGrace = TimeSpan.FromSeconds(10);

    private static readonly HttpMethod Subscribe = new("SUBSCRIBE");
    private static readonly HttpMethod Unsubscribe = new("UNSUBSCRIBE");

    private readonly SonosOptions _options = options.Value;

    public bool Enabled => !string.IsNullOrWhiteSpace(_options.CallbackAddress);

    private record Subscription(Uri BaseAddress, string Sid);

    public async Task RunAsync(SonosSpeaker speaker, CancellationToken cancellationToken)
    {
        var callback = $"<{_options.CallbackAddress!.TrimEnd('/')}{CallbackPath.Replace("{uuid}", speaker.Uuid)}>";
        var subscriptions = new Dictionary<string, Subscription>();
        var failing = false;
        var warnedNoEvents = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan wait;
                try
                {
                    var (granted, fresh) = await SubscribeAllAsync(speaker, callback, subscriptions, cancellationToken);
                    speaker.SubscribedUntil = DateTimeOffset.UtcNow + granted;

                    if (failing)
                    {
                        logger.LogInformation("Receiving events from {name} ({uuid}) again", speaker.Name, speaker.Uuid);
                    }
                    failing = false;
                    wait = granted / 2;

                    if (fresh)
                    {
                        await Task.Delay(FirstEventGrace, cancellationToken);
                        wait -= FirstEventGrace;
                        if (!speaker.EventsArriving && !warnedNoEvents)
                        {
                            logger.LogWarning("{name} ({uuid}) accepted the subscription but no event reached {callback}; check the speakers can reach it. Polling every {interval} meanwhile",
                                speaker.Name, speaker.Uuid, callback, _options.PollInterval);
                            warnedNoEvents = true;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    speaker.SubscribedUntil = DateTimeOffset.MinValue;
                    subscriptions.Clear();

                    // An unreachable speaker is already reported by polling; only a speaker that answers polls but
                    // will not take a subscription is worth a warning.
                    if (!failing && speaker.Reachable == true)
                    {
                        logger.LogWarning("Subscribing to events from {name} ({uuid}) failed, polling every {interval} instead: {message}",
                            speaker.Name, speaker.Uuid, _options.PollInterval, ex.Message);
                    }
                    failing = true;
                    wait = _options.UnreachableRetryInterval;
                }

                await Task.Delay(wait > TimeSpan.FromSeconds(1) ? wait : TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            speaker.SubscribedUntil = DateTimeOffset.MinValue;
            await UnsubscribeAllAsync(subscriptions);
        }
    }

    /// <summary>
    /// Renews every subscription, or makes a new one where there is none, where a renewal was refused (the
    /// speaker rebooted and forgot it) or where the speaker moved to a new address.
    /// </summary>
    /// <returns>The shortest timeout the speaker granted, and whether any subscription is new.</returns>
    private async Task<(TimeSpan Granted, bool Fresh)> SubscribeAllAsync(SonosSpeaker speaker, string callback, Dictionary<string, Subscription> subscriptions, CancellationToken cancellationToken)
    {
        var baseAddress = speaker.Member.BaseAddress;
        var granted = _options.SubscriptionTimeout;
        var fresh = false;

        foreach (var path in EventPaths)
        {
            TimeSpan? timeout = null;

            if (subscriptions.TryGetValue(path, out var existing) && existing.BaseAddress == baseAddress)
            {
                timeout = await TrySendAsync("renew", baseAddress, path, request => request.Headers.TryAddWithoutValidation("SID", existing.Sid), cancellationToken);
            }

            if (timeout == null)
            {
                // Cleared before subscribing: the speaker's first event can arrive before the response does.
                speaker.EventsArriving = false;
                fresh = true;
                string? sid = null;
                timeout = await TrySendAsync("subscribe", baseAddress, path, request =>
                {
                    request.Headers.TryAddWithoutValidation("CALLBACK", callback);
                    request.Headers.TryAddWithoutValidation("NT", "upnp:event");
                }, cancellationToken, newSid => sid = newSid);

                if (timeout == null || sid == null)
                {
                    throw new InvalidOperationException($"{baseAddress} refused the subscription to {path}");
                }
                subscriptions[path] = new Subscription(baseAddress, sid);
            }

            granted = timeout.Value < granted ? timeout.Value : granted;
        }

        return (granted, fresh);
    }

    /// <returns>The granted timeout, or null if the speaker refused.</returns>
    private async Task<TimeSpan?> TrySendAsync(
        string kind,
        Uri baseAddress,
        string path,
        Action<HttpRequestMessage> addHeaders,
        CancellationToken cancellationToken,
        Action<string>? onSid = null)
    {
        using var request = new HttpRequestMessage(Subscribe, new Uri(baseAddress, path));
        request.Headers.TryAddWithoutValidation("TIMEOUT", $"Second-{(int)_options.SubscriptionTimeout.TotalSeconds}");
        addHeaders(request);

        var client = httpClientFactory.CreateClient(SonosSoapClient.HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            metrics.Subscription(kind, "refused");
            logger.LogDebug("{kind} {path} on {address} was refused with HTTP {status}", kind, path, baseAddress, (int)response.StatusCode);
            return null;
        }

        if (response.Headers.TryGetValues("SID", out var sids))
        {
            onSid?.Invoke(sids.First());
        }
        metrics.Subscription(kind, "ok");

        return response.Headers.TryGetValues("TIMEOUT", out var timeouts)
            ? ParseTimeout(timeouts.First()) ?? _options.SubscriptionTimeout
            : _options.SubscriptionTimeout;
    }

    internal static TimeSpan? ParseTimeout(string header)
    {
        const string prefix = "Second-";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(header[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    /// <summary>
    /// Best effort - a speaker that is gone forgets the subscription on its own when it times out.
    /// </summary>
    private async Task UnsubscribeAllAsync(Dictionary<string, Subscription> subscriptions)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var client = httpClientFactory.CreateClient(SonosSoapClient.HttpClientName);

        foreach (var (path, subscription) in subscriptions)
        {
            try
            {
                using var request = new HttpRequestMessage(Unsubscribe, new Uri(subscription.BaseAddress, path));
                request.Headers.TryAddWithoutValidation("SID", subscription.Sid);
                using var _ = await client.SendAsync(request, timeout.Token);
            }
            catch (Exception ex)
            {
                logger.LogDebug("Unsubscribing {path} on {address} failed: {message}", path, subscription.BaseAddress, ex.Message);
            }
        }
    }

    /// <summary>
    /// NOTIFY from a speaker. Answered at once; the read it triggers happens on the speaker's poll loop.
    /// </summary>
    public Task HandleNotifyAsync(HttpContext context)
    {
        metrics.EventReceived();

        var uuid = context.Request.RouteValues["uuid"] as string;
        if (uuid != null && registry.TryGetByUuid(uuid, out var speaker))
        {
            speaker.EventsArriving = true;
            speaker.Wake();
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        return Task.CompletedTask;
    }
}
