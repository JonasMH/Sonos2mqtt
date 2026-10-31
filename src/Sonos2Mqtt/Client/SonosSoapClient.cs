using System.Security;
using System.Text;
using System.Xml.Linq;

namespace Sonos2Mqtt.Client;

public class SonosSoapException(SonosService service, string action, int? errorCode, string message) : Exception(message)
{
    public SonosService Service { get; } = service;
    public string Action { get; } = action;

    /// <summary>
    /// The UPnP error code from the fault, when the speaker sent one.
    /// </summary>
    public int? ErrorCode { get; } = errorCode;
}

/// <summary>
/// Sends UPnP SOAP actions to a speaker. Arguments and results are plain strings, which is all UPnP has; the
/// typed wrappers live in <see cref="SonosSpeakerClient"/>.
/// </summary>
public class SonosSoapClient(IHttpClientFactory httpClientFactory, SonosMetrics metrics)
{
    public const string HttpClientName = "sonos";

    private static readonly XNamespace SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace UpnpControlNs = "urn:schemas-upnp-org:control-1-0";

    private static readonly Dictionary<int, string> KnownErrors = new()
    {
        [401] = "Invalid action",
        [402] = "Invalid arguments",
        [701] = "Transition not available",
        [711] = "Illegal seek target",
        [712] = "Play mode not supported",
        [714] = "Illegal MIME type",
        [800] = "Not supported, or the speaker is not its group's coordinator",
    };

    public async Task<IReadOnlyDictionary<string, string>> InvokeAsync(
        Uri baseAddress,
        SonosService service,
        string action,
        IEnumerable<KeyValuePair<string, string>> arguments,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, service.ControlPath))
        {
            Content = new StringContent(BuildEnvelope(service, action, arguments), Encoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{service.Urn}#{action}\"");

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw ParseFault(service, action, (int)response.StatusCode, body);
            }

            var result = ParseResponse(body);
            metrics.SoapRequest(service.Name, action, "ok");
            return result;
        }
        catch (SonosSoapException)
        {
            metrics.SoapRequest(service.Name, action, "fault");
            throw;
        }
        catch
        {
            metrics.SoapRequest(service.Name, action, "error");
            throw;
        }
    }

    internal static string BuildEnvelope(SonosService service, string action, IEnumerable<KeyValuePair<string, string>> arguments)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        builder.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>");
        builder.Append($"<u:{action} xmlns:u=\"{service.Urn}\">");
        foreach (var (name, value) in arguments)
        {
            builder.Append($"<{name}>{SecurityElement.Escape(value)}</{name}>");
        }
        builder.Append($"</u:{action}>");
        builder.Append("</s:Body></s:Envelope>");
        return builder.ToString();
    }

    internal static IReadOnlyDictionary<string, string> ParseResponse(string body)
    {
        var document = XDocument.Parse(body);
        var response = document.Root?.Element(SoapNs + "Body")?.Elements().FirstOrDefault();

        if (response == null)
        {
            return new Dictionary<string, string>();
        }

        return response.Elements().ToDictionary(x => x.Name.LocalName, x => x.Value);
    }

    internal static SonosSoapException ParseFault(SonosService service, string action, int statusCode, string body)
    {
        int? errorCode = null;
        try
        {
            var code = XDocument.Parse(body).Descendants(UpnpControlNs + "errorCode").FirstOrDefault()?.Value;
            if (int.TryParse(code, out var parsed))
            {
                errorCode = parsed;
            }
        }
        catch (System.Xml.XmlException)
        {
            // Not a SOAP fault at all; the HTTP status is all there is to report.
        }

        var description = errorCode is int known && KnownErrors.TryGetValue(known, out var text)
            ? $"UPnP error {known} ({text})"
            : errorCode != null ? $"UPnP error {errorCode}" : $"HTTP {statusCode}";

        return new SonosSoapException(service, action, errorCode, $"{service.Name}.{action} failed: {description}");
    }
}
