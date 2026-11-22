using System.Text;
using ForgeMission.Core.Adapters;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Manifest;
using ForgeMission.Parser;
using Microsoft.Extensions.AI;
using ForgeMission.Scout;

namespace ForgeMission.Core.Runtime;

/// <summary>
/// The one MCL interpreter. A run with root tools can pause on an agent's tool call and resume by
/// replay: the checkpoint logs every completed step under its call-path key, and
/// <see cref="ResumeAsync"/> runs the mission from the top, returning each logged step's recorded
/// result instead of running it, until the paused agent continues its tool turn.
/// </summary>
public class PipelineRunner
{
    private readonly IReadOnlyDictionary<string, IExpertRunner> _runners;
    private readonly ExecutionConfig _execution;
    private readonly HashSet<string> _locallyResumedContinuations = new(StringComparer.Ordinal);
    private readonly Lock _localResumeGate = new();
    private readonly Dictionary<string, LocalContinuationState> _localContinuations = new(StringComparer.Ordinal);
    // Optional live-retrieval backend for kind:search experts (Scout). Null ⇒ kind:search fails clearly.
    // Injected here (not on ExecutionConfig, a TOML POCO) because it is a runtime service like _runners.
    private readonly IWebSearch? _webSearch;

    public PipelineRunner(
        IReadOnlyDictionary<string, IExpertRunner> runners,
        ExecutionConfig? execution = null,
        IWebSearch? webSearch = null)
    {
        _runners   = runners;
        _execution = execution ?? new ExecutionConfig();
        _webSearch = webSearch;
    }

    // Convenience: a single runner registered as "default".
    public PipelineRunner(IExpertRunner defaultRunner, IWebSearch? webSearch = null)
        : this(new Dictionary<string, IExpertRunner>(StringComparer.Ordinal) { ["default"] = defaultRunner },
               webSearch: webSearch) { }

    public async Task<MissionResult> RunAsync(
        Program ast,
        Dictionary<string, ExpertDefinition> experts,
        PipelineRunOptions options,
        CancellationToken ct = default)
    {
        if (options.RootTools is not { Count: > 0 } rootTools)
            return await RunCoreAsync(ast, experts, options, new RunState(), string.Empty, ct);

        // A run that can pause sees only the root inputs a resume will see again.
        var admittedNames = DurableMissionInputPolicy.AdmittedNames(ast, experts, options.MissionName);
        var rootInputs = RootInputs(admittedNames, options.Vars);
        var scope = new PauseScope(rootTools, rootTools.Select(PipelineContinuationIdentity.ToDeclaration).ToList(), options.MissionName,
            PipelineDefinitionFingerprint.Compute(ast, experts, options.MissionName), rootInputs, admittedNames,
            Guid.NewGuid().ToString("N"), ordinal: 0);
        return await RunCoreAsync(ast, experts, options with { Vars = rootInputs }, new RunState(scope), string.Empty, ct);
    }

    /// <summary>Resumes a paused root-tool run after a fresh runner has been composed.</summary>
    public async Task<MissionResult> ResumeAsync(
        Program ast,
        Dictionary<string, ExpertDefinition> experts,
        PipelineResumeRequest request,
        PipelineRunOptions observers,
        CancellationToken ct = default)
    {
        if (!PipelineCheckpointCodec.TryRead(request.Continuation, out var checkpoint))
            return Failure(string.Empty, PipelineFailure.InvalidContinuation);

        var root = checkpoint.RootMissionName;
        var admittedNames = DurableMissionInputPolicy.AdmittedNames(ast, experts, root);
        if (!PipelineContinuationIdentity.IsResumable(checkpoint, admittedNames, request.Result.CallId, ast, experts))
            return Failure(root, PipelineFailure.InvalidContinuation);

        if (ConsumeLocalContinuation(checkpoint) is { } localFailure)
            return Failure(root, localFailure);

        var tools = checkpoint.ToolDeclarations.Select(tool => (AITool)new Katasec.AITools.DeclaredTool(
            tool.Name, tool.Description, tool.InputSchema)).ToList();
        var scope = new PauseScope(tools, checkpoint.ToolDeclarations, root, checkpoint.RootDefinitionFingerprint,
            checkpoint.RootInputs, admittedNames, checkpoint.RootExecutionId, checkpoint.ContinuationOrdinal);
        var run = new RunState(scope, checkpoint, PipelineContinuationIdentity.ResumedTurn(checkpoint, request.Result));
        var options = observers with { MissionName = root, Vars = checkpoint.RootInputs, RootTools = tools, MissionPath = null };

        try
        {
            var result = await RunCoreAsync(ast, experts, options, run, string.Empty, ct);
            // Still replaying: the run finished without reaching the paused agent.
            return run.Replaying ? Failure(root, PipelineFailure.InvalidContinuation) : result;
        }
        catch (ReplayDivergedException)
        {
            return Failure(root, PipelineFailure.InvalidContinuation);
        }
    }

