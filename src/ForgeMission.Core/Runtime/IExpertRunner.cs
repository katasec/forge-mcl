using System.Runtime.CompilerServices;
using ForgeMission.Core.Experts;

namespace ForgeMission.Core.Runtime;

public interface IExpertRunner
{
    Task<StepEnvelope> RunAsync(
        ExpertDefinition expert,
        Dictionary<string, object> context,
        CancellationToken ct = default);

    // Runners without real streaming produce their whole text on completion.
    async IAsyncEnumerable<string> StreamAsync(
        ExpertDefinition expert,
        Dictionary<string, object> context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var envelope = await RunAsync(expert, context, ct);
        yield return envelope.Text;
    }
}
