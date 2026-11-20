using ForgeMission.Core.Experts;
using ForgeMission.Core.Rules;
using ForgeMission.Core.Runtime;

namespace ForgeMission.Core.Adapters;

public class RuleExpertRunner : IExpertRunner
{
    public Task<StepEnvelope> RunAsync(
        ExpertDefinition expert,
        Dictionary<string, object> context,
        CancellationToken ct = default)
    {
        var text = context.TryGetValue(ContextKeys.Output, out var o) ? o?.ToString() ?? "" : "";

        if (RuleExpression.Evaluate(expert.Check, text))
            return Task.FromResult(new StepEnvelope(text));

        // Write feedback to context so PipelineRunner can propagate it to the next loop iteration.
        var message = string.IsNullOrWhiteSpace(expert.OnFail) ? "Rule check failed." : expert.OnFail;
        context[ContextKeys.Feedback] = message;
        return Task.FromResult(new StepEnvelope(text, StepEnvelope.Fail, message));
    }
}