    private async Task<MissionResult> RunCoreAsync(
        Program ast,
        Dictionary<string, ExpertDefinition> experts,
        PipelineRunOptions options,
        RunState run,
        string keyPrefix,
        CancellationToken ct)
    {
        var mission = ast.FindMission(options.MissionName)
            ?? throw new InvalidOperationException(
                $"Mission '{options.MissionName}' not found in .mcl file");

        MissionResult? lastResult = null;
        string? loopFeedback = null;
        var history = IsNegotiationEligible(mission, experts) ? new SpeakerTranscript() : null;

        // Fresh array, root mission first, ending with this mission. A
        // top-level call leaves options.MissionPath null; CreateChildOptions supplies it for a
        // nested sub-mission invocation.
        IReadOnlyList<string> missionPath = options.MissionPath ?? [options.MissionName];

        for (var attempt = 1; attempt <= mission.MaxLoops; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var context = SeedAttemptContext(ast, mission, options, attempt, loopFeedback, history);
            var outcome = await RunPipelineAsync(
                mission, keyPrefix, context, new MissionAttempt(ast, experts, options, run, missionPath, attempt), ct);
            if (outcome.Halt is { } halt) return halt;

            // Carry feedback written by rule/judge experts into the next loop iteration.
            if (context.TryGetValue(ContextKeys.Feedback, out var fb))
                loopFeedback = fb?.ToString();

            lastResult = AttemptResult(options.MissionName, context, outcome.FailReason, attempt);
            if (outcome.FailReason is null) return lastResult;
        }

        return lastResult!;
    }

    // The context one loop attempt starts from: the seeded vars plus the reserved runtime keys.
    private static Dictionary<string, object> SeedAttemptContext(
        Program ast,
        MissionDeclaration mission,
        PipelineRunOptions options,
        int attempt,
        string? loopFeedback,
        SpeakerTranscript? history)
    {
        var context = ContextBuilder.Seed(ast, options.Vars);
        if (options.ChatHistory is not null)
        {
            context[ChatHistory.ContextKey] = options.ChatHistory;
            context[ContextKeys.Output] = ChatInput(mission, options.Vars);
        }
        context[ContextKeys.Attempt]   = attempt.ToString();
        context[ContextKeys.MaxLoops] = mission.MaxLoops.ToString();
        if (loopFeedback is not null)
            context[ContextKeys.Feedback] = loopFeedback;
        if (history is not null)
            context[ContextKeys.History] = history;
        return context;
    }

    // Runs the mission's elements once, in order. Stops at the first halt or failing step; a
    // pipeline with when() guards must match one of them or have a when(else) branch.
    private async Task<StepOutcome> RunPipelineAsync(
        MissionDeclaration mission,
        string keyPrefix,
        Dictionary<string, object> context,
        MissionAttempt attempt,
        CancellationToken ct)
    {
        // Track whether any when()-guarded step matched — used for when(else) and error detection.
        var anyGuardedStepMatched = false;

        var elements = mission.Pipeline.Elements;
        for (var index = 0; index < elements.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var element = elements[index];
            var stepKey = StepKey(keyPrefix, attempt.Options.MissionName, attempt.Number, index);

            StepOutcome outcome;
            if (element is ParallelElement parallel)
                outcome = await ExecuteParallelAsync(parallel, stepKey, context, attempt, ct);
            else if (element is StepElement { Step: var step } && WhenGuards.Admits(step.When, context, anyGuardedStepMatched))
            {
                anyGuardedStepMatched |= WhenGuards.IsGuarded(step.When);
                outcome = await ExecuteStepAsync(step, stepKey, context, attempt, ct);
            }
            else
                continue;

            if (outcome.Halt is not null || outcome.FailReason is not null) return outcome;
        }

        if (WhenGuards.HasUnmatchedGuards(mission, anyGuardedStepMatched))
            throw new InvalidOperationException(
                "No when() guard matched and no when(else) branch exists in the pipeline.");

        return new StepOutcome(null, null);
    }

    private static MissionResult AttemptResult(
        string missionName, Dictionary<string, object> context, string? failReason, int attempt)
    {
        var text = context.TryGetValue(ContextKeys.Output, out var last) ? last?.ToString() ?? string.Empty : string.Empty;
        return failReason is null
            ? new MissionResult(missionName, text, MissionStatus.Pass, null, attempt)
            : new MissionResult(missionName, text, MissionStatus.Fail, failReason, attempt);
    }

    // ------------------------------------------------------------------
    // Sequence and parallel steps
    // ------------------------------------------------------------------

