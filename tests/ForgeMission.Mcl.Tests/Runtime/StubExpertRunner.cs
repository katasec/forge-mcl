using System.Runtime.CompilerServices;
using System.Text.Json;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Runtime;

namespace ForgeMission.Tests.Runtime;

public class StubExpertRunner : IExpertRunner
{
    private readonly Func<string, Dictionary<string, object>, StepEnvelope> _respond;

    public StubExpertRunner(Func<string, Dictionary<string, object>, string>? respond = null)
    {
        var fn = respond ?? ((name, _) => $"Output from {name}");
        _respond = (name, ctx) => new StepEnvelope(fn(name, ctx));
    }

    public StubExpertRunner(Func<string, Dictionary<string, object>, StepEnvelope> respond)
    {
        _respond = respond;
    }

    // Parallel branches call the stub concurrently, so every write takes the lock.
    // Tests read Calls only after the run completes.
    public List<(string ExpertName, Dictionary<string, object> Context)> Calls { get; } = [];

    public Task<StepEnvelope> RunAsync(
        ExpertDefinition expert,
        Dictionary<string, object> context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record(expert.Name, context);
        return Task.FromResult(_respond(expert.Name, context));
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ExpertDefinition expert,
        Dictionary<string, object> context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Record(expert.Name, context);
        yield return _respond(expert.Name, context).Text;
        await Task.CompletedTask;
    }

    private void Record(string expertName, Dictionary<string, object> context)
    {
        var snapshot = new Dictionary<string, object>(context, StringComparer.Ordinal);
        lock (Calls)
            Calls.Add((expertName, snapshot));
    }
}
