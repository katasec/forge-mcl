using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using ForgeMission.Core.Adapters;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Runtime;
using ForgeMission.Exec.Probe;
using ForgeMission.Parser;

if (args.FirstOrDefault() == "--child") return await ExecProbeChild.RunAsync(args[1..]);
var verifyInit = args.FirstOrDefault() == "--verify-init";
var directory = Path.Combine(Path.GetTempPath(), "forge-exec-proof-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    if (args.FirstOrDefault() == "--verify-pid1-refusal") return await VerifyPid1RefusalAsync(directory);
    if (verifyInit) await VerifyInitTopologyAsync();
    await VerifyPipeCancellationAsync(PipeDirection.In);
    await VerifyPipeCancellationAsync(PipeDirection.Out);
    await VerifyDeclinedInputAsync();
    await VerifyDuplexPressureAsync(directory);
    await VerifyWorkspaceAsync(directory);
    await VerifyArgumentsAsync(directory);
    await VerifyCommandLookupAsync(directory);
    await VerifyExitedDescendantAsync(directory);
    await VerifyDescendantsAsync(directory, false, verifyInit);
    await VerifyDescendantsAsync(directory, true, verifyInit);
    Console.WriteLine($"PASS native exec lifecycle; pid={Environment.ProcessId}; initHosted={verifyInit}; {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
    return 0;
}
finally { Directory.Delete(directory, true); }

static ExpertDefinition Expert(string directory, string mode, string timeout, params string[] arguments) =>
    new("Execute", "text", "text", "", Kind: "exec", Command: Environment.ProcessPath!,
        Args: ["--child", mode, .. arguments], Inputs: ["input"], OutputKey: "result", Timeout: timeout, ExpertDirectory: directory);

static async Task VerifyPipeCancellationAsync(PipeDirection direction)
{
    using var pipe = new AnonymousPipeServerStream(direction,
        OperatingSystem.IsWindows() ? HandleInheritability.Inheritable : HandleInheritability.None);
    using var peer = new AnonymousPipeClientStream(
        direction == PipeDirection.In ? PipeDirection.Out : PipeDirection.In, pipe.ClientSafePipeHandle);
    using var cancellation = new CancellationTokenSource();
    var operation = direction == PipeDirection.In
        ? pipe.ReadAsync(new byte[1], cancellation.Token).AsTask()
        : pipe.WriteAsync(new byte[4 * 1024 * 1024], cancellation.Token).AsTask();
    try
    {
        await Task.Delay(100);
        if (operation.IsCompleted) throw new Exception($"Expected blocked anonymous-pipe {direction} operation.");
        var elapsed = Stopwatch.StartNew();
        cancellation.Cancel();
        try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        if (!operation.IsCanceled) throw new Exception($"Anonymous-pipe {direction} did not complete with cancellation.");
        Console.WriteLine($"PASS blocked anonymous-pipe {(direction == PipeDirection.In ? "read" : "write")} cancelled before peer closure; {elapsed.ElapsedMilliseconds}ms");
    }
    finally
    {
        cancellation.Cancel();
        peer.Dispose();
        try { await operation; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (IOException) when (!peer.IsConnected) { }
    }
}

static async Task<int> VerifyPid1RefusalAsync(string directory)
{
    if (!OperatingSystem.IsLinux() || Environment.ProcessId != 1) throw new Exception("Expected actual bare Linux PID 1.");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var result = await new ExecExpertRunner().RunAsync(Expert(directory, "parent", "5s", directory), [], deadline.Token);
    if (result.Status != "fail" || result.Reason?.Contains("Linux PID 1 is unsupported.") != true)
        throw new Exception($"Expected prelaunch PID1 refusal: {result.Reason}");
    if (Directory.GetFileSystemEntries(directory).Length != 0) throw new Exception("PID1 refusal launched a child.");
    Console.WriteLine("PASS bare Linux PID1 refused before child/root/sentinel creation");
    return 0;
}

static async Task VerifyInitTopologyAsync()
{
    if (!OperatingSystem.IsLinux() || Environment.ProcessId == 1) throw new Exception("Expected init-hosted Linux probe.");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (true)
    {
        if (File.ReadAllText("/proc/1/comm").Trim() != "tini") throw new Exception("Expected published image tini PID1.");
        var children = File.ReadAllText("/proc/1/task/1/children").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var child in children)
        {
            if (!IsRunnerChild(child)) continue;
            Console.WriteLine($"PASS published-image topology: tini PID1; direct dotnet Runner PID{child}; native probe PID{Environment.ProcessId}");
            return;
        }
        await Task.Delay(10, deadline.Token);
    }
}

static bool IsRunnerChild(string pid)
{
    try
    {
        if (File.ReadAllText($"/proc/{pid}/comm").Trim() != "dotnet") return false;
        var command = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return command.Take(2).SequenceEqual(new[] { "dotnet", "ForgeMission.Runner.dll" });
    }
    catch (DirectoryNotFoundException) { return false; }
    catch (FileNotFoundException) { return false; }
}

static async Task VerifyDeclinedInputAsync()
{
    var expert = Expert(Directory.GetCurrentDirectory(), "decline", "5s");
    var result = await new ExecExpertRunner().RunAsync(expert, new() { ["input"] = new string('x', 1_000_000) });
    if (result.Status != "pass" || result.Text != "declined") throw new Exception($"Declined stdin failed: {result.Reason}");
    var options = ExecProbeChild.Command("decline");
    options.RedirectStandardInput = options.RedirectStandardOutput = options.RedirectStandardError = true;
    using var child = Process.Start(options)!;
    await child.StandardOutput.ReadToEndAsync();
    try { await child.StandardInput.BaseStream.WriteAsync(new byte[1_000_000]); throw new Exception("Expected closed-input observation."); }
    catch (IOException exception)
    {
        Console.WriteLine($"closed stdin HRESULT={exception.HResult:X8}; inner={exception.InnerException?.GetType().Name}; native={(exception.InnerException as System.Net.Sockets.SocketException)?.NativeErrorCode}");
    }
    child.StandardInput.BaseStream.Dispose();
    await child.StandardError.ReadToEndAsync();
    await child.WaitForExitAsync();
}

static async Task VerifyArgumentsAsync(string directory)
{
    string[] arguments = ["", "a b", "a\"b", "x\\", "x\\\" y", "$HOME", ";"];
    var result = await new ExecExpertRunner().RunAsync(Expert(directory, "args", "5s", arguments), []);
    using var parsed = JsonDocument.Parse(result.Text);
    if (!parsed.RootElement.EnumerateArray().Select(item => item.GetString()).SequenceEqual(arguments)) throw new Exception("Literal argv mismatch.");
    var working = Path.Combine(directory, "cwd with spaces");
    Directory.CreateDirectory(working);
    result = await new ExecExpertRunner().RunAsync(Expert(working, "cwd", "5s"), []);
    AssertWorkingDirectory(result, working);
    Console.WriteLine("PASS literal argv and expert cwd");
}

static async Task VerifyDuplexPressureAsync(string directory)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var result = await new ExecExpertRunner().RunAsync(Expert(directory, "pressure", "5s"),
        new() { ["input"] = new string('i', 2_000_000) }, deadline.Token);
    if (result.Status != "pass" || result.Text.Length != 1_000_000 || result.Text.Any(value => value != 'o'))
        throw new Exception($"Concurrent stdin/stdout/stderr pressure failed: {result.Reason}");
    Console.WriteLine("PASS concurrent stdin/stdout/stderr pressure with verified JSON result under caps");
}

