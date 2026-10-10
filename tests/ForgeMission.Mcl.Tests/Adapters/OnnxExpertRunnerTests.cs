using ForgeMission.Core.Adapters;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Runtime;
using ForgeMission.Parser;
using ForgeMission.Tests.Runtime;

namespace ForgeMission.Tests.Adapters;

public sealed class OnnxExpertRunnerTests
{
    private static ExpertDefinition Expert(string model, string role = "") => new("Numeric", "features", "score", "",
        Kind: "onnx", Role: role, Inputs: ["low", "high"], OutputKey: "score", Threshold: "0.5",
        Model: model, ExpertDirectory: Path.Combine(AppContext.BaseDirectory, "Fixtures", "onnx"));

    [Theory]
    [InlineData("", "pass")]
    [InlineData("judge", "fail")]
    public async Task Native_numeric_inference_preserves_probability_and_threshold_semantics(string role, string status)
    {
        var context = new Dictionary<string, object> { ["low"] = "0.2", ["high"] = "0.8" };
        var result = await new OnnxExpertRunner().RunAsync(Expert("identity.onnx", role), context);
        Assert.Equal(status, result.Status);
        Assert.Equal(0.8, Assert.IsType<double>(context["score"]), 6);
    }

    [Fact]
    public async Task Native_inflight_cancellation_joins_before_return_and_does_not_write_score()
    {
        using var cancellation = new CancellationTokenSource();
        var context = new Dictionary<string, object> { ["low"] = "0.2", ["high"] = "0.8" };
        var task = new OnnxExpertRunner().RunAsync(Expert("cancellable-loop.onnx"), context, cancellation.Token);
        await Task.Delay(300);
        Assert.False(task.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(context.ContainsKey("score"));
    }

    [Fact]
    public async Task Pipeline_parallel_sibling_launches_while_native_work_is_unfinished_and_cancellation_joins()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sibling = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var facts = new System.Collections.Concurrent.ConcurrentBag<PipelineTraceEvent>();
        var runner = new PipelineRunner(new StubExpertRunner((_, context) =>
        {
            sibling.SetResult(context);
            return new StepEnvelope("sibling launched");
        }));
        var experts = new Dictionary<string, ExpertDefinition> { ["Numeric"] = Expert("cancellable-loop.onnx"),
            ["Sibling"] = new("Sibling", "text", "text", "") };
        var run = runner.RunAsync(MclParser.Parse("mission Root(low, high) = { parallel { Numeric Sibling } }"), experts,
            new PipelineRunOptions("Root", new Dictionary<string, string> { ["low"] = "0.2", ["high"] = "0.8" },
                OnTrace: (fact, _) => { facts.Add(fact); return Task.CompletedTask; }), cancellation.Token);
        try
        {
            var context = await sibling.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(300);
            Assert.False(run.IsCompleted);
            Assert.Contains(facts, fact => fact is PipelineStepStarted { ExpertName: "Numeric" });
            Assert.Contains(facts, fact => fact is PipelineStepCompleted { ExpertName: "Sibling" });
            Assert.DoesNotContain(facts, fact => fact is PipelineStepCompleted { ExpertName: "Numeric" });
            Assert.False(context.ContainsKey("score"));
        }
        finally { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10))); }
        Assert.DoesNotContain(facts, fact => fact is PipelineStepCompleted { ExpertName: "Numeric" });
    }

    [Fact]
    public async Task Missing_numeric_features_and_model_fail_explicitly()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OnnxExpertRunner().RunAsync(Expert("identity.onnx"), []));
        await Assert.ThrowsAnyAsync<Exception>(() => new OnnxExpertRunner().RunAsync(Expert("missing.onnx"),
            new() { ["low"] = 0.2, ["high"] = 0.8 }));
    }
}
