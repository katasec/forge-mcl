using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Runtime;

namespace ForgeMission.Core.Adapters;

public class HttpExpertRunner(HttpClient? httpClient = null) : IExpertRunner
{
    private static readonly HttpClient DefaultClient = new();
    private readonly HttpClient _http = httpClient ?? DefaultClient;

    public async Task<StepEnvelope> RunAsync(
        ExpertDefinition expert,
        Dictionary<string, object> context,
        CancellationToken ct = default)
    {
        // Serialise context as string-string — all current values are strings or ToString()-able.
        var payload = context.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString() ?? "", StringComparer.Ordinal);
        var json    = JsonSerializer.Serialize(payload, HttpRunnerContext.Default.DictionaryStringString);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync(expert.Endpoint, content, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize(body, StepEnvelopeContext.Default.StepEnvelope)
               ?? new StepEnvelope(body);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ExpertDefinition expert,
        Dictionary<string, object> context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var envelope = await RunAsync(expert, context, ct);
        yield return envelope.Text;
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class HttpRunnerContext : JsonSerializerContext { }