    private async Task<StepOutcome> ExecuteStepAsync(
        Step step,
        string key,
        Dictionary<string, object> context,
        MissionAttempt attempt,
        CancellationToken ct)
    {
        if (attempt.Ast.FindMission(step.ExpertName) is not null)
            return await ExecuteSubMissionStepAsync(step, key, context, attempt, ct);

        var expert = ResolveExpert(attempt.Experts, step);
        ApplyBindings(step, context);

        var invocation = await InvokeStepAsync(step, key, expert, context, attempt, inParallel: false, ct);
        if (invocation.RootCalls is { } calls)
            return new StepOutcome(null, await PauseAsync(key, step, expert, invocation, calls, attempt, ct));

        var envelope = invocation.Envelope;
        context[ContextKeys.Output] = envelope.Text;
        RecordHistory(step, envelope, context);

        // Emitted for both pass and fail envelopes, after the output/history update above and
        // before the caller decides whether the envelope fails the mission — always awaited
        // before the next step begins. A replayed step reports nothing.
        if (!invocation.Replayed)
            await TraceStepCompletedAsync(step, key, expert, envelope, attempt, ct);

        return new StepOutcome(StepFailReason(step, envelope), null);
    }

    private async Task<StepOutcome> ExecuteSubMissionStepAsync(
        Step step,
        string key,
        Dictionary<string, object> context,
        MissionAttempt attempt,
        CancellationToken ct)
    {
        var subResult = await RunSubMissionAsync(step, key, context, attempt, ct);
        if (Halts(subResult)) return new StepOutcome(null, subResult);

        context[ContextKeys.Output] = subResult.Text;
        return new StepOutcome(SubMissionFailReason(step, subResult), null);
    }

    // A negotiation run's transcript gets each speaker's text, plus its reason when that differs.
    private static void RecordHistory(Step step, StepEnvelope envelope, Dictionary<string, object> context)
    {
        if (!context.TryGetValue(ContextKeys.History, out var historyValue)
            || historyValue is not SpeakerTranscript history)
            return;

        var text = envelope.Text;
        if (!string.IsNullOrWhiteSpace(envelope.Reason)
            && !string.Equals(envelope.Reason, text, StringComparison.Ordinal))
            text = $"{text}\n\nReason: {envelope.Reason}";

        history.Add(step.ExpertName, text);
    }

    // A parallel block runs its branches concurrently on one context snapshot. When the run can
    // pause and a branch can reach an agent, branches run one at a time in source order instead:
    // two tool pauses can never be outstanding at once.
    private async Task<StepOutcome> ExecuteParallelAsync(
        ParallelElement parallel,
        string key,
        Dictionary<string, object> context,
        MissionAttempt attempt,
        CancellationToken ct)
    {
        var snapshot = new Dictionary<string, object>(context, StringComparer.Ordinal);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var branches = parallel.Steps.Select((step, branch) => (step, key: $"{key}.{branch}")).ToList();

        // A failing branch cancels its siblings; a halting branch does not.
        async Task<BranchOutcome> Branch((Step step, string key) branch)
        {
            var outcome = await ExecuteParallelStepAsync(branch.step, branch.key, snapshot, attempt, linkedCts.Token);
            if (outcome.FailReason is not null) linkedCts.Cancel();
            return outcome;
        }

        var inOrder = attempt.Run.Scope is not null
            && parallel.Steps.Any(step => CanReachAgent(step, attempt.Ast, attempt.Experts));
        var results = inOrder
            ? await RunBranchesInOrderAsync(branches, Branch)
            : await RunBranchesConcurrentlyAsync(branches, Branch, ct);

        if (results.FirstOrDefault(result => result.Halt is not null)?.Halt is { } halt)
            return new StepOutcome(null, halt);

        foreach (var result in results)
            context[result.NamedKey] = result.Output;

        var failReason = results.Select(result => result.FailReason).FirstOrDefault(reason => reason is not null)
            ?? (results.Count < parallel.Steps.Count ? "a parallel step was cancelled" : null);
        return new StepOutcome(failReason, null);
    }

    private static async Task<IReadOnlyList<BranchOutcome>> RunBranchesInOrderAsync(
        IReadOnlyList<(Step step, string key)> branches,
        Func<(Step step, string key), Task<BranchOutcome>> run)
    {
        var results = new List<BranchOutcome>();
        foreach (var branch in branches)
        {
            var result = await run(branch);
            results.Add(result);
            if (result.FailReason is not null || result.Halt is not null) break;
        }
        return results;
    }

    // A failing branch cancels its siblings; the block keeps the branches that completed.
    private static async Task<IReadOnlyList<BranchOutcome>> RunBranchesConcurrentlyAsync(
        IReadOnlyList<(Step step, string key)> branches,
        Func<(Step step, string key), Task<BranchOutcome>> run,
        CancellationToken ct)
    {
        var tasks = branches.Select(run).ToArray();
        try
        {
            return await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return tasks.Where(task => task.IsCompletedSuccessfully).Select(task => task.Result).ToList();
        }
    }

