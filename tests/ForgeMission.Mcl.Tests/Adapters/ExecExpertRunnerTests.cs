using ForgeMission.Core.Adapters;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Runtime;
using ForgeMission.Parser;
using ForgeMission.Tests.Runtime;
using System.Diagnostics;
using System.Text.Json;

namespace ForgeMission.Tests.Adapters;

public class ExecExpertRunnerTests : IDisposable
{
    [SkippableFact]
    public async Task Workspace_bindings_are_process_local_expert_relative_and_alias_aware()
    {
        var python = RequirePython();
        var relative = "inputs/invoice/content.bin";
        var file = Path.Combine(_dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "verified input");
        var script = Script("""
            import json,os,sys
            d=json.load(sys.stdin)
            assert open(d['source_file']).read() == 'verified input'
            assert os.environ['FORGE_SOURCE_FILE'] == d['source_file']
            assert os.environ['FORGE_INPUT_source_file'] == d['source_file']
            assert 'FORGE_INPUT_mode' not in os.environ
            assert os.path.realpath(os.getcwd()) == os.path.realpath(os.path.dirname(__file__))
            assert os.path.realpath(os.getcwd()) != os.path.realpath(d['work_dir'])
            open(os.path.join(d['output_dir'],'proof.txt'),'w').write(d['mode'])
            print(json.dumps({'result': 'bound'}))
            """);
        var expertDirectory = Path.Combine(_dir, "experts", "Execute");
        Directory.CreateDirectory(expertDirectory);
        File.Move(Path.Combine(_dir, script), Path.Combine(expertDirectory, script));
        var expert = new ExpertDefinition("Execute", "text", "text", "", Kind: "exec", Command: python.Command,
            Args: [.. python.PrefixArgs, script], Inputs: ["source_file", "mode", "work_dir", "output_dir"], OutputKey: "result", ExpertDirectory: expertDirectory);
        var ast = MclParser.Parse("mission Root(invoice, mode) = { Child(invoice: invoice, mode: mode) }\nmission Child(invoice, mode) = { Execute(source_file: invoice) }");
        var workspace = new PipelineExecutionWorkspace(_dir, new Dictionary<string, string> { [relative] = "verified-digest" });
        var events = new List<PipelineTraceEvent>();
        var result = await new PipelineRunner(new StubExpertRunner((_, _) => new StepEnvelope("unused")))
            .RunAsync(ast, new() { ["Execute"] = expert }, new PipelineRunOptions("Root",
                new Dictionary<string, string> { ["invoice"] = relative, ["mode"] = "text" }, OnTrace: (fact, _) => { events.Add(fact); return Task.CompletedTask; })
            { ExecutionWorkspace = workspace });
        Assert.True(result.Status == MissionStatus.Pass, result.FailReason);
        var completed = Assert.Single(events.OfType<PipelineStepCompleted>());
        Assert.Equal("Root@1#0/Child@1#0", completed.StepKey);
        Assert.Equal("text", await File.ReadAllTextAsync(Path.Combine(workspace.GetStepOutputDirectory(completed.StepKey, 1), "proof.txt")));
    }

