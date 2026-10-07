using System.Diagnostics;
using System.Reflection;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Runtime;
using ForgeMission.Core.Tools;
using ForgeMission.Parser;
using ForgeMission.Tests.Runtime;
using Microsoft.Extensions.AI;

namespace ForgeMission.Tests.Cli;

// Controlled model calls exercise the production composition with real Bob, never a fake dispatcher.
public sealed class ForgeRunTests : IDisposable
{
    private static readonly Type RunType = ForgeText.Type("ForgeMission.Cli.ForgeRun");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-run-" + Guid.NewGuid().ToString("N"));
    private static readonly Dictionary<string, ExpertDefinition> Experts = new(StringComparer.Ordinal)
    {
        ["Respond"] = new("Respond", "any", "text", "Use tools.", Role: "agent"),
    };

    public ForgeRunTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Successive_write_read_edit_uses_correlated_results_and_closes_hands()
    {
        var hands = CreateHands();
        var declarations = (IReadOnlyList<AITool>)hands.GetType().GetProperty("ToolDeclarations")!.GetValue(hands)!;
        Assert.Equal(new[] { "Edit", "Read", "Write" }, declarations.Select(tool => tool.Name).Order());
        var runner = new StubExpertRunner((_, context) =>
        {
            var replies = Replies(context);
            if (replies.Count == 3)
            {
                Assert.Equal(new[] { "call-0", "call-1", "call-2" }, replies.Select(reply => reply.CallId));
                Assert.Equal("hello", replies[1].Result!.ToString());
                return new StepEnvelope("finished");
            }
            var call = replies.Count switch
            {
                0 => Call("Write", ("file_path", "greeting.txt"), ("content", "hello")),
                1 => Call("Read", ("file_path", "greeting.txt")),
                _ => Call("Edit", ("file_path", "greeting.txt"), ("old_string", "hello"), ("new_string", "goodbye")),
            };
            call = new FunctionCallContent($"call-{replies.Count}", call.Name, call.Arguments);
            context["tool_calls"] = new List<FunctionCallContent> { call };
            return new StepEnvelope("");
        });
        var result = await RunAsync(hands, runner);
        Assert.Equal("finished", result.Text);
        Assert.Equal("goodbye", await File.ReadAllTextAsync(Path.Combine(_root, "greeting.txt")));
        await AssertClosedAsync(hands);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("outside")]
    [InlineData("symlink")]
    [InlineData("io")]
    public async Task File_errors_resume_with_failed_reply_and_preserve_outside_files(string kind)
    {
        var outside = _root + "-outside.txt";
        await File.WriteAllTextAsync(outside, "sentinel");
        try
        {
            var target = kind == "outside" ? outside : "target.txt";
            if (kind == "symlink") File.CreateSymbolicLink(Path.Combine(_root, target), outside);
            if (kind == "io") Directory.CreateDirectory(Path.Combine(_root, target));
            var call = kind switch
            {
                "missing" => Call("Read", ("file_path", target)),
                "malformed" => Call("Write", ("file_path", target)),
                _ => Call("Write", ("file_path", target), ("content", "changed")),
            };
            var hands = CreateHands();
            var result = await RunAsync(hands, RespondWith(call, reply => Assert.StartsWith("ERROR [Failed]:", reply)));
            Assert.Equal("corrected", result.Text);
            Assert.Equal("sentinel", await File.ReadAllTextAsync(outside));
            await AssertClosedAsync(hands);
        }
        finally { File.Delete(outside); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsupported_or_multiple_calls_fail_and_close_hands(bool multiple)
    {
        var hands = CreateHands();
        var runner = new StubExpertRunner((_, context) =>
        {
            context["tool_calls"] = multiple
                ? new List<FunctionCallContent> { Call("Read"), Call("Read") }
                : new List<FunctionCallContent> { Call("Bash", ("command", "echo forbidden")) };
            return new StepEnvelope("");
        });
        var result = await RunAsync(hands, runner);
        Assert.Equal(MissionStatus.Fail, result.Status);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
        await AssertClosedAsync(hands);
    }

    [Fact]
    public async Task Provider_exception_closes_hands()
    {
        var hands = CreateHands();
        var runner = new StubExpertRunner((Func<string, Dictionary<string, object>, StepEnvelope>)((_, _) => throw new InvalidOperationException("provider failed")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(hands, runner));
        await AssertClosedAsync(hands);
    }

    [Fact]
    public async Task Cancellation_during_provider_prevents_dispatch_and_closes_hands()
    {
        using var cancel = new CancellationTokenSource();
        var hands = CreateHands(cancel.Token);
        var runner = new StubExpertRunner((_, context) =>
        {
            cancel.Cancel();
            context["tool_calls"] = new List<FunctionCallContent> { Call("Write", ("file_path", "forbidden.txt"), ("content", "no")) };
            return new StepEnvelope("");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(hands, runner, cancel.Token));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
        await AssertClosedAsync(hands);
    }

    [Fact]
    public async Task Tool_free_run_uses_same_lifetime_and_preserves_step_output()
    {
        var hands = CreateHands();
        using var steps = new StringWriter();
        var result = await RunAsync(hands, new StubExpertRunner((_, _) => "plain output"), steps: steps);
        Assert.Equal("plain output", result.Text);
        Assert.Contains("plain output", steps.ToString());
        await AssertClosedAsync(hands);
    }

    [Theory]
    [InlineData("stdout")]
    [InlineData("file")]
    [InlineData("unwritable")]
    public async Task Command_preserves_output_steps_and_nonzero_write_failure(string mode)
    {
        var expert = Path.Combine(_root, "experts", "Echo");
        Directory.CreateDirectory(expert);
        await File.WriteAllTextAsync(Path.Combine(expert, "expert.md"), "---\nname: Echo\nkind: exec\ncommand: python3\ninputs: [goal]\noutputKey: output\nargs: [./echo.py]\ninput: any\noutput: text\n---\n");
        await File.WriteAllTextAsync(Path.Combine(expert, "echo.py"), "import json\nprint(json.dumps({'output': 'plain output'}))\n");
        var output = mode == "stdout" ? "output(Root)" : $"output(Root, \"{(mode == "file" ? "answer.txt" : "missing/answer.txt")}\")";
        await File.WriteAllTextAsync(Path.Combine(_root, "mission.mcl"), "let apiKey = \"controlled-unused-key\"\nlet model = \"controlled-unused-model\"\nmission Root = { Echo }\n" + output);
        var initialized = await InvokeAsync("init");
        Assert.True(initialized.ExitCode == 0, initialized.Error);
        var result = await InvokeAsync("run", "--steps");
        Assert.True(result.ExitCode == (mode == "unwritable" ? 1 : 0), result.Error);
        if (mode == "stdout") Assert.Contains("plain output", result.Output);
        else Assert.Equal("", result.Output);
        Assert.Contains("Running mission 'Root'", result.Error);
        Assert.Contains("plain output", result.Error);
        if (mode == "file") Assert.Contains("plain output", await File.ReadAllTextAsync(Path.Combine(_root, "answer.txt")));
        if (mode == "unwritable") Assert.Contains("error", result.Error);
    }

    private object CreateHands(CancellationToken ct = default) =>
        RunType.GetMethod("CreateHands", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [_root, ct])!;

    private static Task<MissionResult> RunAsync(object hands, IExpertRunner expertRunner, CancellationToken ct = default, TextWriter? steps = null) =>
        (Task<MissionResult>)RunType.GetMethod("RunAsync", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
            [MclParser.Parse("mission Root = { Respond }"), Experts, new PipelineRunner(expertRunner), new PipelineRunOptions("Root", StepWriter: steps), hands, ct])!;

    private static async Task AssertClosedAsync(object hands)
    {
        var reply = await ((ICapabilityDispatcher)hands).DispatchAsync("file", new WriteFileCapabilityRequest("after.txt", "no"), CancellationToken.None);
        Assert.True(reply.IsError);
        Assert.Contains("closed", reply.Content);
    }

    private static StubExpertRunner RespondWith(FunctionCallContent call, Action<string> check) => new((_, context) =>
    {
        var replies = Replies(context);
        if (replies.Count > 0)
        {
            Assert.Equal(call.CallId, replies[0].CallId);
            check(replies[0].Result!.ToString()!);
            return new StepEnvelope("corrected");
        }
        context["tool_calls"] = new List<FunctionCallContent> { call };
        return new StepEnvelope("");
    });

    private static List<FunctionResultContent> Replies(Dictionary<string, object> context) => context.Values
        .OfType<IReadOnlyList<ChatMessage>>().SelectMany(messages => messages.SelectMany(message => message.Contents))
        .OfType<FunctionResultContent>().ToList();

    private static FunctionCallContent Call(string name, params (string Key, string Value)[] args) =>
        new("call", name, args.ToDictionary(arg => arg.Key, arg => (object?)arg.Value));

    private async Task<(int ExitCode, string Output, string Error)> InvokeAsync(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = _root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(RunType.Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        return (process.ExitCode, await stdout, await stderr);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