    private async Task<BranchOutcome> ExecuteParallelStepAsync(
        Step step,
        string key,
        Dictionary<string, object> baseContext,
        MissionAttempt attempt,
        CancellationToken ct)
    {
        var namedKey = $"{step.ExpertName}.output";

        // Sub-mission in parallel block → recurse with isolated child context.
        if (attempt.Ast.FindMission(step.ExpertName) is not null)
        {
            var subResult = await RunSubMissionAsync(step, key, baseContext, attempt, ct);
            if (Halts(subResult)) return new BranchOutcome(null, namedKey, subResult.Text, subResult);
            return new BranchOutcome(SubMissionFailReason(step, subResult), namedKey, subResult.Text, null);
        }

        var expert = ResolveExpert(attempt.Experts, step);

        // Each parallel step gets its own context copy so with-bindings don't interfere.
        var localContext = new Dictionary<string, object>(baseContext, StringComparer.Ordinal);
        ApplyBindings(step, localContext);

        var invocation = await InvokeStepAsync(step, key, expert, localContext, attempt, inParallel: true, ct);
        if (invocation.RootCalls is { } calls)
            return new BranchOutcome(null, namedKey, string.Empty,
                await PauseAsync(key, step, expert, invocation, calls, attempt, ct));

        // Parallel steps may call the sink concurrently and retain their own facts/path/attempt;
        // there is no global sequence across them.
        var envelope = invocation.Envelope;
        if (!invocation.Replayed)
            await TraceStepCompletedAsync(step, key, expert, envelope, attempt, ct);

        return new BranchOutcome(StepFailReason(step, envelope), namedKey, envelope.Text, null);
    }

    // Sub-mission: the step name matches a declared mission, so recurse under the step's key with
    // its with-bindings as the child's vars. No synthetic lifecycle fact is emitted for the
    // sub-mission invocation itself — the actual experts inside it, at the deeper path
    // CreateChildOptions builds, are the visible trail.
    private async Task<MissionResult> RunSubMissionAsync(
        Step step,
        string key,
        Dictionary<string, object> context,
        MissionAttempt attempt,
        CancellationToken ct)
    {
        var childVars = step.Context.ToDictionary(
            b => b.Key,
            b => ContextBuilder.ResolveBindingValue(b.Value, context),
            StringComparer.Ordinal);

        return await RunCoreAsync(attempt.Ast, attempt.Experts,
            CreateChildOptions(attempt.Options, step.ExpertName, childVars, attempt.MissionPath), attempt.Run, ChildPrefix(key), ct);
    }

    private static void ApplyBindings(Step step, Dictionary<string, object> context)
    {
        foreach (var binding in step.Context)
            context[binding.Key] = ContextBuilder.ResolveBindingValue(binding.Value, context);
    }

    private static string? StepFailReason(Step step, StepEnvelope envelope) => envelope.Status == StepEnvelope.Fail
        ? $"[{step.ExpertName}] {envelope.Reason ?? "step failed"}"
        : null;

    private static string? SubMissionFailReason(Step step, MissionResult subResult) => subResult.Status == MissionStatus.Fail
        ? $"[{step.ExpertName}] {subResult.FailReason ?? "sub-mission failed"}"
        : null;

    private static async Task TraceStepCompletedAsync(
        Step step,
        string key,
        ExpertDefinition expert,
        StepEnvelope envelope,
        MissionAttempt attempt,
        CancellationToken ct)
    {
        if (attempt.Options.OnTrace is { } onCompleted)
            await onCompleted(new PipelineStepCompleted(attempt.Options.MissionName, attempt.MissionPath, step.ExpertName, expert.Kind, attempt.Number, envelope) { StepKey = key }, ct);
    }

    // ------------------------------------------------------------------
    // Step invocation
    // ------------------------------------------------------------------

    // The one invoke point for a step's expert, shared by sequence and parallel steps. On replay a
    // logged step returns its recorded envelope and writes, and runs or reports nothing. A live
    // step runs and is logged, unless it is an agent that paused on a root tool call.
    private async Task<StepInvocation> InvokeStepAsync(
        Step step,
        string key,
        ExpertDefinition expert,
        Dictionary<string, object> context,
        MissionAttempt attempt,
        bool inParallel,
        CancellationToken ct)
    {
        var run = attempt.Run;
        if (run.AdvanceReplay(key, step.ExpertName, expert) is { } logged)
            return ReplayLogged(logged, context);

        var runner = RunnerFor(expert, step, attempt.Options, key, attempt.Number);

        // Before invoking a real expert: its attempt is the enclosing
        // mission's current loop attempt.
        if (attempt.Options.OnTrace is { } onStarted)
            await onStarted(new PipelineStepStarted(attempt.Options.MissionName, attempt.MissionPath, step.ExpertName, expert.Kind, attempt.Number) { StepKey = key }, ct);

        // Taken after bindings and before tools: the log holds only what the expert itself wrote.
        var before = new Dictionary<string, object>(context, StringComparer.Ordinal);
        var turn = run.ResumeTurnFor(key);
        AttachTools(expert, context, run, turn);

        var (envelope, responseMessages) = await RunExpertAsync(
            runner, step, key, expert, context, attempt, inParallel, ct);

        if (TakeRootCalls(run, expert, context) is { } calls)
            return new StepInvocation(envelope, Replayed: false, calls, NormalizeToolTurn(turn, responseMessages, calls));

        run.Record(new PipelineStepLogEntry(key, envelope.Text, envelope.Status, envelope.Reason, StepWrites(before, context)));
        return new StepInvocation(envelope, Replayed: false, null, []);
    }

