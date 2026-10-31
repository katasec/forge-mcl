using System.Text.RegularExpressions;
using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// Ctrl+C on a command waiting for a hosted call that never answers ends it with the Ctrl+C exit
// code, 130, and nothing more on stdout than it printed before the call ({home}: the isolated HOME).
public sealed class CtrlCTests
{
    [SkippableTheory]
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

    private static async Task UntilAsync(Func<bool> condition)
    {
        while (!condition()) await Task.Delay(50);
    }
}
