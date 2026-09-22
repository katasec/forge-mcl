using ForgeMission.Core.Experts;

namespace ForgeMission.Core.Runtime;

public interface IExpertRunner
{
    Task<StepEnvelope> RunAsync(
        ExpertDefinition expert,
        Dictionary<string, object> context,
        CancellationToken ct = default);

    IAsyncEnumerable<string> StreamAsync(
        ExpertDefinition expert,
        Dictionary<string, object> context,
        CancellationToken ct = default);
}