    private static StepInvocation ReplayLogged(PipelineStepLogEntry logged, Dictionary<string, object> context)
    {
        foreach (var (name, value) in logged.Writes)
            context[name] = value.ToContextValue();
        return new StepInvocation(new StepEnvelope(logged.Text, logged.Status, logged.Reason), Replayed: true, null, []);
    }

    // Runs the expert, then always strips the per-call tool hand-off from the context. A sequence
    // step's failure is wrapped with its name; a parallel step's propagates as thrown.
    private static async Task<(StepEnvelope Envelope, object? ResponseMessages)> RunExpertAsync(
        IExpertRunner runner,
        Step step,
        string key,
        ExpertDefinition expert,
        Dictionary<string, object> context,
        MissionAttempt attempt,
        bool inParallel,
        CancellationToken ct)
    {
        StepEnvelope envelope;
        object? responseMessages;
        try
        {
            envelope = inParallel
                ? await runner.RunAsync(expert, context, ct)
                : await InvokeExpertAsync(runner, expert, context, step.ExpertName, key, attempt, ct);
        }
        catch (Exception ex) when (!inParallel && ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Step '{step.ExpertName}' failed: {ex.Message}", ex);
        }
        finally
        {
            responseMessages = DetachTools(context);
        }

        return (envelope, responseMessages);
    }

    // Removes what AttachTools and the runner put on the context for this call only; returns the
    // provider's complete reply messages, when the runner left them.
    private static object? DetachTools(Dictionary<string, object> context)
    {
        context.Remove(ContextKeys.Tools);
        context.Remove(ContextKeys.AllowMultipleToolCalls);
        context.Remove(ContextKeys.ToolTurnMessages);
        context.Remove(ContextKeys.ToolResponseMessages, out var responseMessages);
        return responseMessages;
    }

    // An agent in a pausable run that asked for root tool calls pauses instead of being logged.
    private static IReadOnlyList<FunctionCallContent>? TakeRootCalls(
        RunState run, ExpertDefinition expert, Dictionary<string, object> context)
        => run.Scope is not null && expert.IsAgent
           && context.Remove(ContextKeys.ToolCalls, out var raw) && raw is IReadOnlyList<FunctionCallContent> calls
            ? calls
            : null;


    // Existing call-only runners and complete provider replies converge before checkpointing.
    private static IReadOnlyList<ChatMessage> NormalizeToolTurn(IReadOnlyList<ChatMessage>? turn,
        object? responseMessages, IReadOnlyList<FunctionCallContent> calls) =>
        [.. turn ?? [], .. responseMessages is IReadOnlyList<ChatMessage> messages
            ? messages : [new ChatMessage(ChatRole.Assistant, [.. calls])]];

    // Root tools (and a resumed tool turn) attach to agent experts' calls only, at every depth —
    // enrichment and verification experts never see them.
    private static void AttachTools(
        ExpertDefinition expert,
        Dictionary<string, object> context,
        RunState run,
        IReadOnlyList<ChatMessage>? turn)
    {
        if (!expert.IsAgent || run.Scope is not { } scope) return;

        context[ContextKeys.Tools] = scope.Tools;
        // One call per pause is Core's rule (PipelineToolPause holds exactly one ToolCall).
        context[ContextKeys.AllowMultipleToolCalls] = false;
        if (turn is not null)
            context[ContextKeys.ToolTurnMessages] = turn;
    }

