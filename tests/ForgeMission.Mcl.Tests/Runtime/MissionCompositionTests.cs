using System.Globalization;
using ForgeMission.Core.Experts;
using ForgeMission.Parser;
using ForgeMission.Core.Runtime;
using static ForgeMission.Core.Runtime.MissionStatus;

namespace ForgeMission.Tests.Runtime;

public class MissionCompositionTests
{
    private static ExpertDefinition Expert(string name) =>
        new(name, "Input", "Output", $"You are {name}.");

    private static Dictionary<string, ExpertDefinition> Experts(params string[] names) =>
        names.ToDictionary(n => n, Expert, StringComparer.Ordinal);

    // ── basic composition ─────────────────────────────────────────────────────

    [Fact]
    public async Task SubMission_OutputPropagatesToParentContext()
    {
        var ast = MclParser.Parse("""
            mission Inner = {
                Worker
            }
            mission Outer = {
                Inner
                -> Reviewer
            }
            """);

        var stub = new StubExpertRunner((name, _) => $"Output from {name}");
        var result = await new PipelineRunner(stub)
            .RunAsync(ast, Experts("Worker", "Reviewer"), new PipelineRunOptions("Outer"));

        Assert.Equal(Pass, result.Status);
        // Reviewer receives Inner's output.
        var reviewerCtx = stub.Calls.First(c => c.ExpertName == "Reviewer").Context;
        Assert.Equal("Output from Worker", reviewerCtx["output"].ToString());
    }

    [Fact]
    public async Task SubMission_FinalOutput_IsSubMissionResult()
    {
        var ast = MclParser.Parse("""
            mission Inner = {
                Worker
            }
            mission Outer = {
                Inner
            }
            """);

        var stub = new StubExpertRunner((name, _) => $"Output from {name}");
        var result = await new PipelineRunner(stub)
            .RunAsync(ast, Experts("Worker"), new PipelineRunOptions("Outer"));

        Assert.Equal("Output from Worker", result.Text);
    }

    // ── parameter binding ─────────────────────────────────────────────────────

    [Fact]
    public async Task SubMission_ExplicitBinding_PassesParameterToChild()
    {
        var ast = MclParser.Parse("""
            mission Inner(topic) = {
                Drafter
            }
            mission Outer = {
                Inner(topic: "composition")
            }
            """);

        var stub = new StubExpertRunner();
        await new PipelineRunner(stub)
            .RunAsync(ast, Experts("Drafter"), new PipelineRunOptions("Outer"));

        var drafterCtx = stub.Calls.First(c => c.ExpertName == "Drafter").Context;
        Assert.Equal("composition", drafterCtx["topic"].ToString());
    }

    [Fact]
    public async Task SubMission_Binding_ForwardsParentContextValue()
    {
        var ast = MclParser.Parse("""
            let goal = "build something"
            mission Inner(task) = {
                Worker
            }
            mission Outer = {
                Inner(task: goal)
            }
            """);

        var stub = new StubExpertRunner();
        await new PipelineRunner(stub)
            .RunAsync(ast, Experts("Worker"), new PipelineRunOptions("Outer"));

        var workerCtx = stub.Calls.First(c => c.ExpertName == "Worker").Context;
        Assert.Equal("build something", workerCtx["task"].ToString());
    }

    [Fact]
    public async Task SubMission_Binding_FormatsNumericContextValueInvariantly()
    {
        var ast = MclParser.Parse("""
            mission Inner(value, limit) = {
                Worker
            }
            mission Outer = {
                Scorer
                -> Inner(value: score, limit: 3)
            }
            """);

        var stub = new StubExpertRunner((name, context) =>
        {
            if (name == "Scorer") context["score"] = 0.25;
            return $"Output from {name}";
        });
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            await new PipelineRunner(stub)
                .RunAsync(ast, Experts("Scorer", "Worker"), new PipelineRunOptions("Outer"));
        }
        finally { CultureInfo.CurrentCulture = original; }

