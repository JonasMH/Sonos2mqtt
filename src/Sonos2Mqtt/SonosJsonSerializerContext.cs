using System.Text.Json.Serialization;

namespace Sonos2Mqtt;

[JsonSerializable(typeof(SonosStatePayload))]
[JsonSerializable(typeof(SonosErrorPayload))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class SonosJsonSerializerContext : JsonSerializerContext
{
}