    [SkippableTheory]
    [InlineData("stdout", 4194305)]
    [InlineData("stderr", 65537)]
    public async Task Oversized_stream_fails_and_joins_without_waiting_for_timeout(string stream, int bytes)
    {
        var script = Script($"import sys,time\nsys.{stream}.write('x'*{bytes})\nsys.{stream}.flush()\ntime.sleep(30)\n");
        var started = Stopwatch.StartNew();
        var result = await new ExecExpertRunner().RunAsync(ExecExpert(script), []);
        Assert.Equal("fail", result.Status);
        Assert.Contains("exceeds", result.Reason);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10));
    }

    [SkippableFact]
    public async Task Duplex_pressure_and_stdin_budget_are_bounded()
    {
        var script = Script("import sys,json\nsys.stderr.write('x'*60000)\nsys.stderr.flush()\nsys.stdout.write(' '*1000000)\nsys.stdout.flush()\nd=json.load(sys.stdin)\nprint(json.dumps({'result':str(len(d['input']))}))\n");
        var expert = ExecExpert(script);
        var result = await new ExecExpertRunner().RunAsync(expert, new() { ["input"] = new string('a', 1_000_000) });
        Assert.Equal("1000000", result.Text);
        Assert.Equal("pass", result.Status);
        var exact = await new ExecExpertRunner().RunAsync(expert, new() { ["input"] = new string('a', 4 * 1024 * 1024 - 12) });
        Assert.Equal((4 * 1024 * 1024 - 12).ToString(), exact.Text);
        Assert.Equal("pass", exact.Status);
        var rejected = await new ExecExpertRunner().RunAsync(expert, new() { ["input"] = new string('a', 4 * 1024 * 1024) });
        Assert.Equal("fail", rejected.Status);
        Assert.Contains("stdin", rejected.Reason);
    }

    [SkippableFact]
    public async Task Exact_stdout_and_stderr_byte_limits_are_accepted()
    {
        var script = Script("import sys,json\njson.load(sys.stdin)\nsys.stderr.write('x'*65536)\nsys.stderr.flush()\njson.dump({'result':'x'*(4194304-len('{\"result\": \"\"}'))},sys.stdout)\n");
        var result = await new ExecExpertRunner().RunAsync(ExecExpert(script), []);
        Assert.Equal("pass", result.Status);
        Assert.Equal(4194304 - "{\"result\": \"\"}".Length, result.Text.Length);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancel_and_timeout_join_child_before_return(bool timeout)
    {
        var python = RequirePython();
        var sentinel = Path.Combine(_dir, "late.txt");
        var pid = Path.Combine(_dir, "child.pid");
        var script = Script("import subprocess,sys,time,pathlib\np=subprocess.Popen([sys.executable,'-c',\"import time,pathlib;time.sleep(2);pathlib.Path('late.txt').write_text('escaped')\"])\npathlib.Path('child.pid').write_text(str(p.pid))\ntime.sleep(30)\n");
        using var cancellation = new CancellationTokenSource();
        var task = new ExecExpertRunner().RunAsync(ExecExpert(script, timeout: timeout ? "1s" : "30s"), [], cancellation.Token);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(pid)) await Task.Delay(10, lifetime.Token);
        if (!timeout) cancellation.Cancel();
        if (timeout) Assert.Equal("fail", (await task.WaitAsync(lifetime.Token)).Status);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(lifetime.Token));
        await Task.Delay(2200, lifetime.Token);
        Assert.False(File.Exists(sentinel));
    }
    private static readonly Lazy<PythonInvocation?> WorkingPython = new(FindWorkingPython);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ExecExpertRunnerTests() => Directory.CreateDirectory(_dir);
    public void Dispose()          => Directory.Delete(_dir, recursive: true);

    private string Script(string content, string name = "script.py")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return name; // relative name — WorkingDirectory is _dir
    }

    private ExpertDefinition ExecExpert(
        string args,
        string inputs    = "input",
        string outputKey = "result",
        string timeout   = "")
    {
        var python = RequirePython();
        return new("TestExec", "Input", "Output", "",
            Kind: "exec", Command: python.Command, Args: [..python.PrefixArgs, args], Inputs: [inputs],
            OutputKey: outputKey, Timeout: timeout,
            ExpertDirectory: _dir);
    }

    [SkippableFact]
    public async Task RunAsync_ValidScript_WritesOutputKeyToContext()
    {
        var script = Script("import sys,json\nd=json.load(sys.stdin)\nprint(json.dumps({'result':d['input']}))\n");
        var runner  = new ExecExpertRunner();
        var expert  = ExecExpert(script);
        var context = new Dictionary<string, object> { ["input"] = "hello" };

        var envelope = await runner.RunAsync(expert, context);

        Assert.Equal("pass",  envelope.Status);
        Assert.Equal("hello", envelope.Text);
        Assert.Equal("hello", context["result"]);
        Assert.Equal("hello", context["output"]);
    }

    [SkippableFact]
    public async Task RunAsync_NonZeroExit_ReturnsFailEnvelope()
    {
        var script  = Script("import sys\nsys.exit(1)\n");
        var runner  = new ExecExpertRunner();
        var expert  = ExecExpert(script);
        var context = new Dictionary<string, object> { ["input"] = "x" };

        var envelope = await runner.RunAsync(expert, context);

        Assert.Equal("fail", envelope.Status);
        Assert.Contains("exited with code 1", envelope.Reason);
    }

    [SkippableFact]
    public async Task RunAsync_Timeout_ReturnsFailEnvelope()
    {
        var script  = Script("import time\ntime.sleep(10)\n");
        var runner  = new ExecExpertRunner();
        var expert  = ExecExpert(script, timeout: "1s");
        var context = new Dictionary<string, object> { ["input"] = "x" };

        var envelope = await runner.RunAsync(expert, context);

        Assert.Equal("fail", envelope.Status);
        Assert.Contains("timed out", envelope.Reason);
    }

    [SkippableFact]
    public async Task RunAsync_MissingOutputKey_ThrowsExpertLoadException()
    {
        var script  = Script("import json,sys\njson.load(sys.stdin)\nprint(json.dumps({'wrong_key':1}))\n");
        var runner  = new ExecExpertRunner();
        var expert  = ExecExpert(script);
        var context = new Dictionary<string, object> { ["input"] = "x" };

        await Assert.ThrowsAsync<ExpertLoadException>(() => runner.RunAsync(expert, context));
    }

    [SkippableFact]
    public async Task RunAsync_InvalidJson_ThrowsExpertLoadException()
    {
        var script  = Script("import sys\nsys.stdin.read()\nprint('not json')\n");
        var runner  = new ExecExpertRunner();
        var expert  = ExecExpert(script);
        var context = new Dictionary<string, object> { ["input"] = "x" };

        await Assert.ThrowsAsync<ExpertLoadException>(() => runner.RunAsync(expert, context));
    }

    [SkippableFact]
    public async Task RunAsync_MultipleInputKeys_AllSerializedToStdin()
    {
        var python = RequirePython();
        var script = Script(
            "import sys,json\n" +
            "d=json.load(sys.stdin)\n" +
            "print(json.dumps({'result': d['a'] + ' ' + d['b']}))\n");
        var runner  = new ExecExpertRunner();
        var expert  = new ExpertDefinition("TestExec", "Input", "Output", "",
            Kind: "exec", Command: python.Command, Args: [..python.PrefixArgs, script], Inputs: ["a", "b"],
            OutputKey: "result", ExpertDirectory: _dir);
        var context = new Dictionary<string, object> { ["a"] = "hello", ["b"] = "world" };

        var envelope = await runner.RunAsync(expert, context);

        Assert.Equal("pass",        envelope.Status);
        Assert.Equal("hello world", envelope.Text);
    }

    [SkippableFact]
    public async Task RunAsync_CommandPlusArgs_DockerStyle()
    {
        var python = RequirePython();
        // command: python, args: script.py - mirrors docker/k8s command+args pattern
        var script  = Script("import sys,json\nd=json.load(sys.stdin)\nprint(json.dumps({'result':'ok'}))\n");
        var runner  = new ExecExpertRunner();
        var expert  = new ExpertDefinition("TestExec", "Input", "Output", "",
            Kind: "exec", Command: python.Command, Args: [..python.PrefixArgs, script],
            Inputs: ["input"], OutputKey: "result", ExpertDirectory: _dir);
        var context = new Dictionary<string, object> { ["input"] = "x" };

        var envelope = await runner.RunAsync(expert, context);

        Assert.Equal("pass", envelope.Status);
        Assert.Equal("ok",   envelope.Text);
    }

    [SkippableFact]
    public async Task RunAsync_ForwardsForgeEnvironmentVariables()
    {
        var outputDir = Path.Combine(_dir, "outputs");
        Directory.CreateDirectory(outputDir);
        var script = Script(
            "import os,json\n" +
            "out=os.environ['FORGE_OUTPUT_DIR']\n" +
            "open(os.path.join(out, 'proof.txt'), 'w').write('artifact proof')\n" +
            "print(json.dumps({'result':'wrote proof'}))\n");
        var runner  = new ExecExpertRunner();
        var expert  = ExecExpert(script);
        var context = new Dictionary<string, object>
        {
            ["input"] = "x",
            ["FORGE_OUTPUT_DIR"] = outputDir,
        };

        var envelope = await runner.RunAsync(expert, context);

        Assert.Equal("pass", envelope.Status);
        Assert.Equal("wrote proof", envelope.Text);
        Assert.Equal("artifact proof", File.ReadAllText(Path.Combine(outputDir, "proof.txt")));
    }

    private static PythonInvocation RequirePython()
    {
        var python = WorkingPython.Value;
        Skip.If(python is null, "No working Python 3 interpreter found (python3/python/py -3 probes failed)");
        return python!;
    }

    private static PythonInvocation? FindWorkingPython()
    {
        PythonInvocation[] candidates =
        [
            new("python3", []),
            new("python", []),
            new("py", ["-3"]),
        ];

        return candidates.FirstOrDefault(IsWorkingPython);
    }

    private static bool IsWorkingPython(PythonInvocation candidate)
    {
        const string probe = "import json,sys; print(json.dumps({'major': sys.version_info[0]}))";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(candidate.Command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            }
        };

        foreach (var arg in candidate.PrefixArgs)
            process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(probe);

        try
        {
            process.Start();
        }
        catch
        {
            return false;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(3_000))
        {
            process.Kill(entireProcessTree: true);
            return false;
        }

        _ = stderrTask.Result;
        var stdout = stdoutTask.Result;
        if (process.ExitCode != 0)
            return false;

        try
        {
            using var json = JsonDocument.Parse(stdout);
            return json.RootElement.TryGetProperty("major", out var major)
                && major.GetInt32() >= 3;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record PythonInvocation(string Command, IReadOnlyList<string> PrefixArgs);
}