    // A root tool call ends the whole run. The checkpoint holds the log of completed steps, this
    // step's key, and its tool turn so far, so a resume can replay up to here and continue the turn.
    private async Task<MissionResult> PauseAsync(
        string key,
        Step step,
        ExpertDefinition expert,
        StepInvocation invocation,
        IReadOnlyList<FunctionCallContent> calls,
        MissionAttempt attempt,
        CancellationToken ct)
    {
        var options = attempt.Options;
        var run = attempt.Run;
        var missionPath = attempt.MissionPath;
        var number = attempt.Number;
        var scope = run.Scope!;
        await TraceStepCompletedAsync(step, key, expert, invocation.Envelope, attempt, ct);

        if (calls.Count != 1) return Failure(scope.RootMissionName, PipelineFailure.MultipleOutstandingTools);
        var call = PipelineToolCallConversion.From(calls).Single();
        if (!scope.Declarations.Any(declaration => string.Equals(declaration.Name, call.Name, StringComparison.Ordinal)))
            return Failure(scope.RootMissionName, PipelineFailure.UnsupportedTool);

        if (options.OnTrace is { } onCheckpointed)
            await onCheckpointed(new PipelineRootToolCheckpointed(options.MissionName, missionPath, step.ExpertName, expert.Kind, number, call) { StepKey = key }, ct);

        var ordinal = scope.NextOrdinal();
        RegisterIssuedContinuation(scope.RootExecutionId, ordinal);
        var checkpoint = new PipelineContinuationCheckpoint(PipelineCheckpointCodec.CheckpointVersion,
            Guid.NewGuid().ToString("N"), scope.RootExecutionId, ordinal, scope.RootMissionName,
            scope.DefinitionFingerprint, scope.ToolScopeFingerprint, scope.Declarations, missionPath, step.ExpertName,
            number, invocation.Turn, scope.RootInputs, scope.AdmittedInputNames,
            run.LogSnapshot(), key);
        var pause = new PipelineToolPause(scope.RootMissionName, missionPath, step.ExpertName, number, call,
            new PipelineContinuation(PipelineCheckpointCodec.EnvelopeVersion, PipelineCheckpointCodec.Write(checkpoint)));
        return new MissionResult(scope.RootMissionName, string.Empty, MissionStatus.Pass, Attempts: number, Pause: pause);
    }

    // Builds a child mission's options for RunSubMissionAsync.
    // Deliberately inherits only the trace sink, delta streaming and the execution workspace, so a
    // sub-mission runs isolated under its own path (e.g. [Janus, Negotiate] and [Janus, Implement]
    // stay distinct). Root tools are not options state: they reach a child through the run's
    // pause scope.
    private static PipelineRunOptions CreateChildOptions(
        PipelineRunOptions parent,
        string childMissionName,
        Dictionary<string, string> childVars,
        IReadOnlyList<string> parentPath)
        => new(
            childMissionName,
            childVars,
            OnTrace: parent.OnTrace,
            MissionPath: [.. parentPath, childMissionName])
        {
            StreamLlmDeltas = parent.StreamLlmDeltas,
            ExecutionWorkspace = parent.ExecutionWorkspace,
        };

    // In a chat run the first step's input is the new message — the root mission's first
    // declared parameter. Seeded only when the run carries ChatHistory; every other run starts
    // with an empty initial output.
    private static string ChatInput(MissionDeclaration mission, IReadOnlyDictionary<string, string>? vars)
        => mission.Params.FirstOrDefault() is { } parameter && vars?.TryGetValue(parameter, out var value) == true
            ? value
            : string.Empty;

