using System.Runtime.CompilerServices;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Runtime;
using ForgeMission.Parser;
using Microsoft.Extensions.AI;

namespace ForgeMission.Tests.Runtime;

/// <summary>
/// Phase 53.8 — <see cref="PipelineRunOptions.StreamLlmDeltas"/>: a tool-free, non-judge llm step
/// streams, emits its chunks as <see cref="PipelineStepDelta"/> facts, and completes with its plain
/// text as a pass. Judges, steps with tools attached, and parallel steps keep the non-streaming path.
/// </summary>
public class PipelineStreamingTests
{
    // A reply whose text looks like a failing envelope: a streamed non-judge step must not parse it.
    private static readonly string[] Chunks = ["Hello, ", "{\"status\":\"fail\"}", " world"];
    private const string Joined = "Hello, {\"status\":\"fail\"} world";

    [Fact]
    public async Task NonJudgeLlmStep_StreamsDeltas_WhoseJoinedTextIsThePassingResult()
    {
        var runner = new ChunkedRunner();
        var events = new List<PipelineTraceEvent>();

        var result = await Run("mission Chat = { Answerer }", Experts(Llm("Answerer")), runner, events);

        Assert.Equal(MissionStatus.Pass, result.Status);
        Assert.Equal(Joined, result.Text);
        Assert.Equal(["Answerer"], runner.Streamed);
        Assert.Empty(runner.Ran);
        var deltas = events.OfType<PipelineStepDelta>().ToList();
        Assert.Equal(Chunks, deltas.Select(d => d.Text));
        var completed = Assert.Single(events.OfType<PipelineStepCompleted>());
        Assert.Equal(string.Concat(deltas.Select(d => d.Text)), completed.Envelope.Text);
        Assert.Equal("pass", completed.Envelope.Status);
        Assert.True(events.IndexOf(deltas[^1]) < events.IndexOf(completed));
    }

    [Fact]
    public async Task JudgeStep_DoesNotStream()
    {
        var runner = new ChunkedRunner();
        var events = new List<PipelineTraceEvent>();

        await Run("mission Check = { Verdict }", Experts(Llm("Verdict") with { Role = "judge" }), runner, events);

        Assert.Equal(["Verdict"], runner.Ran);
        Assert.Empty(runner.Streamed);
        Assert.Empty(events.OfType<PipelineStepDelta>());
    }

    [Fact]
    public async Task ToolModeStep_DoesNotStream()
    {
        var runner = new ChunkedRunner();
        var events = new List<PipelineTraceEvent>();

        await Run("mission Act = { Enrich -> Respond }", Experts(Llm("Enrich"), Llm("Respond") with { Role = "agent" }),
            runner, events, tools: ClientTools());

        Assert.Equal(["Enrich"], runner.Streamed);
        Assert.Equal(["Respond"], runner.Ran);
        Assert.All(events.OfType<PipelineStepDelta>(), d => Assert.Equal("Enrich", d.ExpertName));
    }

    [Fact]
    public async Task RootScopedPath_StreamsToolFreeSteps_AndKeepsRunAsyncForTheToolModeAgent()
    {
        var runner = new ChunkedRunner();
        var events = new List<PipelineTraceEvent>();

        var result = await Run("mission Root = { Enrich -> Respond }", Experts(Llm("Enrich"), Llm("Respond") with { Role = "agent" }),
            runner, events, rootTools: ClientTools());

        Assert.Equal(MissionStatus.Pass, result.Status);
        Assert.Equal(["Enrich"], runner.Streamed);
        Assert.Equal(["Respond"], runner.Ran);
        Assert.Equal(Chunks, events.OfType<PipelineStepDelta>().Select(d => d.Text));
    }

    [Fact]
    public async Task ChildMission_InheritsStreaming()
    {
        var runner = new ChunkedRunner();
        var events = new List<PipelineTraceEvent>();

        await Run("""
            mission Inner = { Answerer }
            mission Outer = { Inner }
            """, Experts(Llm("Answerer")), runner, events,
            missionName: "Outer");

        Assert.Equal(["Answerer"], runner.Streamed);
        Assert.All(events.OfType<PipelineStepDelta>(), d => Assert.Equal(["Outer", "Inner"], d.MissionPath));
    }

    [Fact]
    public async Task ParallelSteps_DoNotStream()
    {
        var runner = new ChunkedRunner();
        var events = new List<PipelineTraceEvent>();

        await Run("""
            mission Fan = {
                Seed
                -> parallel {
                    A
                    B
                }
            }
            """, Experts(Llm("Seed"), Llm("A"), Llm("B")), runner, events);

        Assert.Equal(["Seed"], runner.Streamed);
        Assert.Equal(["A", "B"], runner.Ran.Order());
        Assert.All(events.OfType<PipelineStepDelta>(), d => Assert.Equal("Seed", d.ExpertName));
    }

    [Fact]
    public async Task WithoutTheOption_NothingStreams()
    {
        var runner = new ChunkedRunner();
        var events = new List<PipelineTraceEvent>();

        await Run("mission Chat = { Answerer }", Experts(Llm("Answerer")), runner, events, stream: false);

        Assert.Equal(["Answerer"], runner.Ran);
        Assert.Empty(events.OfType<PipelineStepDelta>());
    }

    private static async Task<MissionResult> Run(string source, Dictionary<string, ExpertDefinition> experts,
        ChunkedRunner runner, List<PipelineTraceEvent> events, string? missionName = null, bool stream = true,
        IList<AITool>? tools = null, IList<AITool>? rootTools = null)
    {
        var ast = MclParser.Parse(source);
        var name = missionName ?? ast.Declarations.OfType<MissionDeclaration>().First().Name;
        return await new PipelineRunner(runner).RunAsync(ast, experts, new PipelineRunOptions(name,
            OnTrace: (evt, _) => { lock (events) events.Add(evt); return Task.CompletedTask; },
            Tools: tools, RootTools: rootTools) { StreamLlmDeltas = stream });
    }

    private static ExpertDefinition Llm(string name) => new(name, "Input", "Output", $"You are {name}.");

    private static Dictionary<string, ExpertDefinition> Experts(params ExpertDefinition[] experts) =>
        experts.ToDictionary(e => e.Name, StringComparer.Ordinal);

    private static List<AITool> ClientTools() =>
        [AIFunctionFactory.Create((string file_path) => "", "Read", "Reads a file")];

    /// <summary>Streams <see cref="Chunks"/>; a non-streamed call returns a passing envelope. Records
    /// which experts took which path.</summary>
    private sealed class ChunkedRunner : IExpertRunner
    {
        public List<string> Ran { get; } = [];
        public List<string> Streamed { get; } = [];

        public Task<StepEnvelope> RunAsync(ExpertDefinition expert, Dictionary<string, object> context, CancellationToken ct = default)
        {
            lock (Ran) Ran.Add(expert.Name);
            return Task.FromResult(new StepEnvelope($"{expert.Name} ran"));
        }

        public async IAsyncEnumerable<string> StreamAsync(ExpertDefinition expert, Dictionary<string, object> context,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (Streamed) Streamed.Add(expert.Name);
            foreach (var chunk in Chunks)
            {
                await Task.Yield();
                yield return chunk;
            }
        }
    }
}
