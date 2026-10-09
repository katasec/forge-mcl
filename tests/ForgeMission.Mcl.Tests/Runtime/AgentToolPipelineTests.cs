using System.Runtime.CompilerServices;
using System.Text.Json;
using ForgeMission.Core.Adapters;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Runtime;
using ForgeMission.Core.Tools;
using ForgeMission.Parser;
using Microsoft.Extensions.AI;

namespace ForgeMission.Tests.Runtime;

/// <summary>
/// Tool-capable agent expert in the pipeline (Phase 42.3 task 4). Client tools attach to the
/// `role: agent` expert's provider call ONLY; a tool call from it ends the run immediately
/// (post-agent steps wait for the final continuation); a text answer lets the pipeline
/// continue into verification as usual.
/// </summary>
public sealed class AgentToolPipelineTests
{
    [Fact]
    public async Task Parameterless_declared_inputs_and_completed_writes_survive_nested_resume_under_new_workspace()
    {
        var ast = MclParser.Parse("let mode = \"default\"\nmission Root = { Child(source_file: source_file, mode: mode, token_count: token_count) }\nmission Child = { Enrich -> Respond }");
        var experts = Experts();
        experts["Enrich"] = experts["Enrich"] with { Inputs = ["source_file", "mode", "token_count"] };
        var effects = 0;
        var runner = new StubExpertRunner((name, context) =>
        {
            if (name == "Enrich") { effects++; context["saved_file"] = context["source_file"]; }
            return Scripted(name, context);
        });
        var firstRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var secondRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var pause = Assert.IsType<PipelineToolPause>((await new PipelineRunner(runner).RunAsync(ast, experts,
            new PipelineRunOptions("Root", new Dictionary<string, string> { ["source_file"] = "inputs/source_file/content.bin",
                ["mode"] = "explicit", ["token_count"] = "7", ["apiKey"] = "excluded", ["undeclared"] = "excluded" }, RootTools: ClientTools())
            { ExecutionWorkspace = new(firstRoot, new Dictionary<string, string>()) })).Pause);
        Assert.DoesNotContain(firstRoot, pause.Continuation.Payload);
        Assert.DoesNotContain("excluded", pause.Continuation.Payload);
        var completed = await new PipelineRunner(runner).ResumeAsync(ast, experts,
            new(pause.Continuation, new(pause.ToolCall.CallId, PipelineToolResultStatus.Succeeded)),
            new PipelineRunOptions("ignored") { ExecutionWorkspace = new(secondRoot, new Dictionary<string, string>()) });
        Assert.Null(completed.Failure);
        Assert.Equal(1, effects);
        Assert.Equal("7", runner.Calls[^1].Context["token_count"]);
        Assert.Equal("explicit", runner.Calls[^1].Context["mode"]);
        Assert.Equal("inputs/source_file/content.bin", runner.Calls[^1].Context["saved_file"]);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("args")]
    [InlineData("timeout")]
    [InlineData("model")]
    [InlineData("endpoint")]
    [InlineData("inputs")]
    [InlineData("typed")]
    public async Task Resume_refuses_changed_expert_execution_semantics(string field)
    {
        var ast = MclParser.Parse("mission Root = { Enrich -> Respond }");
        var experts = Experts();
        var runner = new StubExpertRunner(Scripted);
        var pause = Assert.IsType<PipelineToolPause>((await new PipelineRunner(runner).RunAsync(ast, experts,
            new PipelineRunOptions("Root", RootTools: ClientTools()))).Pause);
        var original = experts["Enrich"];
        experts["Enrich"] = field switch
        {
            "command" => original with { Command = "changed" }, "args" => original with { Args = ["changed"] },
            "timeout" => original with { Timeout = "1s" }, "model" => original with { Model = "changed" },
            "endpoint" => original with { Endpoint = "changed" }, "inputs" => original with { Inputs = ["added"] },
            _ => original with { InputKeys = new Dictionary<string, string> { ["goal"] = "string" } },
        };
        var resumed = await Resume(new PipelineRunner(runner), ast, experts, pause);
        Assert.Equal(PipelineFailure.InvalidContinuation, resumed.Failure);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task Resume_rejects_old_inner_format_and_changed_admitted_set()
    {
        var ast = MclParser.Parse("mission Root = { Respond }");
        var runner = new StubExpertRunner(Scripted);
        var pause = Assert.IsType<PipelineToolPause>((await new PipelineRunner(runner).RunAsync(ast, Experts(),
            new PipelineRunOptions("Root", RootTools: ClientTools()))).Pause);
        var checkpoint = System.Text.Json.Nodes.JsonNode.Parse(pause.Continuation.Payload)!;
        checkpoint["formatVersion"] = 2;
        var old = pause with { Continuation = pause.Continuation with { Payload = checkpoint.ToJsonString() } };
        Assert.Equal(PipelineFailure.InvalidContinuation, (await Resume(new PipelineRunner(runner), ast, Experts(), old)).Failure);
        checkpoint["formatVersion"] = 3;
        checkpoint["admittedInputNames"] = new System.Text.Json.Nodes.JsonArray("extra");
        var altered = pause with { Continuation = pause.Continuation with { Payload = checkpoint.ToJsonString() } };
        Assert.Equal(PipelineFailure.InvalidContinuation, (await Resume(new PipelineRunner(runner), ast, Experts(), altered)).Failure);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolResponseMetadata_IsConsumedOnNormalAndExceptionalExit(bool fail)
    {
        const string responseKey = "__pipeline_tool_response_messages";
        Dictionary<string, object>? captured = null;
        var call = new FunctionCallContent("read-once", "Read", new Dictionary<string, object?>());
        var modelReply = new ChatMessage(ChatRole.Assistant,
            [new TextReasoningContent("reason") { ProtectedData = "protected" }, call]);
        var runner = new PipelineRunner(new StubExpertRunner((_, context) =>
        {
            captured = context;
            context[responseKey] = new ChatMessage[] { modelReply };
            if (fail) throw new InvalidOperationException("fixture failure");
            context["tool_calls"] = new List<FunctionCallContent> { call };
            return new StepEnvelope("");
        }));
        var ast = MclParser.Parse("mission Root = { Respond }");
        var experts = new Dictionary<string, ExpertDefinition>
        { ["Respond"] = new("Respond", "any", "text", "Respond.", Role: "agent") };
        if (fail)
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(ast, experts,
                new PipelineRunOptions("Root", RootTools: ClientTools())));
        else
        {
            var pause = Assert.IsType<PipelineToolPause>((await runner.RunAsync(ast, experts,
                new PipelineRunOptions("Root", RootTools: ClientTools()))).Pause);
            using var checkpoint = JsonDocument.Parse(pause.Continuation.Payload);
            var message = Assert.Single(checkpoint.RootElement.GetProperty("turnMessages").EnumerateArray());
            Assert.Equal(2, message.GetProperty("contents").GetArrayLength());
            Assert.Equal("protected", message.GetProperty("contents")[0].GetProperty("protectedData").GetString());
        }
        Assert.NotNull(captured);
        Assert.False(captured.ContainsKey(responseKey));
    }

    private static readonly Program Ast = MclParser.Parse("""
        mission Task(goal) = {
            Enrich
            -> Respond
            -> Verify
        }
        output(Task)
        """);

    private static Dictionary<string, ExpertDefinition> Experts() => new(StringComparer.Ordinal)
    {
        ["Enrich"]  = new("Enrich",  "any", "text", "You enrich."),
        ["Respond"] = new("Respond", "any", "text", "You respond.", Role: "agent"),
        ["Verify"]  = new("Verify",  "any", "text", "You verify."),
    };

    private static List<AITool> ClientTools() =>
        [AIFunctionFactory.Create((string file_path) => "", "Read", "Reads a file")];

    // ------------------------------------------------------------------
    // Agent calls a tool → run ends at the agent segment, Verify never runs
    // ------------------------------------------------------------------
    [Fact]
    public async Task AgentToolCall_ShortCircuits_BeforePostAgentSteps()
    {
        var client = new ScriptedPipelineClient(onToolCapableCall: ToolCallReply);
        var result = await RunAsync(client);

        Assert.Equal(MissionStatus.Pass, result.Status);
        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal("Read", call.Name);
        Assert.Equal("toolu_pipeline_1", call.CallId);

        // Enrich + Respond ran; Verify did NOT (post-agent waits for the final continuation).
        Assert.Equal(2, client.Calls.Count);
    }

    [Fact]
    public async Task ToolsAttach_OnlyToTheAgentExpertsCall()
    {
        var client = new ScriptedPipelineClient(onToolCapableCall: ToolCallReply);
        await RunAsync(client);

        Assert.Null(client.Calls[0].Options?.Tools);          // Enrich never sees client tools
        var agentTools = client.Calls[1].Options?.Tools;      // Respond does
        Assert.NotNull(agentTools);
        Assert.Equal("Read", Assert.Single(agentTools!).Name);
    }

    // ------------------------------------------------------------------
    // Agent answers in text → pipeline continues; Verify runs; no ToolCalls
    // ------------------------------------------------------------------
    [Fact]
    public async Task AgentTextAnswer_ContinuesIntoPostAgentSteps()
    {
        var client = new ScriptedPipelineClient(
            onToolCapableCall: _ => new ChatResponse([new ChatMessage(ChatRole.Assistant, "final answer")]));
        var result = await RunAsync(client);

        Assert.Equal(MissionStatus.Pass, result.Status);
        Assert.Null(result.ToolCalls);
        Assert.Equal(3, client.Calls.Count);                  // Enrich, Respond, Verify all ran
    }

    // ------------------------------------------------------------------
    // PipelineRunOptions.AllowMultipleToolCalls (Phase 43.16 Task 8b) — proves the closed
    // context-bag seam end-to-end: PipelineRunOptions -> PipelineRunner's context write ->
    // DirectExpertRunner's read -> the real ChatOptions the provider client receives.
    // ------------------------------------------------------------------
    [Fact]
    public async Task AllowMultipleToolCalls_False_IsAppliedToTheAgentExpertsChatOptions()
    {
        var client = new ScriptedPipelineClient(
            onToolCapableCall: _ => new ChatResponse([new ChatMessage(ChatRole.Assistant, "final answer")]));

        await new PipelineRunner(new DirectExpertRunner(client)).RunAsync(
            Ast, Experts(),
            new PipelineRunOptions("Task",
                new Dictionary<string, string> { ["goal"] = "read the probe file" },
                Tools: ClientTools(),
                AllowMultipleToolCalls: false));

        Assert.False(client.Calls[1].Options?.AllowMultipleToolCalls);
    }

    [Fact]
    public async Task AllowMultipleToolCalls_Default_LeavesTheAgentExpertsChatOptionsUnset()
    {
        var client = new ScriptedPipelineClient(
            onToolCapableCall: _ => new ChatResponse([new ChatMessage(ChatRole.Assistant, "final answer")]));
        await RunAsync(client); // AllowMultipleToolCalls left at its null default

        Assert.Null(client.Calls[1].Options?.AllowMultipleToolCalls);
    }

    [Fact]
    public async Task AllowMultipleToolCalls_False_IsAppliedOnTheStreamingPath()
    {
        var client = new ScriptedPipelineClient(
            onToolCapableCall: _ => new ChatResponse([new ChatMessage(ChatRole.Assistant, "final answer")]));

        // A non-null StepWriter forces DirectExpertRunner.StreamAsync instead of RunAsync
        // (PipelineRunner's streaming-trigger condition) — proves the seam on the streaming path
        // through the public API only, without reaching into the internal instruction key.
        await new PipelineRunner(new DirectExpertRunner(client)).RunAsync(
            Ast, Experts(),
            new PipelineRunOptions("Task",
                new Dictionary<string, string> { ["goal"] = "read the probe file" },
                StepWriter: TextWriter.Null,
                Tools: ClientTools(),
                AllowMultipleToolCalls: false));

        Assert.False(client.Calls[1].Options?.AllowMultipleToolCalls);
    }

    // ------------------------------------------------------------------
    // Through MissionChatClient: tool calls surface as FunctionCallContent on the reply
    // ------------------------------------------------------------------
    [Fact]
    public async Task MissionChatClient_SurfacesToolCalls_OnTheReplyMessage()
    {
        var client  = new ScriptedPipelineClient(onToolCapableCall: ToolCallReply);
        var mission = new MissionChatClient(Ast, Experts(), new DirectExpertRunner(client), fullConversation: true);

        var response = await mission.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "read the probe file")],
            new ChatOptions { Tools = ClientTools() });

        var call = Assert.Single(response.Messages.Single().Contents.OfType<FunctionCallContent>());
        Assert.Equal("Read", call.Name);
    }

    // ------------------------------------------------------------------
    // Phase 46 Task A: root-scoped generic nested continuation
    // ------------------------------------------------------------------
    [Fact]
    public async Task RootScopedToolPause_ResumesNestedAgent_WithExactProviderToolResult()
    {
        var ast = MclParser.Parse("""
            mission Child = { Enrich -> Respond -> Verify }
            mission Root = { Child }
            """);
        var experts = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal)
        {
            ["Enrich"] = new("Enrich", "any", "text", "You enrich."),
            ["Respond"] = new("Respond", "any", "text", "You respond.", Role: "agent"),
            ["Verify"] = new("Verify", "any", "text", "You verify."),
        };
        var client = new ContinuationClient();
        var runner = new PipelineRunner(new DirectExpertRunner(client));
        var facts = new List<PipelineTraceEvent>();
        var paused = await runner.RunAsync(ast, experts,
            new PipelineRunOptions("Root", new Dictionary<string, string> { ["apiKey"] = "never-persist" }, RootTools: ClientTools(),
                OnTrace: (fact, _) => { facts.Add(fact); return Task.CompletedTask; }));

        var pause = Assert.IsType<PipelineToolPause>(paused.Pause);
        Assert.Equal(["Root", "Child"], pause.MissionPath);
        Assert.Equal("Respond", pause.ExpertName);
        Assert.Equal("Read", pause.ToolCall.Name);
        Assert.Equal(1, pause.Continuation.FormatVersion);
        Assert.Equal("Root@1#0/Child@1#1", Assert.Single(facts.OfType<PipelineRootToolCheckpointed>()).StepKey);
        Assert.DoesNotContain("dispatcher", pause.Continuation.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", pause.Continuation.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("never-persist", pause.Continuation.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("You respond.", pause.Continuation.Payload, StringComparison.Ordinal);

        var reloadedContinuation = JsonSerializer.Deserialize<PipelineContinuation>(
            JsonSerializer.Serialize(pause.Continuation))!;
        var completed = await new PipelineRunner(new DirectExpertRunner(client)).ResumeAsync(ast, experts,
            new PipelineResumeRequest(reloadedContinuation,
                new PipelineToolResult(pause.ToolCall.CallId, PipelineToolResultStatus.Succeeded, "probe content")),
            new PipelineRunOptions("ignored"));

        Assert.Equal(MissionStatus.Pass, completed.Status);
        Assert.Null(completed.Pause);
        Assert.Equal(4, client.Calls.Count); // Enrich ran only before the pause; then resumed agent + Verify
        var result = Assert.Single(client.Calls[2].Messages.SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>());
        Assert.Equal(pause.ToolCall.CallId, result.CallId);
        Assert.Equal("probe content", result.Result);
    }

    // AgentToolDeclarations schemas are indented; the checkpoint stores them compacted. The scope
    // fingerprint must hash the same canonical form at pause and resume (Phase 55 Task 1c).
    [Fact]
    public async Task RootScopedToolPause_ResumesWithIndentedSchema()
    {
        var ast = MclParser.Parse("mission Root = { Respond }");
        var experts = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal)
        {
            ["Respond"] = new("Respond", "any", "text", "You respond.", Role: "agent"),
        };
        var client = new ContinuationClient();
        var runner = new PipelineRunner(new DirectExpertRunner(client));
        var paused = await runner.RunAsync(ast, experts,
            new PipelineRunOptions("Root", RootTools: [AgentToolDeclarations.Read]));
        var pause = Assert.IsType<PipelineToolPause>(paused.Pause);

        var completed = await runner.ResumeAsync(ast, experts,
            new PipelineResumeRequest(pause.Continuation,
                new PipelineToolResult(pause.ToolCall.CallId, PipelineToolResultStatus.Succeeded, "file text")),
            new PipelineRunOptions("ignored"));

        Assert.Null(completed.Failure);
        Assert.Equal(MissionStatus.Pass, completed.Status);
        Assert.Equal(2, client.Calls.Count);
    }

    [Fact]
    public async Task RootScopedToolPause_RejectsUnsupportedAndDuplicateCallsWithoutProviderResume()
    {
        var ast = MclParser.Parse("mission Root = { Respond }");
        var experts = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal)
        {
            ["Respond"] = new("Respond", "any", "text", "You respond.", Role: "agent"),
        };
        var client = new ContinuationClient();
        var runner = new PipelineRunner(new DirectExpertRunner(client));
        var paused = await runner.RunAsync(ast, experts,
            new PipelineRunOptions("Root", RootTools: ClientTools()));
        var pause = Assert.IsType<PipelineToolPause>(paused.Pause);

        var invalid = await runner.ResumeAsync(ast, experts,
            new PipelineResumeRequest(pause.Continuation with { FormatVersion = 99 },
                new PipelineToolResult(pause.ToolCall.CallId, PipelineToolResultStatus.Succeeded)),
            new PipelineRunOptions("ignored"));
        Assert.Equal(PipelineFailure.InvalidContinuation, invalid.Failure);
        Assert.Single(client.Calls);

        var completed = await runner.ResumeAsync(ast, experts,
            new PipelineResumeRequest(pause.Continuation,
                new PipelineToolResult(pause.ToolCall.CallId, PipelineToolResultStatus.Denied, "not allowed")),
            new PipelineRunOptions("ignored"));
        Assert.Equal(MissionStatus.Pass, completed.Status);

        var duplicate = await runner.ResumeAsync(ast, experts,
            new PipelineResumeRequest(pause.Continuation,
                new PipelineToolResult(pause.ToolCall.CallId, PipelineToolResultStatus.Succeeded)),
            new PipelineRunOptions("ignored"));
        Assert.Equal(PipelineFailure.DuplicateContinuation, duplicate.Failure);
        Assert.Equal(2, client.Calls.Count);
    }

    [Fact]
    public async Task RootScopedParallelAgents_PauseAndResumeInSourceOrder()
    {
        var ast = MclParser.Parse("""
            mission Left = { LeftAgent }
            mission Right = { RightAgent }
            mission Root = {
                parallel {
                    Left
                    Right
                }
            }
            """);
        var experts = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal)
        {
            ["LeftAgent"] = new("LeftAgent", "any", "text", "Left agent.", Role: "agent"),
            ["RightAgent"] = new("RightAgent", "any", "text", "Right agent.", Role: "agent"),
        };
        var client = new ContinuationClient();
        var runner = new PipelineRunner(new DirectExpertRunner(client));

        var first = await runner.RunAsync(ast, experts, new PipelineRunOptions("Root", RootTools: ClientTools()));
        var firstPause = Assert.IsType<PipelineToolPause>(first.Pause);
        Assert.Equal("LeftAgent", firstPause.ExpertName);
        Assert.Single(client.Calls); // Right must not reach its provider before Left resumes.

        var second = await runner.ResumeAsync(ast, experts,
            new PipelineResumeRequest(firstPause.Continuation,
                new PipelineToolResult(firstPause.ToolCall.CallId, PipelineToolResultStatus.Succeeded, "left")),
            new PipelineRunOptions("ignored"));
        var secondPause = Assert.IsType<PipelineToolPause>(second.Pause);
        Assert.Equal("RightAgent", secondPause.ExpertName);
        Assert.Equal(3, client.Calls.Count); // initial+continued Left, then initial Right

        var late = await runner.ResumeAsync(ast, experts,
            new PipelineResumeRequest(firstPause.Continuation,
                new PipelineToolResult(firstPause.ToolCall.CallId, PipelineToolResultStatus.Succeeded, "late")),
            new PipelineRunOptions("ignored"));
        Assert.Equal(PipelineFailure.LateContinuation, late.Failure);
        Assert.Equal(3, client.Calls.Count);

        var completed = await new PipelineRunner(new DirectExpertRunner(client)).ResumeAsync(ast, experts,
            new PipelineResumeRequest(secondPause.Continuation,
                new PipelineToolResult(secondPause.ToolCall.CallId, PipelineToolResultStatus.Succeeded, "right")),
            new PipelineRunOptions("ignored"));
        Assert.Equal(MissionStatus.Pass, completed.Status);
    }

    // Phase 61: the root-scoped pause holds exactly one tool call, so the agent call asks the
    // provider for one tool call per assistant turn.
    [Fact]
    public async Task RootScopedAgent_AsksProviderForOneToolCall()
    {
        var ast = MclParser.Parse("mission Root = { Respond }");
        var experts = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal)
        { ["Respond"] = new("Respond", "any", "text", "Respond.", Role: "agent") };
        var client = new ContinuationClient();

        await new PipelineRunner(new DirectExpertRunner(client)).RunAsync(ast, experts,
            new PipelineRunOptions("Root", RootTools: ClientTools()));

        Assert.False(Assert.Single(client.Calls).Options?.AllowMultipleToolCalls);
    }

    // A second resume resends the whole tool turn, as providers expect: the first call's result is
    // still there, not only the latest pair.
    [Fact]
    public async Task RootScopedAgent_SecondResume_ResendsTheWholeToolTurn()
    {
        var ast = MclParser.Parse("mission Root = { Respond }");
        var experts = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal)
        { ["Respond"] = new("Respond", "any", "text", "Respond.", Role: "agent") };
        var client = new TwoReadClient();
        var runner = new PipelineRunner(new DirectExpertRunner(client));

        var first = (await runner.RunAsync(ast, experts, new PipelineRunOptions("Root", RootTools: ClientTools()))).Pause!;
        var second = (await runner.ResumeAsync(ast, experts, new PipelineResumeRequest(first.Continuation,
            new PipelineToolResult(first.ToolCall.CallId, PipelineToolResultStatus.Succeeded, "repo-a")),
            new PipelineRunOptions("ignored"))).Pause!;
        var done = await runner.ResumeAsync(ast, experts, new PipelineResumeRequest(second.Continuation,
            new PipelineToolResult(second.ToolCall.CallId, PipelineToolResultStatus.Succeeded, "repo-b")),
            new PipelineRunOptions("ignored"));

        Assert.Equal(MissionStatus.Pass, done.Status);
        Assert.Equal(["a=repo-a", "b=repo-b"], client.Calls[^1].SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>().Select(r => $"{r.CallId}={r.Result}"));
    }

    [Fact]
    public async Task RootScopedToolPause_RejectsUnsupportedAndMultipleCalls_BeforeResume()
    {
        var ast = MclParser.Parse("mission Root = { Respond }");
        var experts = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal)
        { ["Respond"] = new("Respond", "any", "text", "Respond.", Role: "agent") };

        var unsupportedClient = new ContinuationClient(_ => ToolReply("Other"));
        var unsupported = await new PipelineRunner(new DirectExpertRunner(unsupportedClient)).RunAsync(ast, experts,
            new PipelineRunOptions("Root", RootTools: ClientTools()));
        Assert.Equal(PipelineFailure.UnsupportedTool, unsupported.Failure);
        Assert.Single(unsupportedClient.Calls);

        var multipleClient = new ContinuationClient(_ => new ChatResponse([new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("one", "Read", new Dictionary<string, object?>()),
             new FunctionCallContent("two", "Read", new Dictionary<string, object?>())])]));
        var multiple = await new PipelineRunner(new DirectExpertRunner(multipleClient)).RunAsync(ast, experts,
            new PipelineRunOptions("Root", RootTools: ClientTools()));
        Assert.Equal(PipelineFailure.MultipleOutstandingTools, multiple.Failure);
        Assert.Single(multipleClient.Calls);
    }

    [Fact]
    public async Task RootScopedToolPause_InvalidContinuations_DoNotInvokeProvider()
    {
        var ast = MclParser.Parse("mission Root = { Respond }");
        var experts = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal)
        { ["Respond"] = new("Respond", "any", "text", "Respond.", Role: "agent") };
        var client = new ContinuationClient();
        var pause = Assert.IsType<PipelineToolPause>((await new PipelineRunner(new DirectExpertRunner(client)).RunAsync(ast, experts,
            new PipelineRunOptions("Root", RootTools: ClientTools()))).Pause);

        foreach (var continuation in new[]
        {
            new PipelineContinuation(1, "{"),
            pause.Continuation with { FormatVersion = 42 },
            pause.Continuation with { Payload = pause.Continuation.Payload.Replace("Read", "Other", StringComparison.Ordinal) },
        })
        {
            var result = await new PipelineRunner(new DirectExpertRunner(client)).ResumeAsync(ast, experts,
                new PipelineResumeRequest(continuation, new PipelineToolResult(pause.ToolCall.CallId, PipelineToolResultStatus.Succeeded)),
                new PipelineRunOptions("ignored"));
            Assert.Equal(PipelineFailure.InvalidContinuation, result.Failure);
        }

        var wrongCall = await new PipelineRunner(new DirectExpertRunner(client)).ResumeAsync(ast, experts,
            new PipelineResumeRequest(pause.Continuation, new PipelineToolResult("wrong", PipelineToolResultStatus.Succeeded)),
            new PipelineRunOptions("ignored"));
        Assert.Equal(PipelineFailure.InvalidContinuation, wrongCall.Failure);

        var changedExperts = new Dictionary<string, ExpertDefinition>(experts, StringComparer.Ordinal)
        { ["Respond"] = experts["Respond"] with { SystemPrompt = "Changed definition." } };
        var wrongDefinition = await new PipelineRunner(new DirectExpertRunner(client)).ResumeAsync(ast, changedExperts,
            new PipelineResumeRequest(pause.Continuation, new PipelineToolResult(pause.ToolCall.CallId, PipelineToolResultStatus.Succeeded)),
            new PipelineRunOptions("ignored"));
        Assert.Equal(PipelineFailure.InvalidContinuation, wrongDefinition.Failure);
        Assert.Single(client.Calls);
    }

    [Theory]
    [InlineData(PipelineToolResultStatus.Denied)]
    [InlineData(PipelineToolResultStatus.Cancelled)]
    [InlineData(PipelineToolResultStatus.Failed)]
    public async Task RootScopedToolPause_DeliversNonSuccessResultsAsProviderErrors(PipelineToolResultStatus status)
    {
        var ast = MclParser.Parse("mission Root = { Respond }");
        var experts = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal)
        { ["Respond"] = new("Respond", "any", "text", "Respond.", Role: "agent") };
        var client = new ContinuationClient();
        var pause = Assert.IsType<PipelineToolPause>((await new PipelineRunner(new DirectExpertRunner(client)).RunAsync(ast, experts,
            new PipelineRunOptions("Root", RootTools: ClientTools()))).Pause);
        var completed = await new PipelineRunner(new DirectExpertRunner(client)).ResumeAsync(ast, experts,
            new PipelineResumeRequest(pause.Continuation, new PipelineToolResult(pause.ToolCall.CallId, status, "detail")),
            new PipelineRunOptions("ignored"));
        Assert.Equal(MissionStatus.Pass, completed.Status);
        var error = Assert.Single(client.Calls[1].Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
        Assert.Contains(status.ToString(), error.Result?.ToString());
    }

    [Fact]
    public async Task RootScopedToolPause_ProviderFailureAndRootCancellation_AreExplicit()
    {
        var ast = MclParser.Parse("mission Root = { Respond }");
        var experts = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal)
        { ["Respond"] = new("Respond", "any", "text", "Respond.", Role: "agent") };
        var client = new ContinuationClient();
        var pause = Assert.IsType<PipelineToolPause>((await new PipelineRunner(new DirectExpertRunner(client)).RunAsync(ast, experts,
            new PipelineRunOptions("Root", RootTools: ClientTools()))).Pause);
        client.ThrowOnContinuation = true;
        // Phase 62 R8: one error path — a provider failure throws; the runner turns it into Fail.
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PipelineRunner(new DirectExpertRunner(client)).ResumeAsync(ast, experts,
            new PipelineResumeRequest(pause.Continuation, new PipelineToolResult(pause.ToolCall.CallId, PipelineToolResultStatus.Succeeded)),
            new PipelineRunOptions("ignored")));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new PipelineRunner(new DirectExpertRunner(new ContinuationClient())).RunAsync(
            ast, experts, new PipelineRunOptions("Root", RootTools: ClientTools()), cancelled.Token));
    }

    [Fact]
    public async Task RootScopedIneligibleParallel_RemainsConcurrent()
    {
        var ast = MclParser.Parse("mission Root = { parallel { First Second } }");
        var experts = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal)
        {
            ["First"] = new("First", "any", "text", "First."),
            ["Second"] = new("Second", "any", "text", "Second."),
        };
        var runner = new DelayedRunner();
        var result = await new PipelineRunner(runner).RunAsync(ast, experts,
            new PipelineRunOptions("Root", RootTools: ClientTools()));
        Assert.Equal(MissionStatus.Pass, result.Status);
        Assert.Equal(2, runner.MaximumConcurrent);
    }

    [Fact]
    public async Task RootScopedToolPause_ReDerivesEnvironmentStepBindingWithoutPersistingItsValue()
    {
        const string variable = "MCLPHASE46ENVTEST";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "first-secret");
            var ast = MclParser.Parse($"mission Root = {{ Respond(input: env(\"{variable}\")) }}");
            var experts = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal)
            { ["Respond"] = new("Respond", "any", "text", "input={{input}}", Role: "agent") };
            var client = new ContinuationClient();
            var pause = Assert.IsType<PipelineToolPause>((await new PipelineRunner(new DirectExpertRunner(client)).RunAsync(ast, experts,
                new PipelineRunOptions("Root", RootTools: ClientTools()))).Pause);
            Assert.DoesNotContain("first-secret", pause.Continuation.Payload, StringComparison.Ordinal);

            Environment.SetEnvironmentVariable(variable, "re-derived");
            await new PipelineRunner(new DirectExpertRunner(client)).ResumeAsync(ast, experts,
                new PipelineResumeRequest(pause.Continuation, new PipelineToolResult(pause.ToolCall.CallId, PipelineToolResultStatus.Succeeded)),
                new PipelineRunOptions("ignored"));
            Assert.Contains("input=re-derived", client.Calls[1].Messages[0].Text);
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    // ------------------------------------------------------------------
    // Phase 62: one interpreter, pause by replay
    // ------------------------------------------------------------------

    // The same child called twice: step keys carry the call path, so the second call's agent is the
    // paused step and the first call's agent is a logged step, not a collision.
    [Fact]
    public async Task Replay_ChildCalledTwice_KeysDoNotCollide()
    {
        var ast = MclParser.Parse("""
            mission Child = { Enrich -> Respond }
            mission Root = { Child -> Child }
            """);
        var agentCalls = 0;
        var runner = new StubExpertRunner((name, ctx) => name == "Respond" && ++agentCalls == 1
            ? new StepEnvelope("first answer")
            : Scripted(name, ctx));

        var pause = Assert.IsType<PipelineToolPause>((await new PipelineRunner(runner).RunAsync(ast, Experts(),
            new PipelineRunOptions("Root", RootTools: ClientTools()))).Pause);
        Assert.Equal(["Root", "Child"], pause.MissionPath);
        Assert.Equal(4, runner.Calls.Count);

        var completed = await Resume(new PipelineRunner(runner), ast, Experts(), pause);

        Assert.Equal(MissionStatus.Pass, completed.Status);
        Assert.Equal("answered", completed.Text);
        Assert.Equal(["Enrich", "Respond", "Enrich", "Respond", "Respond"], runner.Calls.Select(call => call.ExpertName));
    }

    // A pause in a loop retry: attempt 1 replays (including its failure and feedback), and the
    // resumed agent in attempt 2 sees the feedback the rule step wrote.
    [Fact]
    public async Task Replay_PauseInLoopRetry_ReplaysTheFailedAttempt()
    {
        var ast = MclParser.Parse("mission Root loop(2) = { Draft -> Check -> Respond }");
        var experts = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal)
        {
            ["Draft"] = new("Draft", "any", "text", "Draft."),
            ["Check"] = new("Check", "any", "text", "Check."),
            ["Respond"] = new("Respond", "any", "text", "Respond.", Role: "agent"),
        };
        var checks = 0;
        var runner = new StubExpertRunner((name, ctx) =>
        {
            if (name != "Check") return Scripted(name, ctx);
            if (++checks > 1) return new StepEnvelope("good draft");
            ctx["feedback"] = "add detail";
            return new StepEnvelope("thin draft", "fail", "add detail");
        });

        var pause = Assert.IsType<PipelineToolPause>((await new PipelineRunner(runner).RunAsync(ast, experts,
            new PipelineRunOptions("Root", RootTools: ClientTools()))).Pause);
        Assert.Equal(2, pause.Attempt);

        var completed = await Resume(new PipelineRunner(runner), ast, experts, pause);

        Assert.Equal(MissionStatus.Pass, completed.Status);
        Assert.Equal(2, completed.Attempts);
        Assert.Equal(["Draft", "Check", "Draft", "Check", "Respond", "Respond"], runner.Calls.Select(call => call.ExpertName));
        Assert.Equal("add detail", runner.Calls[^1].Context["feedback"]);
    }

    [Fact]
    public async Task Replay_PauseInChildInsideParallel_ResumesAndMergesBranches()
    {
        var ast = MclParser.Parse("""
            mission Side = { Respond }
            mission Root = {
                parallel {
                    Enrich
                    Side
                }
                -> Verify
            }
            """);
        var runner = new StubExpertRunner(Scripted);

        var pause = Assert.IsType<PipelineToolPause>((await new PipelineRunner(runner).RunAsync(ast, Experts(),
            new PipelineRunOptions("Root", RootTools: ClientTools()))).Pause);
        Assert.Equal(["Root", "Side"], pause.MissionPath);
        Assert.Equal(["Enrich", "Respond"], runner.Calls.Select(call => call.ExpertName));

        var completed = await Resume(new PipelineRunner(runner), ast, Experts(), pause);

        Assert.Equal(MissionStatus.Pass, completed.Status);
        Assert.Equal(["Enrich", "Respond", "Respond", "Verify"], runner.Calls.Select(call => call.ExpertName));
        var verify = runner.Calls[^1].Context;
        Assert.Equal("Enrich done", verify["Enrich.output"]);
        Assert.Equal("answered", verify["Side.output"]);
    }

    // json_extract writes a double; the log keeps its type, so the resumed run sees a double, not
    // the string form (R5).
    [Fact]
    public async Task Replay_DoubleValue_ReplaysAsDouble()
    {
        var ast = MclParser.Parse("mission Root = { Score -> Extract -> Respond when(score > 0.5) }");
        var experts = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal)
        {
            ["Score"] = new("Score", "any", "text", "Score."),
            ["Extract"] = new("Extract", "any", "text", "", Kind: "json_extract"),
            ["Respond"] = new("Respond", "any", "text", "Respond.", Role: "agent"),
        };
        var runner = new StubExpertRunner((name, ctx) => name == "Score"
            ? new StepEnvelope("""{"score": 0.9}""")
            : Scripted(name, ctx));

        var pause = Assert.IsType<PipelineToolPause>((await new PipelineRunner(runner).RunAsync(ast, experts,
            new PipelineRunOptions("Root", RootTools: ClientTools()))).Pause);
        Assert.Matches("\"number\":\\s*0.9", pause.Continuation.Payload);

        var completed = await Resume(new PipelineRunner(runner), ast, experts, pause);

        Assert.Equal(MissionStatus.Pass, completed.Status);
        Assert.Equal(["Score", "Respond", "Respond"], runner.Calls.Select(call => call.ExpertName));
        Assert.IsType<double>(runner.Calls[^1].Context["score"]);
        Assert.Equal(0.9, runner.Calls[^1].Context["score"]);
    }

    // An env value flips a guard between pause and resume: replay reaches a step that is neither
    // logged nor the paused agent, so the continuation is rejected before that step runs (R6).
    [Fact]
    public async Task Replay_Divergence_ReturnsInvalidContinuation()
    {
        const string variable = "MCLPHASE62DIVERGE";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "agent");
            var ast = MclParser.Parse($$"""
                mission Root = {
                    Enrich(mode: env("{{variable}}"))
                    -> Respond when(mode: "agent")
                    -> Verify when(else)
                }
                """);
            var runner = new StubExpertRunner(Scripted);
            var pause = Assert.IsType<PipelineToolPause>((await new PipelineRunner(runner).RunAsync(ast, Experts(),
                new PipelineRunOptions("Root", RootTools: ClientTools()))).Pause);

            Environment.SetEnvironmentVariable(variable, "skip");
            var resumed = await Resume(new PipelineRunner(runner), ast, Experts(), pause);

            Assert.Equal(PipelineFailure.InvalidContinuation, resumed.Failure);
            Assert.Equal(["Enrich", "Respond"], runner.Calls.Select(call => call.ExpertName));
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    // Replayed steps publish nothing: no trace fact and no StepWriter line (R4).
    [Fact]
    public async Task Replay_EmitsNoTraceOrStepWriterOutputForLoggedSteps()
    {
        var ast = MclParser.Parse("mission Root = { Enrich -> Respond -> Verify }");
        var runner = new StubExpertRunner(Scripted);
        var pause = Assert.IsType<PipelineToolPause>((await new PipelineRunner(runner).RunAsync(ast, Experts(),
            new PipelineRunOptions("Root", RootTools: ClientTools()))).Pause);

        var events = new List<PipelineTraceEvent>();
        var steps = new StringWriter();
        await new PipelineRunner(runner).ResumeAsync(ast, Experts(),
            new PipelineResumeRequest(pause.Continuation,
                new PipelineToolResult(pause.ToolCall.CallId, PipelineToolResultStatus.Succeeded, "probe content")),
            new PipelineRunOptions("ignored", StepWriter: steps,
                OnTrace: (trace, _) => { events.Add(trace); return Task.CompletedTask; }));

        Assert.DoesNotContain(events, trace => trace is PipelineStepStarted { ExpertName: "Enrich" }
            or PipelineStepCompleted { ExpertName: "Enrich" });
        Assert.Equal(["Respond", "Verify"], events.OfType<PipelineStepStarted>().Select(trace => trace.ExpertName));
        Assert.DoesNotContain("Enrich", steps.ToString(), StringComparison.Ordinal);
        Assert.Contains("→ Respond...", steps.ToString(), StringComparison.Ordinal);
    }

    // An exec step before the agent runs once across two pauses in one turn; its context write
    // replays on both resumes.
    [SkippableFact]
    public async Task Replay_LoggedExecStep_NotReRunAcrossTwoPauses()
    {
        Skip.IfNot(File.Exists("/bin/sh"), "No /bin/sh for the exec step.");
        var runs = Path.GetTempFileName();
        try
        {
            var ast = MclParser.Parse("mission Root = { Count -> Respond }");
            var experts = new Dictionary<string, ExpertDefinition>(StringComparer.Ordinal)
            {
                ["Count"] = new("Count", "any", "text", "", Kind: "exec", Command: "/bin/sh",
                    Args: ["-c", $"cat > /dev/null; echo run >> '{runs}'; echo '{{\"counted\": \"yes\"}}'"], OutputKey: "counted"),
                ["Respond"] = new("Respond", "any", "text", "Respond.", Role: "agent"),
            };
            var runner = new StubExpertRunner((_, ctx) => TwoReads(ctx));

            var first = Assert.IsType<PipelineToolPause>((await new PipelineRunner(runner).RunAsync(ast, experts,
                new PipelineRunOptions("Root", RootTools: ClientTools()))).Pause);
            var second = Assert.IsType<PipelineToolPause>((await Resume(new PipelineRunner(runner), ast, experts, first)).Pause);
            var done = await Resume(new PipelineRunner(runner), ast, experts, second);

            Assert.Equal(MissionStatus.Pass, done.Status);
            Assert.Single(File.ReadAllLines(runs));
            Assert.All(runner.Calls, call => Assert.Equal("yes", call.Context["counted"]));
            Assert.Equal(3, runner.Calls.Count);
        }
        finally { File.Delete(runs); }
    }

    // Respond asks for one tool call, then answers once a tool result is in its turn.
    private static StepEnvelope Scripted(string name, Dictionary<string, object> context)
    {
        if (name != "Respond") return new StepEnvelope($"{name} done");
        if (ToolResults(context) > 0) return new StepEnvelope("answered");
        context["tool_calls"] = new List<FunctionCallContent> { new("call-1", "Read", new Dictionary<string, object?>()) };
        return new StepEnvelope(string.Empty);
    }

    // Reads twice (one call per assistant turn), then answers.
    private static StepEnvelope TwoReads(Dictionary<string, object> context)
    {
        var results = ToolResults(context);
        if (results >= 2) return new StepEnvelope("answered");
        context["tool_calls"] = new List<FunctionCallContent> { new($"call-{results}", "Read", new Dictionary<string, object?>()) };
        return new StepEnvelope(string.Empty);
    }

    private static int ToolResults(Dictionary<string, object> context) => context.Values
        .OfType<IReadOnlyList<ChatMessage>>()
        .SelectMany(turn => turn.SelectMany(message => message.Contents))
        .OfType<FunctionResultContent>()
        .Count();

    private static Task<MissionResult> Resume(PipelineRunner runner, Program ast,
        Dictionary<string, ExpertDefinition> experts, PipelineToolPause pause) =>
        runner.ResumeAsync(ast, experts,
            new PipelineResumeRequest(pause.Continuation,
                new PipelineToolResult(pause.ToolCall.CallId, PipelineToolResultStatus.Succeeded, "probe content")),
            new PipelineRunOptions("ignored"));

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // Phase 58: durable chat history on both pipeline paths; root only
    // ------------------------------------------------------------------
    private static ChatHistory EarlierTurns() => new(
    [
        new ChatMessage(ChatRole.User, "earlier question"),
        new ChatMessage(ChatRole.Assistant, "earlier answer"),
    ]);

    private static bool CarriesHistory(IList<ChatMessage> messages)
        => messages.Any(message => message.Text == "earlier answer");

    [Fact]
    public async Task RootScoped_ChatHistory_ReachesRootSteps_AndResumedAgentKeepsOrder()
    {
        var ast = MclParser.Parse("mission Root = { Enrich -> Respond -> Verify }");
        var client = new ContinuationClient();

        var paused = await new PipelineRunner(new DirectExpertRunner(client)).RunAsync(ast, Experts(),
            new PipelineRunOptions("Root", RootTools: ClientTools()) { ChatHistory = EarlierTurns() });
        var pause = Assert.IsType<PipelineToolPause>(paused.Pause);

        await new PipelineRunner(new DirectExpertRunner(client)).ResumeAsync(ast, Experts(),
            new PipelineResumeRequest(pause.Continuation,
                new PipelineToolResult(pause.ToolCall.CallId, PipelineToolResultStatus.Succeeded, "probe content")),
            new PipelineRunOptions("ignored") { ChatHistory = EarlierTurns() });

        Assert.Equal(4, client.Calls.Count);
        Assert.All(client.Calls, call => Assert.True(CarriesHistory(call.Messages)));
        Assert.Equal(
            [ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant, ChatRole.Tool],
            client.Calls[2].Messages.Select(message => message.Role));
        Assert.Equal("continued", client.Calls[2].Messages[3].Text);  // Respond's original input
    }

    [Fact]
    public async Task RootScoped_ResumedAgent_RepeatsItsOriginalInput()
    {
        var ast = MclParser.Parse("mission Root = { Enrich -> Respond -> Verify }");
        var client = new ContinuationClient();

        var paused = await new PipelineRunner(new DirectExpertRunner(client)).RunAsync(ast, Experts(),
            new PipelineRunOptions("Root", RootTools: ClientTools()));
        var pause = Assert.IsType<PipelineToolPause>(paused.Pause);
        await new PipelineRunner(new DirectExpertRunner(client)).ResumeAsync(ast, Experts(),
            new PipelineResumeRequest(pause.Continuation,
                new PipelineToolResult(pause.ToolCall.CallId, PipelineToolResultStatus.Succeeded, "probe content")),
            new PipelineRunOptions("ignored"));

        var firstUser = client.Calls[1].Messages.Single(message => message.Role == ChatRole.User).Text;
        var resumedUser = client.Calls[2].Messages.Single(message => message.Role == ChatRole.User).Text;
        Assert.Equal("continued", firstUser);
        Assert.Equal(firstUser, resumedUser);
    }

    [Fact]
    public async Task RootScoped_ChatHistory_NotInheritedByChildMission()
    {
        var ast = MclParser.Parse("""
            mission Child = { Respond -> Verify }
            mission Root = { Enrich -> Child }
            """);
        var client = new ContinuationClient(_ => new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")]));

        await new PipelineRunner(new DirectExpertRunner(client)).RunAsync(ast, Experts(),
            new PipelineRunOptions("Root", RootTools: ClientTools()) { ChatHistory = EarlierTurns() });

        Assert.Equal(3, client.Calls.Count);
        Assert.True(CarriesHistory(client.Calls[0].Messages));
        Assert.False(CarriesHistory(client.Calls[1].Messages));
        Assert.False(CarriesHistory(client.Calls[2].Messages));
    }

    [Fact]
    public async Task Recursive_ChatHistory_ReachesRootSteps_NotChildMission()
    {
        var ast = MclParser.Parse("""
            mission Child = { Verify }
            mission Root = { Enrich -> Child }
            """);
        var client = new ContinuationClient();

        await new PipelineRunner(new DirectExpertRunner(client)).RunAsync(ast, Experts(),
            new PipelineRunOptions("Root") { ChatHistory = EarlierTurns() });

        Assert.Equal(2, client.Calls.Count);
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User],
            client.Calls[0].Messages.Select(message => message.Role));
        Assert.False(CarriesHistory(client.Calls[1].Messages));
    }

    // Phase 58 addition A: in a chat run, step 1's input is the root input (the new message);
    // later steps take the previous step's output. Without ChatHistory, "Begin." is unchanged.
    private static readonly Dictionary<string, string> NewMessage = new() { ["message"] = "the new message" };

    private static string LastUserText(IList<ChatMessage> messages)
        => messages.Last(message => message.Role == ChatRole.User).Text;

    [Fact]
    public async Task Recursive_ChatRun_FirstStepGetsRootInput_SecondGetsFirstOutput()
    {
        var ast = MclParser.Parse("mission Chat(message) = { Enrich -> Verify }");
        var client = new ContinuationClient();

        await new PipelineRunner(new DirectExpertRunner(client)).RunAsync(ast, Experts(),
            new PipelineRunOptions("Chat", NewMessage) { ChatHistory = EarlierTurns() });

        Assert.Equal("the new message", LastUserText(client.Calls[0].Messages));
        Assert.Equal("continued", LastUserText(client.Calls[1].Messages));
        Assert.DoesNotContain(client.Calls.SelectMany(call => call.Messages), message => message.Text == "Begin.");
    }

    [Fact]
    public async Task RootScoped_ChatRun_FirstStepAndItsResumeGetRootInput()
    {
        var ast = MclParser.Parse("mission Chat(message) = { Respond -> Verify }");
        var client = new ContinuationClient();

        var paused = await new PipelineRunner(new DirectExpertRunner(client)).RunAsync(ast, Experts(),
            new PipelineRunOptions("Chat", NewMessage, RootTools: ClientTools()) { ChatHistory = EarlierTurns() });
        var pause = Assert.IsType<PipelineToolPause>(paused.Pause);
        await new PipelineRunner(new DirectExpertRunner(client)).ResumeAsync(ast, Experts(),
            new PipelineResumeRequest(pause.Continuation,
                new PipelineToolResult(pause.ToolCall.CallId, PipelineToolResultStatus.Succeeded, "probe content")),
            new PipelineRunOptions("ignored") { ChatHistory = EarlierTurns() });

        Assert.Equal(3, client.Calls.Count);
        Assert.Equal("the new message", LastUserText(client.Calls[0].Messages));
        Assert.Equal(
            [ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant, ChatRole.Tool],
            client.Calls[1].Messages.Select(message => message.Role));
        Assert.Equal("the new message", LastUserText(client.Calls[1].Messages));
        Assert.Equal("continued", LastUserText(client.Calls[2].Messages));
    }

    [Fact]
    public async Task WithoutChatHistory_FirstStepStillBegins()
    {
        var ast = MclParser.Parse("mission Chat(message) = { Enrich }");
        var client = new ContinuationClient();

        await new PipelineRunner(new DirectExpertRunner(client)).RunAsync(ast, Experts(),
            new PipelineRunOptions("Chat", NewMessage));

        Assert.Equal("Begin.", LastUserText(client.Calls[0].Messages));
    }

    private static ChatResponse ToolCallReply(ChatOptions? _) => new(
        [new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("toolu_pipeline_1", "Read",
                new Dictionary<string, object?> { ["file_path"] = "/tmp/probe.txt" })])]);

    private static ChatResponse ToolReply(string name) => new(
        [new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("toolu_pipeline_1", name, new Dictionary<string, object?>())])]);

    private static async Task<MissionResult> RunAsync(ScriptedPipelineClient client)
        => await new PipelineRunner(new DirectExpertRunner(client)).RunAsync(
            Ast, Experts(),
            new PipelineRunOptions("Task",
                new Dictionary<string, string> { ["goal"] = "read the probe file" },
                Tools: ClientTools()));

    // Envelope JSON for ordinary experts; delegates to the script when tools are attached.
    private sealed class ScriptedPipelineClient(Func<ChatOptions?, ChatResponse> onToolCapableCall) : IChatClient
    {
        public List<(IList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Calls.Add((messages.ToList(), options));

            var reply = options?.Tools is { Count: > 0 }
                ? onToolCapableCall(options)
                : new ChatResponse([new ChatMessage(ChatRole.Assistant,
                    """{"text": "step output", "status": "pass", "reason": null}""")]);

            return Task.FromResult(reply);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var response = await GetResponseAsync(messages, options, ct);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public void Dispose() { }
        public object? GetService(Type serviceType, object? key = null) => null;
    }

    private sealed class ContinuationClient(Func<ChatOptions?, ChatResponse>? firstToolReply = null) : IChatClient
    {
        public List<(IList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];
        public bool ThrowOnContinuation { get; set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var captured = messages.ToList();
            Calls.Add((captured, options));
            var hasResult = captured.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any();
            if (hasResult && ThrowOnContinuation) throw new InvalidOperationException("provider unavailable");
            var reply = options?.Tools is { Count: > 0 } && !hasResult
                ? (firstToolReply?.Invoke(options) ?? ToolCallReply(options))
                : new ChatResponse([new ChatMessage(ChatRole.Assistant,
                    """{"text": "continued", "status": "pass", "reason": null}""")]);
            return Task.FromResult(reply);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var response = await GetResponseAsync(messages, options, ct);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public void Dispose() { }
        public object? GetService(Type serviceType, object? key = null) => null;
    }

    // Reads "a", then "b", then answers: one tool call per assistant turn.
    private sealed class TwoReadClient : IChatClient
    {
        public List<IList<ChatMessage>> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var captured = messages.ToList();
            Calls.Add(captured);
            ChatMessage reply = captured.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Count() switch
            {
                0 => new(ChatRole.Assistant, [new FunctionCallContent("a", "Read", new Dictionary<string, object?> { ["file_path"] = "a" })]),
                1 => new(ChatRole.Assistant, [new FunctionCallContent("b", "Read", new Dictionary<string, object?> { ["file_path"] = "b" })]),
                _ => new(ChatRole.Assistant, "done"),
            };
            return Task.FromResult(new ChatResponse([reply]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public void Dispose() { }
        public object? GetService(Type serviceType, object? key = null) => null;
    }

    private sealed class DelayedRunner : IExpertRunner
    {
        private int _active;
        public int MaximumConcurrent { get; private set; }

        public async Task<StepEnvelope> RunAsync(ExpertDefinition expert, Dictionary<string, object> context, CancellationToken ct)
        {
            var active = Interlocked.Increment(ref _active);
            MaximumConcurrent = Math.Max(MaximumConcurrent, active);
            try { await Task.Delay(60, ct); return new StepEnvelope(expert.Name); }
            finally { Interlocked.Decrement(ref _active); }
        }

        public async IAsyncEnumerable<string> StreamAsync(ExpertDefinition expert, Dictionary<string, object> context,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return (await RunAsync(expert, context, ct)).Text;
        }
    }
}
