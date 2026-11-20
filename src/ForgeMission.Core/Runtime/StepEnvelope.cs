using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeMission.Core.Runtime;

public record StepEnvelope(
    [property: JsonPropertyName("text")]   string Text,
    [property: JsonPropertyName("status")] string Status = StepEnvelope.Pass,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("meta")]   IReadOnlyDictionary<string, JsonElement>? Meta = null)
{
    // The two step statuses; the wire format is these exact strings.
    internal const string Pass = "pass";
    internal const string Fail = "fail";
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(StepEnvelope))]
internal partial class StepEnvelopeContext : JsonSerializerContext { }
