using System.Runtime.CompilerServices;
using System.Text.Json;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Runtime;
using Microsoft.Extensions.AI;

namespace ForgeMission.Core.Adapters;

public class DirectExpertRunner(IChatClient chatClient) : IExpertRunner
{
    public async Task<StepEnvelope> RunAsync(
        ExpertDefinition expert,
        Dictionary<string, object> context,
        CancellationToken ct = default)
    {
        var messages = BuildMessages(expert, context);
        AddToolTurn(messages, context);
        // Non-generic call with an explicit closed schema (see _stepFormat) rather than
        // GetResponseAsync<StepEnvelope>, whose derived schema is rejected by Anthropic. Deserialize
        // via the source-gen context (AOT-safe); a non-judge falls back to the raw text if the model
        // returns non-JSON, while a judge reply that is not a verdict fails (see JudgeVerdict).
        var options = new ChatOptions { ResponseFormat = _stepFormat };

        // Tool-capable agent expert: the PipelineRunner put the root tools in the bag for this step
        // only. Free-form reply instead of the forced envelope — the model must be able to answer
        // OR call a tool; the raw-text fallback below still applies.
        if (ApplyToolMode(options, context))
            options.ResponseFormat = null;

        var response = await chatClient.GetResponseAsync(messages, options, cancellationToken: ct);
        ThrowIfProviderError(response.Messages.SelectMany(message => message.Contents));

        // The model called a tool: hand the calls back through the bag — the pipeline returns them
        // to the client (which executes) instead of running any further steps.
        if (CaptureToolResponse(response, context))
        {
            var text = response.Messages.LastOrDefault()?.Contents
                .OfType<TextContent>().Select(c => c.Text).FirstOrDefault() ?? string.Empty;
            return new StepEnvelope(text);
        }

        // Non-judge experts always continue the pipeline — enforce pass regardless of LLM output.
        return expert.IsJudge
            ? JudgeVerdict.Parse(response.Text)
            : ParseStep(response.Text) with { Status = "pass", Reason = null };
    }

    // A non-judge reply is its output; non-JSON falls back to the raw text.
    private static StepEnvelope ParseStep(string reply)
    {
        try
        {
            return JsonSerializer.Deserialize(reply, StepEnvelopeContext.Default.StepEnvelope)
                   ?? new StepEnvelope(reply);
        }
        catch (JsonException)
        {
            return new StepEnvelope(reply);
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ExpertDefinition expert,
        Dictionary<string, object> context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = BuildMessages(expert, context);

        await foreach (var update in chatClient.GetStreamingResponseAsync(messages, new ChatOptions(), ct))
        {
            ThrowIfProviderError(update.Contents);
            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
        }
    }

    // Tool-capable agent step: the PipelineRunner put the root tools in the bag for this step only.
    private static bool ApplyToolMode(ChatOptions options, Dictionary<string, object> context)
    {
        if (!context.TryGetValue(ContextKeys.Tools, out var toolsValue) || toolsValue is not IList<AITool> tools)
            return false;

        options.Tools = tools;

        // Closed runtime instruction — set only by PipelineRunner; never a mission-authorable variable.
        if (context.TryGetValue(PipelineRuntimeInstructions.AllowMultipleToolCalls, out var allowValue)
            && allowValue is bool allowMultiple)
            options.AllowMultipleToolCalls = allowMultiple;
        return true;
    }

    private static void ThrowIfProviderError(IEnumerable<AIContent> contents)
    {
        if (contents.OfType<ErrorContent>().FirstOrDefault() is not { } error) return;
        throw new InvalidOperationException(string.IsNullOrEmpty(error.Message)
            ? "The model provider returned a failed response." : error.Message);
    }

    // Calls and complete messages come from the same response, preserving protected reasoning.
    private static bool CaptureToolResponse(ChatResponse response, Dictionary<string, object> context)
    {
        var calls = response.Messages.LastOrDefault()?.Contents.OfType<FunctionCallContent>().ToList();
        if (calls is not { Count: > 0 }) return false;
        context[ContextKeys.ToolCalls] = calls;
        context[PipelineToolContinuationInstructions.ResponseMessages] = response.Messages.ToArray();
        return true;
    }

    // A resumed agent step: every tool call and result so far in this turn follows the step's own
    // input, as the provider protocol expects the conversation resent.
    private static void AddToolTurn(List<ChatMessage> messages, Dictionary<string, object> context)
    {
        if (context.Remove(PipelineToolContinuationInstructions.TurnMessages, out var turn)
            && turn is IReadOnlyList<ChatMessage> turnMessages)
            messages.AddRange(turnMessages);
    }

    private static List<ChatMessage> BuildMessages(
        ExpertDefinition expert,
        Dictionary<string, object> context)
    {
        var systemPrompt = ContextInterpolator.Interpolate(expert.SystemPrompt, context);
        List<ChatMessage> messages = [new(ChatRole.System, systemPrompt)];

        // Durable chat history (earlier turns) precedes this step's own input.
        if (context.TryGetValue(ChatHistory.ContextKey, out var chatValue) && chatValue is ChatHistory chat)
            messages.AddRange(chat.Messages);

        if (context.TryGetValue(ContextKeys.History, out var historyValue)
            && historyValue is SpeakerTranscript history
            && history.Turns.Count > 0)
        {
            messages.AddRange(history.AsMessages(expert.Name));
            return messages;
        }

        var userMessage = context.TryGetValue(ContextKeys.Output, out var output) && !string.IsNullOrWhiteSpace(output?.ToString())
            ? output.ToString()!
            : "Begin.";
        messages.Add(new ChatMessage(ChatRole.User, userMessage));
        return messages;
    }

    // Explicit closed schema for structured output. GetResponseAsync<StepEnvelope> auto-derives a
    // schema from the record, but StepEnvelope.Meta is an open dictionary, which produces an object
    // without `additionalProperties: false`. Anthropic's structured output rejects that ("For 'object'
    // type, 'additionalProperties' must be explicitly set to false") while OpenAI tolerates it — so a
    // hand-written closed schema (text/status/reason only; Meta is never set by the LLM path) is the
    // only shape both providers accept. All three fields are `required` to satisfy OpenAI strict mode;
    // `reason` is nullable. The backing JsonDocument is held statically so its JsonElement stays valid.
    private const string StepEnvelopeSchemaJson = """
    {
      "type": "object",
      "properties": {
        "text":   { "type": "string" },
        "status": { "type": "string", "enum": ["pass", "fail"] },
        "reason": { "type": ["string", "null"] }
      },
      "required": ["text", "status", "reason"],
      "additionalProperties": false
    }
    """;

    private static readonly JsonDocument _schemaDoc = JsonDocument.Parse(StepEnvelopeSchemaJson);

    private static readonly ChatResponseFormat _stepFormat = ChatResponseFormat.ForJsonSchema(
        _schemaDoc.RootElement,
        schemaName: "step_envelope",
        schemaDescription: "A step result: the answer text plus a pass/fail status and optional reason.");
}