        var workerCtx = stub.Calls.First(c => c.ExpertName == "Worker").Context;
        Assert.Equal("0.25", workerCtx["value"]);
        Assert.Equal("3", workerCtx["limit"]);
    }

    [Fact]
    public async Task SubMission_ParentContextDoesNotLeak()
    {
        // Seed runs in the parent before Inner, so the parent has a non-empty output and a
        // runtime-only key by the time Inner starts. The child must see neither.
        var ast = MclParser.Parse("""
            let secret = "parent-only"
            mission Inner = {
                Worker
            }
            mission Outer = {
                Seed
                -> Inner
            }
            """);

        var stub = new StubExpertRunner((name, context) =>
        {
            if (name == "Seed") context["parent_only"] = "x";
            return $"Output from {name}";
        });
        await new PipelineRunner(stub)
            .RunAsync(ast, Experts("Seed", "Worker"), new PipelineRunOptions("Outer"));

        var workerCtx = stub.Calls.First(c => c.ExpertName == "Worker").Context;
        Assert.Equal(string.Empty, workerCtx["output"].ToString());
        Assert.False(workerCtx.ContainsKey("parent_only"));
        // let bindings are file-level constants, so the child seeds them itself by design.
        Assert.Equal("parent-only", workerCtx["secret"].ToString());
    }

    // ── failure propagation ───────────────────────────────────────────────────

    [Fact]
    public async Task SubMission_Failure_StopsParentPipeline()
    {
        var ast = MclParser.Parse("""
            mission Inner = {
                Gatekeeper
            }
            mission Outer = {
                Inner
                -> Downstream
            }
            """);

        var stub = new StubExpertRunner((name, _) => name switch
        {
            "Gatekeeper" => new StepEnvelope("rejected", "fail", "quality gate failed"),
            _            => new StepEnvelope($"Output from {name}")
        });

        var result = await new PipelineRunner(stub)
            .RunAsync(ast, Experts("Gatekeeper", "Downstream"), new PipelineRunOptions("Outer"));

        Assert.Equal(Fail, result.Status);
        Assert.Contains("Inner", result.FailReason);
        Assert.DoesNotContain(stub.Calls, c => c.ExpertName == "Downstream");
    }

    [Fact]
    public async Task SubMission_Failure_SurfacesInnerFailReason()
    {
        var ast = MclParser.Parse("""
            mission Inner = {
                Judge
            }
            mission Outer = {
                Inner
            }
            """);

        var stub = new StubExpertRunner((name, _) =>
            new StepEnvelope("bad output", "fail", "does not meet quality bar"));

        var result = await new PipelineRunner(stub)
            .RunAsync(ast, Experts("Judge"), new PipelineRunOptions("Outer"));

        Assert.Equal(Fail, result.Status);
        Assert.Contains("does not meet quality bar", result.FailReason);
    }

    // ── two levels deep ───────────────────────────────────────────────────────

    [Fact]
    public async Task SubMission_TwoLevelsDeep_AllExpertsRun()
    {
        var ast = MclParser.Parse("""
            mission Leaf = {
                WorkerA
                -> WorkerB
            }
            mission Mid(input) = {
                Leaf
                -> Synthesiser
            }
            mission Top(goal) = {
                Mid(input: goal)
                -> FinalReviewer
            }
            """);

        var stub = new StubExpertRunner((name, _) => $"Output from {name}");
        var result = await new PipelineRunner(stub)
            .RunAsync(ast, Experts("WorkerA", "WorkerB", "Synthesiser", "FinalReviewer"),
                      new PipelineRunOptions("Top", new Dictionary<string, string> { ["goal"] = "test" }));

        Assert.Equal(Pass, result.Status);
        var names = stub.Calls.Select(c => c.ExpertName).ToList();
        Assert.Contains("WorkerA",      names);
        Assert.Contains("WorkerB",      names);
        Assert.Contains("Synthesiser",  names);
        Assert.Contains("FinalReviewer", names);
    }

    // ── when() routing to sub-mission (SDLCAgent pattern) ────────────────────

    [Fact]
    public async Task WhenRouting_DispatchesToCorrectSubMission()
    {
        var ast = MclParser.Parse("""
            mission DesignMode(input) = {
                Architect
            }
            mission TaskMode(input) = {
                Developer
            }
            mission SDLCAgent(input) = {
                Classifier
                -> DesignMode(input: input) when(mode: "design")
                -> TaskMode(input: input)   when(mode: "task")
                -> Planner                  when(else)
            }
            """);

        var stub = new StubExpertRunner((name, ctx) =>
        {
            if (name == "Classifier") ctx["mode"] = "design";
            return new StepEnvelope($"Output from {name}");
        });

        var result = await new PipelineRunner(stub)
            .RunAsync(ast, Experts("Classifier", "Architect", "Developer", "Planner"),
                      new PipelineRunOptions("SDLCAgent",
                          new Dictionary<string, string> { ["input"] = "design something" }));

        Assert.Equal(Pass, result.Status);
        var names = stub.Calls.Select(c => c.ExpertName).ToList();
        Assert.Contains("Classifier", names);
        Assert.Contains("Architect",  names);
        Assert.DoesNotContain("Developer", names);
        Assert.DoesNotContain("Planner",   names);
    }

    // ── loop(N) inside sub-mission ────────────────────────────────────────────

    [Fact]
    public async Task SubMission_WithLoop_RetriesIndependently()
    {
        var ast = MclParser.Parse("""
            mission Inner loop(3) = {
                Drafter
                -> Judge
            }
            mission Outer = {
                Inner
                -> FinalStep
            }
            """);

        var judgeCallCount = 0;
        var stub = new StubExpertRunner((name, _) =>
        {
            if (name == "Judge")
            {
                judgeCallCount++;
                return judgeCallCount < 2
                    ? new StepEnvelope("not good", "fail", "retry")
                    : new StepEnvelope("approved");
            }
            return new StepEnvelope($"Output from {name}");
        });

        var result = await new PipelineRunner(stub)
            .RunAsync(ast, Experts("Drafter", "Judge", "FinalStep"), new PipelineRunOptions("Outer"));

        Assert.Equal(Pass, result.Status);
        Assert.Equal(2, judgeCallCount); // Inner retried once
        Assert.Contains(stub.Calls, c => c.ExpertName == "FinalStep");
    }
}
