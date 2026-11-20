using System.Text.Json;
using ForgeMission.Core.Runtime;

namespace ForgeMission.Core.Adapters;

/// <summary>The one verdict rule for judge experts of every kind: only an explicit "pass" or "fail"
/// status counts. Anything else fails verification, so a loop retries rather than passing a verdict
/// it cannot read.</summary>
internal static class JudgeVerdict
{
    private const string NoStatus = "it has no \"status\" of \"pass\" or \"fail\".";

    /// <summary>Reads a reply whose whole body is the step envelope (LLM and HTTP judges).</summary>
    public static StepEnvelope Parse(string reply)
    {
        try
        {
            using var document = JsonDocument.Parse(reply);
            if (Status(document.RootElement) is null)
                return Invalid(reply, NoStatus);
            return document.RootElement.Deserialize(StepEnvelopeContext.Default.StepEnvelope)
                   ?? Invalid(reply, "it is empty.");
        }
        catch (JsonException exception)
        {
            return Invalid(reply, exception.Message);
        }
    }

    /// <summary>Reads the status of an already-parsed reply object whose output is carried
    /// elsewhere (exec judges).</summary>
    public static StepEnvelope Read(JsonElement root, string output, string? reason) =>
        Status(root) is { } status ? new(output, status, reason) : Invalid(output, NoStatus);

    // Matches the envelope's case-insensitive property binding.
    private static string? Status(JsonElement root) =>
        root.ValueKind != JsonValueKind.Object ? null
        : root.EnumerateObject()
            .Where(property => property.Name.Equals("status", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            .Select(property => property.Value.GetString())
            .FirstOrDefault(status => status is StepEnvelope.Pass or StepEnvelope.Fail);

    private static StepEnvelope Invalid(string reply, string error) =>
        new(reply, StepEnvelope.Fail, $"The judge reply was not a valid verdict: {error}");
}
