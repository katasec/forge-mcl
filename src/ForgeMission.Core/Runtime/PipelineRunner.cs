using System.Buffers;
using System.Text;
using System.Text.Json;
using ForgeMission.Core.Adapters;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Manifest;
using ForgeMission.Parser;
using Microsoft.Extensions.AI;
using Scout;

namespace ForgeMission.Core.Runtime;

/// <summary>
/// The one MCL interpreter. A run with root tools can pause on an agent's tool call and resume by
/// replay (Phase 62): the checkpoint logs every completed step under its call-path key, and
/// <see cref="ResumeAsync"/> runs the mission from the top, returning each logged step's recorded
/// result instead of running it, until the paused agent continues its tool turn.
/// </summary>
public class PipelineRunner
{
    private readonly IReadOnlyDictionary<string, IExpertRunner> _runners;
    private readonly ExecutionConfig _execution;
    private readonly HashSet<string> _locallyResumedContinuations = new(StringComparer.Ordinal);
    private readonly object _localResumeGate = new();
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

    // Convenience: single default runner — keeps existing tests and callers unchanged.
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
        var scope = new PauseScope(rootTools, rootTools.Select(ToDeclaration).ToList(), options.MissionName,
            RootDefinitionFingerprint(ast, experts, options.MissionName), rootInputs, admittedNames,
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
        var pending = PipelineCheckpointCodec.PendingCall(checkpoint);
        if (!checkpoint.AdmittedInputNames.SequenceEqual(admittedNames, StringComparer.Ordinal)
            || checkpoint.RootInputs.Keys.Any(name => !admittedNames.Contains(name, StringComparer.Ordinal))
            || !string.Equals(pending.CallId, request.Result.CallId, StringComparison.Ordinal)
            || !checkpoint.ToolDeclarations.Any(tool => string.Equals(tool.Name, pending.Name, StringComparison.Ordinal))
            || !string.Equals(checkpoint.RootToolScopeFingerprint, ScopeFingerprint(checkpoint.ToolDeclarations), StringComparison.Ordinal)
            || !string.Equals(checkpoint.RootDefinitionFingerprint, RootDefinitionFingerprint(ast, experts, root), StringComparison.Ordinal))
            return Failure(root, PipelineFailure.InvalidContinuation);

        if (ConsumeLocalContinuation(checkpoint) is { } localFailure)
            return Failure(root, localFailure);

        if (FindMission(ast, root) is null)
            return Failure(root, PipelineFailure.InvalidContinuation);

        var tools = checkpoint.ToolDeclarations.Select(tool => (AITool)new Katasec.AITools.DeclaredTool(
            tool.Name, tool.Description, tool.InputSchema)).ToList();
        var scope = new PauseScope(tools, checkpoint.ToolDeclarations, root, checkpoint.RootDefinitionFingerprint,
            checkpoint.RootInputs, admittedNames, checkpoint.RootExecutionId, checkpoint.ContinuationOrdinal);
        var run = new RunState(scope, checkpoint, ResumedTurn(checkpoint, request.Result));
        var options = observers with { MissionName = root, Vars = checkpoint.RootInputs, RootTools = tools, MissionPath = null };

        try
        {
            var result = await RunCoreAsync(ast, experts, options, run, string.Empty, ct);
            // Still replaying: the run finished without reaching the paused agent (R6).
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
        var mission = FindMission(ast, options.MissionName)
            ?? throw new InvalidOperationException(
                $"Mission '{options.MissionName}' not found in .mcl file");

        var maxLoops = mission.MaxLoops;
        MissionResult? lastResult = null;
        string? loopFeedback = null;
        var history = IsNegotiationEligible(mission, experts) ? new SpeakerTranscript() : null;

        // Fresh array, root mission first, ending with this mission (Phase 43.16 Task 3). A
        // top-level call leaves options.MissionPath null; CreateChildOptions supplies it for a
        // nested sub-mission invocation.
        IReadOnlyList<string> missionPath = options.MissionPath ?? [options.MissionName];

        for (var attempt = 1; attempt <= maxLoops; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (!run.Replaying && options.StepWriter is { } sw && maxLoops > 1)
                await sw.WriteLineAsync($"(attempt {attempt}/{maxLoops})");

            var context = ContextBuilder.Seed(ast, options.Vars, options.ContextObjects);
            if (options.ChatHistory is not null)
            {
                context[ChatHistory.ContextKey] = options.ChatHistory;
                context["output"] = ChatInput(mission, options.Vars);
            }
            context["attempt"]   = attempt.ToString();
            context["max_loops"] = maxLoops.ToString();
            if (loopFeedback is not null)
                context["feedback"] = loopFeedback;
            if (history is not null)
                context["history"] = history;

            string? failReason = null;

            // Track whether any when()-guarded step matched — used for when(else) and error detection.
            var anyGuardedStepMatched = false;
            var hasGuardedSteps       = mission.Pipeline.Elements
                .OfType<StepElement>()
                .Any(e => e.Step.When is StringEqualsWhen or NumericCompareWhen);
            var hasElseBranch         = mission.Pipeline.Elements
                .OfType<StepElement>()
                .Any(e => e.Step.When is ElseWhen);

            // Tool continuation (42.3): pre-agent already ran on the original user turn and its
            // outputs were restored from the enrichment cache — resume at the agent step.
            var skipPreAgent = options.StartAtAgent;

            var elements = mission.Pipeline.Elements;
            for (var index = 0; index < elements.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var element = elements[index];
                var stepKey = StepKey(keyPrefix, options.MissionName, attempt, index);

                if (skipPreAgent)
                {
                    if (element is StepElement pre
                        && experts.TryGetValue(pre.Step.ExpertName, out var preExpert)
                        && preExpert.IsAgent)
                        skipPreAgent = false;   // reached the agent segment — run from here
                    else
                        continue;               // pre-agent element: skip (cache restored its outputs)
                }

                if (element is ParallelElement parallel)
                {
                    var parallelOutcome = await ExecuteParallelAsync(
                        parallel, stepKey, ast, experts, context, options, run, missionPath, attempt, ct);
                    if (parallelOutcome.Halt is { } parallelHalt) return parallelHalt;
                    failReason = parallelOutcome.FailReason;
                    if (failReason is not null) break;
                    continue;
                }

                if (element is StepElement se)
                {
                    var step = se.Step;

                    if (step.When is StringEqualsWhen sw2)
                    {
                        var matched = context.TryGetValue(sw2.Key, out var val)
                                      && val?.ToString() == sw2.Value;
                        if (!matched) continue;
                        anyGuardedStepMatched = true;
                    }
                    else if (step.When is NumericCompareWhen nw)
                    {
                        var matched = context.TryGetValue(nw.Key, out var raw)
                                      && TryParseDouble(raw, out var actual)
                                      && EvaluateNumericOp(actual, nw.Op, nw.Threshold);
                        if (!matched) continue;
                        anyGuardedStepMatched = true;
                    }
                    else if (step.When is ElseWhen)
                    {
                        if (anyGuardedStepMatched) continue;
                    }

                    var outcome = await ExecuteStepAsync(step, stepKey, ast, experts, context, options, run, missionPath, attempt, ct);
                    if (outcome.Halt is { } halt) return halt;
                    failReason = outcome.FailReason;
                    if (failReason is not null) break;

                    // Per-call client tools (42.3): a tool call ends the run; the caller continues it.
                    if (context.TryGetValue("tool_calls", out var tc)
                        && tc is IReadOnlyList<FunctionCallContent> toolCalls)
                    {
                        if (options.OnTrace is { } onToolRequested
                            && experts.TryGetValue(step.ExpertName, out var toolExpert))
                        {
                            await onToolRequested(new PipelineToolRequested(
                                options.MissionName, missionPath, step.ExpertName, toolExpert.Kind,
                                attempt, ToPipelineToolCalls(toolCalls)) { StepKey = stepKey }, ct);
                        }

                        var toolText = context.TryGetValue("output", out var o) ? o?.ToString() ?? string.Empty : string.Empty;
                        return new MissionResult(options.MissionName, toolText, MissionStatus.Pass, null, attempt, toolCalls);
                    }
                }
            }

            if (failReason is null && hasGuardedSteps && !anyGuardedStepMatched && !hasElseBranch)
                throw new InvalidOperationException(
                    "No when() guard matched and no when(else) branch exists in the pipeline.");

            // Carry feedback written by rule/judge experts into the next loop iteration.
            if (context.TryGetValue("feedback", out var fb))
                loopFeedback = fb?.ToString();

            var text = context.TryGetValue("output", out var last) ? last?.ToString() ?? string.Empty : string.Empty;

            if (failReason is null)
                return new MissionResult(options.MissionName, text, MissionStatus.Pass, null, attempt);

            lastResult = new MissionResult(options.MissionName, text, MissionStatus.Fail, failReason, attempt);
        }

        return lastResult!;
    }

    private async Task<StepOutcome> ExecuteStepAsync(
        Step step,
        string key,
        Program ast,
        Dictionary<string, ExpertDefinition> experts,
        Dictionary<string, object> context,
        PipelineRunOptions options,
        RunState run,
        IReadOnlyList<string> missionPath,
        int attempt,
        CancellationToken ct)
    {
        // Sub-mission: step name matches a declared mission → recurse. No synthetic lifecycle fact
        // is emitted for the sub-mission invocation itself (Phase 43.16 Task 3) — the actual
        // experts inside it, at the deeper path CreateChildOptions builds, are the visible trail.
        if (FindMission(ast, step.ExpertName) is not null)
        {
            var childVars = step.Context.ToDictionary(
                b => b.Key,
                b => ContextBuilder.ResolveBindingValue(b.Value, context),
                StringComparer.Ordinal);

            if (!run.Replaying && options.StepWriter is { } msw)
                await msw.WriteLineAsync($"→ {step.ExpertName} (mission)...");

            var subResult = await RunCoreAsync(ast, experts,
                CreateChildOptions(options, step.ExpertName, childVars, missionPath), run, ChildPrefix(key), ct);
            if (Halts(subResult)) return new StepOutcome(null, subResult);

            context["output"] = subResult.Text;

            return new StepOutcome(subResult.Status == MissionStatus.Fail
                ? $"[{step.ExpertName}] {subResult.FailReason ?? "sub-mission failed"}"
                : null, null);
        }

        var expert = ResolveExpert(experts, step);

        foreach (var binding in step.Context)
            context[binding.Key] = ContextBuilder.ResolveBindingValue(binding.Value, context);

        var invocation = await InvokeStepAsync(step, key, expert, context, options, run, missionPath, attempt, inParallel: false, ct);
        if (invocation.RootCalls is { } calls)
            return new StepOutcome(null, await PauseAsync(run, key, step, expert, invocation, calls, options, missionPath, attempt, ct));

        var envelope = invocation.Envelope;
        context["output"] = envelope.Text;

        if (context.TryGetValue("history", out var historyValue)
            && historyValue is SpeakerTranscript history)
        {
            var text = envelope.Text;
            if (!string.IsNullOrWhiteSpace(envelope.Reason)
                && !string.Equals(envelope.Reason, text, StringComparison.Ordinal))
                text = $"{text}\n\nReason: {envelope.Reason}";

            history.Add(step.ExpertName, text);
        }

        // Emitted for both pass and fail envelopes, after the output/history update above and
        // before the caller decides whether the envelope fails the mission — always awaited
        // before the next step begins (Phase 43.16 Task 3). A replayed step reports nothing (R4).
        if (!invocation.Replayed && options.OnTrace is { } onCompleted)
            await onCompleted(new PipelineStepCompleted(options.MissionName, missionPath, step.ExpertName, expert.Kind, attempt, envelope) { StepKey = key }, ct);

        return new StepOutcome(envelope.Status == "fail"
            ? $"[{step.ExpertName}] {envelope.Reason ?? "step failed"}"
            : null, null);
    }

    // A parallel block runs its branches concurrently on one context snapshot. When the run can
    // pause and a branch can reach an agent, branches run one at a time in source order instead:
    // two tool pauses can never be outstanding at once (R3).
    private async Task<StepOutcome> ExecuteParallelAsync(
        ParallelElement parallel,
        string key,
        Program ast,
        Dictionary<string, ExpertDefinition> experts,
        Dictionary<string, object> context,
        PipelineRunOptions options,
        RunState run,
        IReadOnlyList<string> missionPath,
        int attempt,
        CancellationToken ct)
    {
        if (!run.Replaying && options.StepWriter is { } writer)
            await writer.WriteLineAsync($"→ parallel {{ {string.Join(", ", parallel.Steps.Select(s => s.ExpertName))} }}");

        var snapshot = new Dictionary<string, object>(context, StringComparer.Ordinal);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var branches = parallel.Steps.Select((step, branch) => (step, key: $"{key}.{branch}")).ToList();
        Task<BranchOutcome> Branch((Step step, string key) branch) => ExecuteParallelStepAsync(
            branch.step, branch.key, ast, experts, snapshot, options, run, missionPath, attempt, linkedCts);

        var inOrder = run.Scope is not null && parallel.Steps.Any(step => CanReachAgent(step, ast, experts));
        var results = inOrder
            ? await RunBranchesInOrderAsync(branches, Branch)
            : await RunBranchesConcurrentlyAsync(branches, Branch, ct);

        if (results.FirstOrDefault(result => result.Halt is not null)?.Halt is { } halt)
            return new StepOutcome(null, halt);

        foreach (var result in results)
            context[result.NamedKey] = result.Output;

        if (!run.Replaying && options.StepWriter is { } endWriter)
            await endWriter.WriteLineAsync();

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
        Program ast,
        Dictionary<string, ExpertDefinition> experts,
        Dictionary<string, object> baseContext,
        PipelineRunOptions options,
        RunState run,
        IReadOnlyList<string> missionPath,
        int attempt,
        CancellationTokenSource cts)
    {
        var namedKey = $"{step.ExpertName}.output";

        // Sub-mission in parallel block → recurse with isolated child context. No synthetic
        // lifecycle fact for the sub-mission invocation itself, same as the sequential path.
        if (FindMission(ast, step.ExpertName) is not null)
        {
            var childVars = step.Context.ToDictionary(
                b => b.Key,
                b => ContextBuilder.ResolveBindingValue(b.Value, baseContext),
                StringComparer.Ordinal);

            var subResult = await RunCoreAsync(ast, experts,
                CreateChildOptions(options, step.ExpertName, childVars, missionPath), run, ChildPrefix(key),
                cts.Token);
            if (Halts(subResult)) return new BranchOutcome(null, namedKey, subResult.Text, subResult);

            if (subResult.Status == MissionStatus.Fail)
            {
                cts.Cancel();
                return new BranchOutcome($"[{step.ExpertName}] {subResult.FailReason ?? "sub-mission failed"}", namedKey, subResult.Text, null);
            }

            return new BranchOutcome(null, namedKey, subResult.Text, null);
        }

        var expert = ResolveExpert(experts, step);

        // Each parallel step gets its own context copy so with-bindings don't interfere.
        var localContext = new Dictionary<string, object>(baseContext, StringComparer.Ordinal);
        foreach (var binding in step.Context)
            localContext[binding.Key] = ContextBuilder.ResolveBindingValue(binding.Value, localContext);

        var invocation = await InvokeStepAsync(step, key, expert, localContext, options, run, missionPath, attempt, inParallel: true, cts.Token);
        if (invocation.RootCalls is { } calls)
            return new BranchOutcome(null, namedKey, string.Empty,
                await PauseAsync(run, key, step, expert, invocation, calls, options, missionPath, attempt, cts.Token));

        // Parallel steps may call the sink concurrently and retain their own facts/path/attempt
        // (Task 3 imposes no global sequence across them).
        var envelope = invocation.Envelope;
        if (!invocation.Replayed && options.OnTrace is { } onCompleted)
            await onCompleted(new PipelineStepCompleted(options.MissionName, missionPath, step.ExpertName, expert.Kind, attempt, envelope) { StepKey = key }, cts.Token);

        if (envelope.Status == "fail")
        {
            cts.Cancel(); // Signal siblings to stop.
            return new BranchOutcome($"[{step.ExpertName}] {envelope.Reason ?? "step failed"}", namedKey, envelope.Text, null);
        }

        return new BranchOutcome(null, namedKey, envelope.Text, null);
    }

    // The one invoke point for a step's expert, shared by sequence and parallel steps. On replay a
    // logged step returns its recorded envelope and writes, and runs or reports nothing (R4). A live
    // step runs and is logged, unless it is an agent that paused on a root tool call.
    private async Task<StepInvocation> InvokeStepAsync(
        Step step,
        string key,
        ExpertDefinition expert,
        Dictionary<string, object> context,
        PipelineRunOptions options,
        RunState run,
        IReadOnlyList<string> missionPath,
        int attempt,
        bool inParallel,
        CancellationToken ct)
    {
        if (run.Replay(key, step.ExpertName, expert) is { } logged)
        {
            foreach (var (name, value) in logged.Writes)
                context[name] = value.ToContextValue();
            return new StepInvocation(new StepEnvelope(logged.Text, logged.Status, logged.Reason), Replayed: true, null, []);
        }

        var runner = RunnerFor(expert, step, options, key, attempt);

        // Before invoking a real expert (Phase 43.16 Task 3): its attempt is the enclosing
        // mission's current loop attempt.
        if (options.OnTrace is { } onStarted)
            await onStarted(new PipelineStepStarted(options.MissionName, missionPath, step.ExpertName, expert.Kind, attempt) { StepKey = key }, ct);

        if (!inParallel && options.StepWriter is { } sw)
            await sw.WriteLineAsync($"→ {step.ExpertName}...");

        // Reached the agent segment on a fresh user turn: hand the caller the pre-agent output
        // for the enrichment cache (42.3 §3) — continuations restore it instead of re-running.
        if (!inParallel && expert.IsAgent && !options.StartAtAgent)
            options.OnPreAgentComplete?.Invoke(StringSnapshot(context));

        // Taken after bindings and before tools: the log holds only what the expert itself wrote.
        var before = new Dictionary<string, object>(context, StringComparer.Ordinal);
        var turn = run.ResumeTurnFor(key);
        AttachTools(expert, context, options, run, turn, inParallel);

        StepEnvelope envelope;
        object? responseMessages = null;
        try
        {
            envelope = inParallel
                ? await runner.RunAsync(expert, context, ct)
                : await InvokeExpertAsync(runner, expert, context, options, missionPath, step.ExpertName, key, attempt, ct);
        }
        catch (Exception ex) when (!inParallel && ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Step '{step.ExpertName}' failed: {ex.Message}", ex);
        }
        finally
        {
            context.Remove("tools");
            context.Remove(PipelineRuntimeInstructions.AllowMultipleToolCalls);
            context.Remove(PipelineToolContinuationInstructions.TurnMessages);
            context.Remove(PipelineToolContinuationInstructions.ResponseMessages, out responseMessages);
        }

        if (run.Scope is not null && expert.IsAgent
            && context.Remove("tool_calls", out var raw) && raw is IReadOnlyList<FunctionCallContent> calls)
            return new StepInvocation(envelope, Replayed: false, calls, NormalizeToolTurn(turn, responseMessages, calls));

        run.Record(new PipelineStepLogEntry(key, envelope.Text, envelope.Status, envelope.Reason, StepWrites(before, context)));
        return new StepInvocation(envelope, Replayed: false, null, []);
    }

    // Existing call-only runners and complete provider replies converge before checkpointing.
    private static IReadOnlyList<ChatMessage> NormalizeToolTurn(IReadOnlyList<ChatMessage>? turn,
        object? responseMessages, IReadOnlyList<FunctionCallContent> calls) =>
        [.. turn ?? [], .. responseMessages is IReadOnlyList<ChatMessage> messages
            ? messages : [new ChatMessage(ChatRole.Assistant, [.. calls])]];

    // Tools attach to the agent expert's call only (42.3) — enrichment and verification experts
    // never see them. Root tools (and a resumed tool turn) reach agents at every depth; per-call
    // client tools reach only a sequence step of the mission they were passed to.
    private static void AttachTools(
        ExpertDefinition expert,
        Dictionary<string, object> context,
        PipelineRunOptions options,
        RunState run,
        IReadOnlyList<ChatMessage>? turn,
        bool inParallel)
    {
        if (!expert.IsAgent) return;

        if (run.Scope is { } scope)
        {
            context["tools"] = scope.Tools;
            // One call per pause is Core's rule (PipelineToolPause holds exactly one ToolCall).
            context[PipelineRuntimeInstructions.AllowMultipleToolCalls] = false;
            if (turn is not null)
                context[PipelineToolContinuationInstructions.TurnMessages] = turn;
            return;
        }

        if (inParallel || options.Tools is not { Count: > 0 }) return;
        context["tools"] = options.Tools;
        if (options.AllowMultipleToolCalls is { } allowMultiple)
            context[PipelineRuntimeInstructions.AllowMultipleToolCalls] = allowMultiple;
    }

    // A root tool call ends the whole run. The checkpoint holds the log of completed steps, this
    // step's key, and its tool turn so far, so a resume can replay up to here and continue the turn.
    private async Task<MissionResult> PauseAsync(
        RunState run,
        string key,
        Step step,
        ExpertDefinition expert,
        StepInvocation invocation,
        IReadOnlyList<FunctionCallContent> calls,
        PipelineRunOptions options,
        IReadOnlyList<string> missionPath,
        int attempt,
        CancellationToken ct)
    {
        var scope = run.Scope!;
        if (options.OnTrace is { } onCompleted)
            await onCompleted(new PipelineStepCompleted(options.MissionName, missionPath, step.ExpertName, expert.Kind, attempt, invocation.Envelope) { StepKey = key }, ct);

        if (calls.Count != 1) return Failure(scope.RootMissionName, PipelineFailure.MultipleOutstandingTools);
        var call = ToPipelineToolCalls(calls).Single();
        if (!scope.Declarations.Any(declaration => string.Equals(declaration.Name, call.Name, StringComparison.Ordinal)))
            return Failure(scope.RootMissionName, PipelineFailure.UnsupportedTool);

        if (options.OnTrace is { } onCheckpointed)
            await onCheckpointed(new PipelineRootToolCheckpointed(options.MissionName, missionPath, step.ExpertName, expert.Kind, attempt, call) { StepKey = key }, ct);

        var ordinal = scope.NextOrdinal();
        RegisterIssuedContinuation(scope.RootExecutionId, ordinal);
        var checkpoint = new PipelineContinuationCheckpoint(PipelineCheckpointCodec.CheckpointVersion,
            Guid.NewGuid().ToString("N"), scope.RootExecutionId, ordinal, scope.RootMissionName,
            scope.DefinitionFingerprint, scope.ToolScopeFingerprint, scope.Declarations, missionPath, step.ExpertName,
            attempt, invocation.Turn, scope.RootInputs, scope.AdmittedInputNames,
            run.LogSnapshot(), key);
        var pause = new PipelineToolPause(scope.RootMissionName, missionPath, step.ExpertName, attempt, call,
            new PipelineContinuation(PipelineCheckpointCodec.EnvelopeVersion, PipelineCheckpointCodec.Write(checkpoint)));
        return new MissionResult(scope.RootMissionName, string.Empty, MissionStatus.Pass, Attempts: attempt, Pause: pause);
    }

    // One small helper (Phase 43.16 Task 3) replacing the two ad-hoc child `new
    // PipelineRunOptions(...)` calls in ExecuteStepAsync/ExecuteParallelStepAsync. Deliberately
    // does not inherit ContextObjects, Tools, StartAtAgent, or OnPreAgentComplete — preserving
    // today's isolated sub-mission/tool semantics (the essential Janus fix: Proposer/Approver run
    // under [Janus, Negotiate], Implementer under [Janus, Implement]). Root tools are not options
    // state: they reach a child through the run's pause scope.
    private static PipelineRunOptions CreateChildOptions(
        PipelineRunOptions parent,
        string childMissionName,
        Dictionary<string, string> childVars,
        IReadOnlyList<string> parentPath)
        => new(
            childMissionName,
            childVars,
            parent.StepWriter,
            parent.ContentWriter,
            OnSearchProgress: parent.OnSearchProgress,
            OnTrace: parent.OnTrace,
            MissionPath: [.. parentPath, childMissionName])
        {
            StreamLlmDeltas = parent.StreamLlmDeltas,
            ExecutionWorkspace = parent.ExecutionWorkspace,
        };

    // Phase 58: in a chat run the first step's input is the new message — the root mission's first
    // declared parameter — not "Begin.". Seeded only when the run carries ChatHistory, so every
    // other run keeps today's empty initial output.
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
                || !expert.Kind.Equals("llm", StringComparison.OrdinalIgnoreCase)
                || expert.IsAgent)
                return false;
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Step keys and the replay log
    // ------------------------------------------------------------------

