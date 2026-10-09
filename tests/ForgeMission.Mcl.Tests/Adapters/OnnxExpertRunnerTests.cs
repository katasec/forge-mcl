using ForgeMission.Core.Adapters;
using ForgeMission.Core.Experts;

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
    public async Task Missing_numeric_features_and_model_fail_explicitly()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OnnxExpertRunner().RunAsync(Expert("identity.onnx"), []));
        await Assert.ThrowsAnyAsync<Exception>(() => new OnnxExpertRunner().RunAsync(Expert("missing.onnx"),
            new() { ["low"] = 0.2, ["high"] = 0.8 }));
    }
}