static async Task VerifyWorkspaceAsync(string directory)
{
    var root = Path.Combine(directory, "workspace");
    var working = Path.Combine(root, "expert with spaces");
    const string relative = "inputs/invoice/content";
    Directory.CreateDirectory(working);
    Directory.CreateDirectory(Path.Combine(root, "inputs", "invoice"));
    File.WriteAllText(Path.Combine(root, relative), "verified content");
    File.WriteAllText(Path.Combine(working, "cwd-marker"), "expert cwd");
    var workspace = new PipelineExecutionWorkspace(root, new Dictionary<string, string> { [relative] = "verified-digest" });
    var vars = new Dictionary<string, string> { ["invoice"] = relative, ["mode"] = "authored mode" };
    var authored = new Dictionary<string, object> { ["FORGE_PROBE_AUTHORED"] = "authored", ["FORGE_WORK_DIR"] = "authored stale runtime" };
    var inherited = new Dictionary<string, string> { ["FORGE_PROBE_INHERITED"] = "inherited", ["FORGE_PROBE_AUTHORED"] = "inherited overridden", ["FORGE_INPUT_stale"] = "stale", ["FORGE_SOURCE_FILE"] = "stale" };
    var saved = inherited.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
    try
    {
        foreach (var pair in inherited) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        var expert = Expert(working, "workspace", "5s") with { Inputs = ["source_file", "document", "mode", "work_dir", "input_dir", "output_dir"] };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await new PipelineRunner(new ExecExpertRunner()).RunAsync(
            MclParser.Parse("mission Root(invoice, mode) = { Execute(source_file: invoice, document: invoice) }"),
            new() { ["Execute"] = expert }, new PipelineRunOptions("Root", vars, ContextObjects: authored) { ExecutionWorkspace = workspace }, deadline.Token);
        if (result.Status != MissionStatus.Pass) throw new Exception($"Workspace execution failed: {result.FailReason}");
        AssertWorkspaceResult(result.Text, workspace, relative);
        if (vars["invoice"] != relative || (string)authored["FORGE_WORK_DIR"] != "authored stale runtime")
            throw new Exception("Process-local workspace mapping changed caller input.");
    }
    finally { foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
    Console.WriteLine("PASS native PipelineRunner workspace/input/runtime aliases and inherited/authored FORGE environment; source remains relative");
}

static void AssertWorkspaceResult(string text, PipelineExecutionWorkspace workspace, string relative)
{
    var result = JsonSerializer.Deserialize(text, ProbeJsonContext.Default.DictionaryStringString)!;
    var absolute = Path.Combine(workspace.RootDirectory, relative);
    var output = workspace.GetStepOutputDirectory("Root@1#0", 1);
    var expected = new Dictionary<string, string> {
        ["source_file"] = absolute, ["document"] = absolute, ["mode"] = "authored mode",
        ["work_dir"] = workspace.RootDirectory, ["input_dir"] = Path.Combine(workspace.RootDirectory, "inputs"), ["output_dir"] = output,
        ["FORGE_INPUT_source_file"] = absolute, ["FORGE_INPUT_document"] = absolute, ["FORGE_SOURCE_FILE"] = absolute,
        ["FORGE_WORK_DIR"] = workspace.RootDirectory, ["FORGE_INPUT_DIR"] = Path.Combine(workspace.RootDirectory, "inputs"), ["FORGE_OUTPUT_DIR"] = output,
        ["FORGE_INPUT_stale"] = "", ["FORGE_INPUT_mode"] = "", ["FORGE_PROBE_INHERITED"] = "inherited", ["FORGE_PROBE_AUTHORED"] = "authored",
        ["content"] = "verified content", ["cwd_marker"] = "expert cwd" };
    foreach (var pair in expected)
        if (!result.TryGetValue(pair.Key, out var actual) || actual != pair.Value) throw new Exception($"Workspace binding mismatch: {pair.Key}.");
    if (File.ReadAllText(Path.Combine(output, "probe.txt")) != "verified content") throw new Exception("Runtime output directory mismatch.");
}

static async Task VerifyDescendantsAsync(string directory, bool cancel, bool verifyInit)
{
    var home = Path.Combine(directory, cancel ? "cancel" : "timeout");
    Directory.CreateDirectory(home);
    var release = Path.Combine(home, "release-unrelated");
    using var unrelated = Process.Start(ExecProbeChild.Command("unrelated", release))!;
    using var cancellation = new CancellationTokenSource();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try
    {
        var run = new ExecExpertRunner().RunAsync(Expert(home, "parent", cancel ? "30s" : "1s", home), [], cancellation.Token);
        while (Directory.GetFiles(home, "child-*.pid").Length != 2) await Task.Delay(10, deadline.Token);
        if (cancel) cancellation.Cancel();
        try
        {
            var result = await run.WaitAsync(deadline.Token);
            if (cancel || result.Status != "fail" || !result.Reason!.Contains("timed out")) throw new Exception($"Expected timeout: {result.Reason}");
        }
        catch (OperationCanceledException) when (cancel && !deadline.IsCancellationRequested) { }
        if (unrelated.HasExited) throw new Exception("Cleanup terminated an unrelated child.");
        if (verifyInit) await AssertInitReapedAsync(home);
        await Task.Delay(2400, deadline.Token);
        if (Directory.GetFiles(home, "sentinel-*").Length != 0) throw new Exception("Descendant continued after return.");
        Console.WriteLine($"PASS root-exits-first descendants; callerCancel={cancel}; initReap={verifyInit}");
    }
    finally
    {
        File.WriteAllText(release, "exit");
        await unrelated.WaitForExitAsync(deadline.Token);
        if (unrelated.ExitCode != 37) throw new Exception("Unrelated .NET child exit ownership was lost.");
    }
}

static async Task VerifyCommandLookupAsync(string directory)
{
    var expert = Expert(directory, "cwd", "5s") with
    { Command = Path.GetRelativePath(Directory.GetCurrentDirectory(), Environment.ProcessPath!) };
    var result = await new ExecExpertRunner().RunAsync(expert, []);
    AssertWorkingDirectory(result, directory);
    var search = Path.Combine(directory, "path search");
    Directory.CreateDirectory(search);
    foreach (var file in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(search, Path.GetFileName(file)));
    var name = "literal probe " + Guid.NewGuid().ToString("N") + (OperatingSystem.IsWindows() ? ".exe" : "");
    File.Copy(Environment.ProcessPath!, Path.Combine(search, name));
    var saved = Environment.GetEnvironmentVariable("PATH");
    try
    {
        Environment.SetEnvironmentVariable("PATH", search + Path.PathSeparator + saved);
        result = await new ExecExpertRunner().RunAsync(expert with { Command = name }, []);
        AssertWorkingDirectory(result, directory);
    }
    finally { Environment.SetEnvironmentVariable("PATH", saved); }
    Console.WriteLine("PASS relative/PATH/spaced executable lookup");
}

static async Task VerifyExitedDescendantAsync(string directory)
{
    // The exited child inherits stderr, but redirect its JSON so the expert envelope stays singular.
    var result = await new ExecExpertRunner().RunAsync(Expert(directory, "exited-descendant", "5s"), []);
    if (result.Status != "pass" || result.Text != "descendant exited")
        throw new Exception($"Already-exited descendant cleanup failed: {result.Reason}");
    Console.WriteLine("PASS already-exited descendant group cleanup");
}

static void AssertWorkingDirectory(ForgeMission.Core.Runtime.StepEnvelope result, string expected)
{
    if (result.Status != "pass") throw new Exception($"Expert cwd failed: {result.Reason}");
    var name = Guid.NewGuid().ToString("N");
    var proof = Path.Combine(expected, name);
    File.WriteAllText(proof, "same directory");
    try
    {
        if (File.ReadAllText(Path.Combine(result.Text, name)) != "same directory") throw new Exception("Expert cwd identity mismatch.");
    }
    finally { File.Delete(proof); }
}

static async Task AssertInitReapedAsync(string directory)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var pids = Directory.GetFiles(directory, "*.pid").Select(File.ReadAllText).ToArray();
    while (pids.Any(pid => Directory.Exists("/proc/" + pid)))
    {
        await Task.Delay(10, deadline.Token);
    }
    Console.WriteLine($"PASS bounded process-entry disappearance under init: {string.Join(',', pids)}");
}