    private static bool IsNegotiationEligible(
        MissionDeclaration mission,
        IReadOnlyDictionary<string, ExpertDefinition> experts)
    {
        if (mission.MaxLoops <= 1) return false;

        foreach (var element in mission.Pipeline.Elements)
        {
            if (element is not StepElement stepElement
                || !experts.TryGetValue(stepElement.Step.ExpertName, out var expert)
                || expert.Kind != ExpertKinds.Llm
                || expert.IsAgent)
                return false;
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Step keys and the replay log
    // ------------------------------------------------------------------

    // One segment per call level, root to step: Mission@attempt#elementIndex[.branchIndex].
    // Names alone collide (Child -> Child, a loop retry).
    private static string StepKey(string prefix, string missionName, int attempt, int elementIndex)
        => $"{prefix}{missionName}@{attempt}#{elementIndex}";

    private static string ChildPrefix(string stepKey) => $"{stepKey}/";

    // The context keys the expert added or changed, typed string or double. Other values
    // (tool calls, structured objects) are re-derived, never logged.
    private static Dictionary<string, PipelineLoggedValue> StepWrites(
        Dictionary<string, object> before,
        Dictionary<string, object> after)
    {
        var writes = new Dictionary<string, PipelineLoggedValue>(StringComparer.Ordinal);
        foreach (var (key, value) in after)
        {
            if (before.TryGetValue(key, out var previous) && Equals(previous, value)) continue;
            if (PipelineLoggedValue.From(value) is { } logged) writes[key] = logged;
        }
        return writes;
    }

    // A pause or a root-tool failure ends the run at every depth; a loop never retries it.
    private static bool Halts(MissionResult result) => result.Pause is not null || result.Failure is not null;

    private static MissionResult Failure(string missionName, PipelineFailure failure)
        => new(missionName, string.Empty, MissionStatus.Fail, failure.ToString(), Failure: failure);

    // ------------------------------------------------------------------
    // Resolution
    // ------------------------------------------------------------------

    private IExpertRunner ResolveRunner(string? profileName)
    {
        var key = profileName ?? "default";
        return _runners.TryGetValue(key, out var runner)
            ? runner
            : throw new InvalidOperationException(
                $"Provider profile '{key}' not found. " +
                $"Add [providers.{key}] to forge.toml. Available: {string.Join(", ", _runners.Keys)}");
    }

    private IExpertRunner RunnerFor(ExpertDefinition expert, Step step, PipelineRunOptions options, string key, int attempt) => expert.Kind switch
    {
        ExpertKinds.Http        => new HttpExpertRunner(),
        ExpertKinds.Rule        => new RuleExpertRunner(),
        ExpertKinds.Onnx        => new OnnxExpertRunner(),
        ExpertKinds.JsonExtract => new JsonExtractExpertRunner(),
        ExpertKinds.Exec        => new ExecExpertRunner(_execution.DefaultTimeout, options.ExecutionWorkspace, key, attempt),
        ExpertKinds.Search      => new SearchExpertRunner(_webSearch
                                   ?? throw new InvalidOperationException(
                                       "kind: search requires a configured IWebSearch (Scout). " +
                                       "Pass one to the PipelineRunner constructor.")),
        ExpertKinds.Llm         => ResolveRunner(step.Using),
        _                       => throw new InvalidOperationException(
                                       $"Expert '{expert.Name}' has unknown kind '{expert.Kind}'.")
    };

    private static ExpertDefinition ResolveExpert(Dictionary<string, ExpertDefinition> experts, Step step)
        => experts.TryGetValue(step.ExpertName, out var expert)
            ? expert
            : throw new InvalidOperationException(
                $"Expert '{step.ExpertName}' not found. " +
                "Run 'forge validate' to check your mission before running.");

    private static bool CanReachAgent(Step step, Program ast, IReadOnlyDictionary<string, ExpertDefinition> experts)
    {
        if (experts.TryGetValue(step.ExpertName, out var expert)) return expert.IsAgent;
        return ast.FindMission(step.ExpertName) is { } mission && mission.Pipeline.Steps().Any(child => CanReachAgent(child, ast, experts));
    }

    // ------------------------------------------------------------------
    // Continuation identity: root inputs, local resume ordinals
    // ------------------------------------------------------------------

    private static IReadOnlyDictionary<string, string> RootInputs(
        IReadOnlyList<string> admittedNames, IReadOnlyDictionary<string, string>? vars)
        => (vars ?? new Dictionary<string, string>())
            .Where(pair => admittedNames.Contains(pair.Key, StringComparer.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private PipelineFailure? ConsumeLocalContinuation(PipelineContinuationCheckpoint checkpoint)
    {
        lock (_localResumeGate)
        {
            if (!_localContinuations.TryGetValue(checkpoint.RootExecutionId, out var state))
                return _locallyResumedContinuations.Add(checkpoint.SessionId) ? null : PipelineFailure.DuplicateContinuation;
            if (checkpoint.ContinuationOrdinal < state.CurrentOrdinal) return PipelineFailure.LateContinuation;
            if (checkpoint.ContinuationOrdinal > state.CurrentOrdinal) return PipelineFailure.InvalidContinuation;
            if (!state.Consumed.Add(checkpoint.ContinuationOrdinal)) return PipelineFailure.DuplicateContinuation;
            return null;
        }
    }

    private void RegisterIssuedContinuation(string rootExecutionId, int ordinal)
    {
        lock (_localResumeGate)
        {
            if (!_localContinuations.TryGetValue(rootExecutionId, out var state))
                _localContinuations[rootExecutionId] = state = new LocalContinuationState();
            state.CurrentOrdinal = ordinal;
        }
    }

    // ------------------------------------------------------------------
    // Expert invocation and streaming
    // ------------------------------------------------------------------

    // The one place a sequence step's expert is invoked. It streams only when the
    // durable executor sets StreamLlmDeltas and the step is a tool-free, non-judge llm step.
    // OnTrace alone never forces streaming: runners that do not override StreamAsync use
    // IExpertRunner's text-only default, which cannot preserve a failing StepEnvelope.
    private static async Task<StepEnvelope> InvokeExpertAsync(
        IExpertRunner runner,
        ExpertDefinition expert,
        Dictionary<string, object> context,
        string expertName,
        string key,
        MissionAttempt attempt,
        CancellationToken ct)
    {
        var options = attempt.Options;
        if (!StreamsStep(expert, context, options))
            return await runner.RunAsync(expert, context, ct);

        var text = new StringBuilder();
        await foreach (var chunk in runner.StreamAsync(expert, context, ct))
        {
            text.Append(chunk);

            if (options.OnTrace is { } onDelta && !string.IsNullOrEmpty(chunk))
                await onDelta(new PipelineStepDelta(options.MissionName, attempt.MissionPath, expertName, expert.Kind, attempt.Number, chunk) { StepKey = key }, ct);
        }

        // A non-judge llm step streams plain text and always passes.
        return new StepEnvelope(text.ToString());
    }

    private static bool StreamsStep(ExpertDefinition expert, Dictionary<string, object> context, PipelineRunOptions options) =>
        options.StreamLlmDeltas && IsPlainTextLlm(expert) && !context.ContainsKey(ContextKeys.Tools);

    private static bool IsPlainTextLlm(ExpertDefinition expert) =>
        expert.Kind == ExpertKinds.Llm && !expert.IsJudge;

    // ------------------------------------------------------------------
    // Run state
    // ------------------------------------------------------------------

    // What stays fixed across every step of one loop attempt of one mission.
    private sealed record MissionAttempt(
        Program Ast,
        Dictionary<string, ExpertDefinition> Experts,
        PipelineRunOptions Options,
        RunState Run,
        IReadOnlyList<string> MissionPath,
        int Number);

    private sealed record StepOutcome(string? FailReason, MissionResult? Halt);

    private sealed record BranchOutcome(string? FailReason, string NamedKey, string Output, MissionResult? Halt);

    private sealed record StepInvocation(
        StepEnvelope Envelope,
        bool Replayed,
        IReadOnlyList<FunctionCallContent>? RootCalls,
        IReadOnlyList<ChatMessage> Turn);

    /// <summary>
    /// One root run's shared state: the log of completed steps and, on a resume, the paused step
    /// to replay up to. Everything before the paused step in run order is in the log and nothing
    /// after it is, so one phase flag tells every invoke point whether it is replaying.
    /// </summary>
    private sealed class RunState
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, PipelineStepLogEntry> _log = new(StringComparer.Ordinal);
        private readonly string? _pausedKey;
        private readonly string? _pausedExpert;
        private readonly IReadOnlyList<ChatMessage>? _resumeTurn;

        public RunState(PauseScope? scope = null) => Scope = scope;

        public RunState(PauseScope scope, PipelineContinuationCheckpoint checkpoint, IReadOnlyList<ChatMessage> resumeTurn)
            : this(scope)
        {
            foreach (var entry in checkpoint.Log)
                _log[entry.Key] = entry;
            _pausedKey = checkpoint.PausedKey;
            _pausedExpert = checkpoint.ExpertName;
            _resumeTurn = resumeTurn;
            Replaying = true;
        }

        /// <summary>Set when the run can pause on a root tool call.</summary>
        public PauseScope? Scope { get; }

        public bool Replaying { get; private set; }

        /// <summary>Moves the replay past this step: returns its logged entry, or null when the step
        /// runs now. Reaching the paused agent ends the replay; a step that is neither logged nor the
        /// paused agent throws <see cref="ReplayDivergedException"/>.</summary>
        public PipelineStepLogEntry? AdvanceReplay(string key, string expertName, ExpertDefinition expert)
        {
            if (!Replaying) return null;
            lock (_gate)
            {
                if (_log.TryGetValue(key, out var entry)) return entry;
            }
            if (key != _pausedKey || expertName != _pausedExpert || !expert.IsAgent)
                throw new ReplayDivergedException();
            Replaying = false;
            return null;
        }

        public IReadOnlyList<ChatMessage>? ResumeTurnFor(string key) => key == _pausedKey ? _resumeTurn : null;

        public void Record(PipelineStepLogEntry entry)
        {
            lock (_gate) _log[entry.Key] = entry;
        }

        public IReadOnlyList<PipelineStepLogEntry> LogSnapshot()
        {
            lock (_gate) return [.. _log.Values];
        }
    }

    /// <summary>The root tool declarations and continuation identity of a pausable run.</summary>
    private sealed class PauseScope(
        IList<AITool> tools,
        IReadOnlyList<PipelineToolDeclaration> declarations,
        string rootMissionName,
        string definitionFingerprint,
        IReadOnlyDictionary<string, string> rootInputs,
        IReadOnlyList<string> admittedInputNames,
        string rootExecutionId,
        int ordinal)
    {
        private int _ordinal = ordinal;

        public IList<AITool> Tools { get; } = tools;
        public IReadOnlyList<PipelineToolDeclaration> Declarations { get; } = declarations;
        public string RootMissionName { get; } = rootMissionName;
        public string DefinitionFingerprint { get; } = definitionFingerprint;
        public string ToolScopeFingerprint { get; } = PipelineContinuationIdentity.ScopeFingerprint(declarations);
        public IReadOnlyDictionary<string, string> RootInputs { get; } = rootInputs;
        public IReadOnlyList<string> AdmittedInputNames { get; } = admittedInputNames;
        public string RootExecutionId { get; } = rootExecutionId;

        public int NextOrdinal() => ++_ordinal;
    }

    private sealed class ReplayDivergedException()
        : Exception("Replay reached a step that is neither logged nor the paused agent.");

    private sealed class LocalContinuationState
    {
        public int CurrentOrdinal { get; set; }
        public HashSet<int> Consumed { get; } = [];
    }
}
