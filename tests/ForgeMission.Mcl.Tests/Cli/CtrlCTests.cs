using System.Text.RegularExpressions;
using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// Ctrl+C on a command waiting for a hosted call that never answers ends it with the Ctrl+C exit
// code, 130, and nothing more on stdout than it printed before the call ({home}: the isolated HOME).
public sealed class CtrlCTests
{
    [SkippableTheory]
    [InlineData("^$", "exec", "Noop", "hello")]
    [InlineData("^$", "project", "create")]
    [InlineData("^Project: .*/{home}\n$", "chat")]
    [InlineData("^$", "whoami")]
    public async Task Ctrl_C_during_a_hosted_call_exits_130(string stdout, params string[] args)
    {
        Skip.If(OperatingSystem.IsWindows(), "SIGINT is POSIX.");
        await using var harness = ForgeCliHarness.Start();
        var release = new TaskCompletionSource();
        var stub = await harness.StartStubAsync(_ =>
        {
            release.Task.Wait();
            return new StubResponse(500, "released");
        });
        harness.SignIn(stub.Address.ToString());
        harness.WriteConfig($$"""{ "api": { "endpoint": "{{stub.Address}}" } }""");
        File.WriteAllText(Path.Combine(harness.WorkingDirectory, "forge.project.json"),
            "{\"projectId\":\"" + Guid.NewGuid() + "\",\"missions\":[\"Chat@1\"],\"folders\":[]}");

        CliResult result;
        try { result = await harness.InterruptWhileAsync(_ => UntilAsync(() => stub.Requests.Count > 0), args); }
        finally { release.SetResult(); }

        Assert.Equal(130, result.ExitCode);
        Assert.Matches(stdout.Replace("{home}", Regex.Escape(Path.GetFileName(harness.Home))),
            result.StandardOutput.ReplaceLineEndings("\n"));
    }

    // Ctrl+C while exec reads a mission's progress stream (headers and a first line sent, the stream
    // still open) ends it with 130 and nothing on stdout.
    [SkippableFact]
    public async Task Ctrl_C_while_exec_reads_the_progress_stream_exits_130()
    {
        Skip.If(OperatingSystem.IsWindows(), "SIGINT is POSIX.");
        await using var harness = ForgeCliHarness.Start();
        var release = new TaskCompletionSource();
        var stub = await harness.StartStubAsync(_ =>
            new StubResponse(200, "{\"type\":\"progress\"}\n", "application/x-ndjson", release.Task));
        harness.SignIn(stub.Address.ToString());
        harness.WriteConfig($$"""{ "api": { "endpoint": "{{stub.Address}}" } }""");

        CliResult result;
        try
        {
            // The pause lets the response headers and first line reach exec before the interrupt.
            result = await harness.InterruptWhileAsync(async _ =>
            {
                await UntilAsync(() => stub.Requests.Count > 0);
                await Task.Delay(500);
            }, "exec", "Noop", "hello");
        }
        finally { release.SetResult(); }

        Assert.Equal(130, result.ExitCode);
        Assert.Equal("", result.StandardOutput);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        while (!condition()) await Task.Delay(50);
    }
}
