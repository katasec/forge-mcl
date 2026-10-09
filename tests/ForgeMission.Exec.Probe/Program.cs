using System.Diagnostics;
using System.Text.Json;
using ForgeMission.Core.Adapters;
using ForgeMission.Core.Experts;
using ForgeMission.Exec.Probe;

if (args.FirstOrDefault() == "--child") return await ExecProbeChild.RunAsync(args[1..]);
var pid1 = args.FirstOrDefault() == "--verify-pid1";
if (pid1 && (!OperatingSystem.IsLinux() || Environment.ProcessId != 1)) throw new Exception("Expected actual Linux PID 1.");
var directory = Path.Combine(Path.GetTempPath(), "forge-exec-proof-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    await VerifyDeclinedInputAsync();
    await VerifyArgumentsAsync(directory);
    await VerifyCommandLookupAsync(directory);
    await VerifyExitedDescendantAsync(directory);
    await VerifyDescendantsAsync(directory, false, pid1);
    await VerifyDescendantsAsync(directory, true, pid1);
    Console.WriteLine($"PASS native exec lifecycle; pid={Environment.ProcessId}; PID1={pid1}; {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
    return 0;
}
finally { Directory.Delete(directory, true); }

static ExpertDefinition Expert(string directory, string mode, string timeout, params string[] arguments) =>
    new("Execute", "text", "text", "", Kind: "exec", Command: Environment.ProcessPath!,
        Args: ["--child", mode, .. arguments], Inputs: ["input"], OutputKey: "result", Timeout: timeout, ExpertDirectory: directory);

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

static async Task VerifyDescendantsAsync(string directory, bool cancel, bool pid1)
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
        if (pid1) AssertReaped(home);
        await Task.Delay(2400, deadline.Token);
        if (Directory.GetFiles(home, "sentinel-*").Length != 0) throw new Exception("Descendant continued after return.");
        Console.WriteLine($"PASS root-exits-first descendants; callerCancel={cancel}; adoptedReap={pid1}");
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

static void AssertReaped(string directory)
{
    foreach (var file in Directory.GetFiles(directory, "*.pid"))
    {
        var pid = File.ReadAllText(file);
        if (Directory.Exists("/proc/" + pid)) throw new Exception($"Owned process entry {pid} remains after PID1 cleanup.");
    }
}