    // One segment per call level, root to step: Mission@attempt#elementIndex[.branchIndex] (R1).
    // Names alone collide (Child -> Child, a loop retry).
    private static string StepKey(string prefix, string missionName, int attempt, int elementIndex)
        => $"{prefix}{missionName}@{attempt}#{elementIndex}";

    private static string ChildPrefix(string stepKey) => $"{stepKey}/";

    // The context keys the expert added or changed, typed string or double (R5). Other values
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
        "http"         => new HttpExpertRunner(),
        "rule"         => new RuleExpertRunner(),
        "onnx"         => new OnnxExpertRunner(),
        "json_extract" => new JsonExtractExpertRunner(),
        "exec"         => new ExecExpertRunner(_execution.DefaultTimeout, options.ExecutionWorkspace, key, attempt),
        "search"       => new SearchExpertRunner(_webSearch
                              ?? throw new InvalidOperationException(
                                  "kind: search requires a configured IWebSearch (Scout). " +
                                  "Pass one to the PipelineRunner constructor."),
                              options.OnSearchProgress),
        _              => ResolveRunner(step.Using)
    };

    private static ExpertDefinition ResolveExpert(Dictionary<string, ExpertDefinition> experts, Step step)
        => experts.TryGetValue(step.ExpertName, out var expert)
            ? expert
            : throw new InvalidOperationException(
                $"Expert '{step.ExpertName}' not found. " +
                "Run 'forge validate' to check your mission before running.");

    private static MissionDeclaration? FindMission(Program ast, string name)
        => ast.Declarations.OfType<MissionDeclaration>().FirstOrDefault(m => m.Name == name);

    private static bool CanReachAgent(Step step, Program ast, IReadOnlyDictionary<string, ExpertDefinition> experts)
    {
        if (experts.TryGetValue(step.ExpertName, out var expert)) return expert.IsAgent;
        return FindMission(ast, step.ExpertName) is { } mission && mission.Pipeline.Elements.Any(element => element switch
        {
            StepElement child => CanReachAgent(child.Step, ast, experts),
            ParallelElement child => child.Steps.Any(childStep => CanReachAgent(childStep, ast, experts)),
            _ => false,
        });
    }

    // ------------------------------------------------------------------
    // Continuation identity: fingerprints, root inputs, local resume ordinals
    // ------------------------------------------------------------------

    private static string RootDefinitionFingerprint(Program ast, IReadOnlyDictionary<string, ExpertDefinition> experts, string rootMission)
        => PipelineDefinitionFingerprint.Compute(ast, experts, rootMission);
    // Pause hashes the declared schemas; resume hashes the checkpoint's re-serialized copies. Both
    // hash the same compact form so schema whitespace never invalidates a continuation.
    private static string ScopeFingerprint(IEnumerable<PipelineToolDeclaration> declarations) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            declarations.Select(d => $"{d.Name}\u001f{d.Description}\u001f{CompactJson(d.InputSchema)}")))));

    private static string CompactJson(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) element.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static IReadOnlyDictionary<string, string> RootInputs(
        IReadOnlyList<string> admittedNames, IReadOnlyDictionary<string, string>? vars)
        => (vars ?? new Dictionary<string, string>())
            .Where(pair => admittedNames.Contains(pair.Key, StringComparer.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    private static PipelineToolDeclaration ToDeclaration(AITool tool)
    {
        if (tool is not AIFunction function) throw new InvalidOperationException($"Root tool '{tool.Name}' must be an AIFunction declaration.");
        return new PipelineToolDeclaration(function.Name, function.Description ?? string.Empty, function.JsonSchema.Clone());
    }

    // The resumed agent's whole tool turn, as providers expect it resent: every earlier call and
    // result, the pending call, then this result.
    private static IReadOnlyList<ChatMessage> ResumedTurn(PipelineContinuationCheckpoint checkpoint, PipelineToolResult result) =>
        [.. checkpoint.TurnMessages, new ChatMessage(ChatRole.Tool, [new FunctionResultContent(result.CallId, ToolResultText(result))])];

    private static string ToolResultText(PipelineToolResult result) => result.Status switch
    {
        PipelineToolResultStatus.Succeeded => result.Content ?? string.Empty,
        _ => $"ERROR [{result.Status}]: {result.Content ?? "Tool did not complete."}",
    };

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
    // Tool-call and context conversions
    // ------------------------------------------------------------------

    // Converts each provider-SDK FunctionCallContent to the closed PipelineToolCall shape (Phase
    // 43.16 Task 3) — no provider SDK object crosses into the trace. Mirrors
    // ForgeMission.Runner's RunnerToolTurnMapper.ToJsonElement/WriteValue (Core cannot reference
    // Runner, so the small Utf8JsonWriter-based conversion is duplicated here rather than
    // reflection-serialized).
    private static IReadOnlyList<PipelineToolCall> ToPipelineToolCalls(IReadOnlyList<FunctionCallContent> calls)
        => calls.Select(call => new PipelineToolCall(
            call.CallId,
            call.Name,
            ToolArgumentsToJsonElement(call.Arguments))).ToList();

    private static JsonElement ToolArgumentsToJsonElement(IDictionary<string, object?>? arguments)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        foreach (var (name, value) in arguments ?? new Dictionary<string, object?>())
        {
            writer.WritePropertyName(name);
            WriteToolArgumentValue(writer, value);
        }
        writer.WriteEndObject();
        writer.Flush();

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteToolArgumentValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case string text:
                writer.WriteStringValue(text);
                return;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                return;
            case long integer:
                writer.WriteNumberValue(integer);
                return;
            case double number:
                writer.WriteNumberValue(number);
                return;
            case JsonElement element:
                element.WriteTo(writer);
                return;
            default:
                throw new InvalidOperationException(
                    $"Unsupported tool argument type '{value.GetType().FullName}'.");
        }
    }

    // The restorable slice of the context bag: string values only. Structured objects
    // (conversation/system/tools) are re-derived from the request on every call.
    private static Dictionary<string, string> StringSnapshot(Dictionary<string, object> context)
        => context.Where(kv => kv.Value is string)
                  .ToDictionary(kv => kv.Key, kv => (string)kv.Value, StringComparer.Ordinal);

    private static bool TryParseDouble(object? value, out double result)
    {
        result = 0;
        return value switch
        {
            double d   => (result = d)    == d,
            float f    => (result = f)    == f,
            int i      => (result = i)    == i,
            long l     => (result = l)    == l,
            string s   => double.TryParse(s, System.Globalization.NumberStyles.Any,
                              System.Globalization.CultureInfo.InvariantCulture, out result),
            _          => false
        };
    }

    private static bool EvaluateNumericOp(double actual, CompOp op, double threshold) => op switch
    {
        CompOp.Gt  => actual >  threshold,
        CompOp.Lt  => actual <  threshold,
        CompOp.Gte => actual >= threshold,
        CompOp.Lte => actual <= threshold,
        CompOp.Eq  => Math.Abs(actual - threshold) < 1e-10,
        _          => false
    };

    // ------------------------------------------------------------------
    // Expert invocation and streaming
    // ------------------------------------------------------------------

    // The one place a sequence step's expert is invoked (Phase 53.8). It streams only when a caller
    // asked for text as it is written: a step or content writer (CLI, serve), or the durable
    // executor's StreamLlmDeltas for a tool-free, non-judge llm step. OnTrace alone never forces
    // streaming: several non-LLM runners expose a text-only streaming adapter that cannot preserve
    // a failing StepEnvelope.
    private static async Task<StepEnvelope> InvokeExpertAsync(
        IExpertRunner runner,
        ExpertDefinition expert,
        Dictionary<string, object> context,
        PipelineRunOptions options,
        IReadOnlyList<string> missionPath,
        string expertName,
        string key,
        int attempt,
        CancellationToken ct)
    {
        if (!StreamsStep(expert, context, options))
            return await runner.RunAsync(expert, context, ct);

        var text = new StringBuilder();
        await foreach (var chunk in runner.StreamAsync(expert, context, ct))
        {
            if (options.StepWriter is { } stepWriter)
                await stepWriter.WriteAsync(chunk);
            if (options.ContentWriter is { } contentWriter)
                await contentWriter.WriteAsync(chunk);
            text.Append(chunk);

            if (options.OnTrace is { } onDelta && !string.IsNullOrEmpty(chunk))
                await onDelta(new PipelineStepDelta(options.MissionName, missionPath, expertName, expert.Kind, attempt, chunk) { StepKey = key }, ct);
        }
        if (options.StepWriter is { } endWriter)
            await endWriter.WriteLineAsync("\n");

        // A non-judge llm step streams plain text (no envelope instruction) and always passes; a
        // judge and every non-llm kind still stream the envelope they always have.
        return IsPlainTextLlm(expert) ? new StepEnvelope(text.ToString()) : ParseStreamedEnvelope(text.ToString());
    }

    private static bool StreamsStep(ExpertDefinition expert, Dictionary<string, object> context, PipelineRunOptions options) =>
        options.StepWriter is not null
        || options.ContentWriter is not null
        || (options.StreamLlmDeltas && IsPlainTextLlm(expert) && !context.ContainsKey("tools"));

    private static bool IsPlainTextLlm(ExpertDefinition expert) =>
        expert.Kind.Equals("llm", StringComparison.OrdinalIgnoreCase) && !expert.IsJudge;

    private static StepEnvelope ParseStreamedEnvelope(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize(raw.Trim(), StepEnvelopeContext.Default.StepEnvelope)
                ?? new StepEnvelope(raw);
        }
        catch (JsonException)
        {
            return new StepEnvelope(raw);
        }
    }

    // ------------------------------------------------------------------
    // Run state
    // ------------------------------------------------------------------

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
        private readonly object _gate = new();
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

        /// <summary>The logged entry for a replayed step, or null when the step runs now. A step
        /// that is neither logged nor the paused agent means the replay diverged (R6).</summary>
        public PipelineStepLogEntry? Replay(string key, string expertName, ExpertDefinition expert)
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
        public string ToolScopeFingerprint { get; } = ScopeFingerprint(declarations);
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
