using System.Diagnostics;
using System.Reflection;

namespace ForgeMission.Tests.Cli;

// Controlled CLI boundaries only: no Ghostty, LaunchServices application launch or OS input.
public sealed class MacChatWindowTests
{
    private static readonly Func<string, string?, string> Encode = Method("EncodeContext").CreateDelegate<Func<string, string?, string>>();
    private static readonly Func<string, (string WorkingDirectory, string? ApiEndpoint)> Decode =
        Method("DecodeContext").CreateDelegate<Func<string, (string WorkingDirectory, string? ApiEndpoint)>>();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.invalid/雪/'\"\\$`  ")]
    public void Context_preserves_original_directory_and_absent_empty_or_exact_endpoint(string? endpoint)
    {
        var cwd = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "雪 'quotes\" \\ $`  "));
        var marker = Encode(cwd, endpoint);

        Assert.Equal((cwd, endpoint), Decode(marker));
        Assert.Equal(endpoint is null ? "-" : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(endpoint)), marker.Split(':')[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("abc:-:extra")]
    [InlineData("?:-")]
    [InlineData("/w==:-")]
    [InlineData(":-")]
    [InlineData("cmVsYXRpdmU=:-")]
    [InlineData("L3RtcA==:?")]
    [InlineData("L3RtcA==:/w==")]
    public void Malformed_context_is_rejected_without_echoing_the_marker(string marker)
    {
        var error = Assert.Throws<FormatException>(() => Decode(marker));
        Assert.Equal("Invalid Forge chat window context.", error.Message);
    }

    [Theory]
    [MemberData(nameof(AdmissionCases))]
    public void Only_an_unmarked_interactive_Mac_launches(bool isMac, bool interactive, string? marker, bool launch)
    {
        var shouldLaunch = Method("ShouldLaunch").CreateDelegate<Func<bool, bool, string?, bool>>();
        var childMarker = Method("ChildMarker").CreateDelegate<Func<bool, bool, string?, string?>>();

        Assert.Equal(launch, shouldLaunch(isMac, interactive, marker));
        Assert.Equal(isMac && interactive ? marker : null, childMarker(isMac, interactive, marker));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.invalid/雪  ")]
    public async Task Child_restores_context_before_same_chat_callback_and_success_never_waits(string? endpoint)
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var events = new List<string>();
        var error = new StringWriter();
        var code = await RunCommand(() =>
        {
            events.Add("chat");
            return Task.FromResult(0);
        }, Encode(cwd, endpoint), context => Restore(context,
            value => { Assert.Equal(cwd, value); events.Add("cwd"); },
            (name, value) => { Assert.Equal("FORGE_API_ENDPOINT", name); Assert.Equal(endpoint, value); events.Add("endpoint"); }),
            () => events.Add("dismiss"), error);

        Assert.Equal(0, code);
        Assert.Equal(["cwd", "endpoint", "chat"], events);
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_context_or_inaccessible_directory_prevents_chat_and_holds_error(bool inaccessible)
    {
        var events = new List<string>();
        var error = new StringWriter();
        var marker = inaccessible ? Encode(Path.GetFullPath(Path.GetTempPath()), null) : "invalid";
        var code = await RunCommand(() => { events.Add("chat"); return Task.FromResult(0); }, marker,
            context => Restore(context, value => throw new DirectoryNotFoundException("Directory is inaccessible."),
                (name, value) => events.Add("endpoint")), () => events.Add("dismiss"), error);

        Assert.Equal(1, code);
        Assert.Equal(["dismiss"], events);
        Assert.Contains(inaccessible ? "Directory is inaccessible." : "Invalid Forge chat window context.", error.ToString());
        Assert.Contains("Press any key to close this window.", error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Child_failure_waits_once_after_callback_cleanup(bool throws)
    {
        var events = new List<string>();
        var error = new StringWriter();
        var code = await RunCommand(async () =>
        {
            try
            {
                await Task.Yield();
                if (throws) throw new IOException("Controlled chat failure.");
                error.WriteLine("Controlled startup failure.");
                return 7;
            }
            finally { events.Add("cleanup"); }
        }, Encode(Path.GetFullPath(Path.GetTempPath()), null), context => events.Add("restore"),
            () => events.Add("dismiss"), error);

        Assert.Equal(1, code);
        Assert.Equal(["restore", "cleanup", "dismiss"], events);
        Assert.Contains(throws ? "Controlled chat failure." : "Controlled startup failure.", error.ToString());
    }

    [Fact]
    public async Task Unmarked_command_keeps_original_result_and_exception_without_restoration_or_dismissal()
    {
        var error = new StringWriter();
        void Unexpected() => throw new InvalidOperationException("Unexpected child boundary.");
        Assert.Equal(7, await RunCommand(() => Task.FromResult(7), null, context => Unexpected(), Unexpected, error));
        var failure = new IOException("Original failure.");
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => RunCommand(() => Task.FromException<int>(failure),
            null, context => Unexpected(), Unexpected, error)));
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Request_uses_only_locked_process_scoped_overrides_and_separate_project_and_cwd(bool hands)
    {
        var cwd = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "original '雪\" \\ $`  "));
        var projectFile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "other project", "forge.project.json"));
        var start = Build("/exact/native forge", hands, projectFile, cwd, "https://example.invalid/  ");
        var initial = start.ArgumentList.Single(arg => arg.StartsWith("--initial-command=", StringComparison.Ordinal));
        var command = start.ArgumentList.Single(arg => arg.StartsWith("--command=", StringComparison.Ordinal));

        Assert.Equal("/usr/bin/open", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Equal(["-n", "-b", "com.mitchellh.ghostty", "--args", "--config-file=", initial, command,
            "--keybind=cmd+a=text:\\x01", "--working-directory=\"" + cwd + "\"",
            "--env=FORGE_CHAT_WINDOW=" + Encode(cwd, "https://example.invalid/  "), "--input=",
            "--shell-integration=none", "--initial-window=true", "--wait-after-command=false",
            "--quit-after-last-window-closed=true"], start.ArgumentList);
        Assert.Equal(initial["--initial-command=".Length..], command["--command=".Length..]);
        Assert.StartsWith("--command=shell:'", command);
        Assert.DoesNotContain("shell:exec ", command);
        Assert.Equal(hands, command.Contains("'--hands'", StringComparison.Ordinal));
        Assert.Contains("'--project'", command);
        Assert.Contains("'" + projectFile + "'", command);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(-1)]
    public async Task Launch_reports_request_acceptance_rejection_or_start_failure(int outcome)
    {
        var executable = typeof(object).Assembly.Location;
        var output = new StringWriter();
        var error = new StringWriter();
        var calls = 0;
        var code = await Launch(executable, false, Path.GetTempPath(), Path.GetTempPath(), null, output, error, start =>
        {
            calls++;
            Assert.Equal("/usr/bin/open", start.FileName);
            return outcome == -1 ? Task.FromException<(int, string, string)>(new IOException("Controlled process start failure.")) :
                Task.FromResult((outcome, "controlled stdout", "controlled stderr"));
        });

        Assert.Equal(1, calls);
        Assert.Equal(outcome == 0 ? 0 : 1, code);
        Assert.Equal(outcome == 0 ? "Opened Forge chat in a new window." + Environment.NewLine : "", output.ToString());
        Assert.Equal(outcome == 0, error.ToString().Length == 0);
        if (outcome != 0) Assert.Contains(outcome == -1 ? "Controlled process start failure." : "open exited 7", error.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/missing/native/forge")]
    public async Task Missing_native_executable_does_not_start_a_process(string? executable)
    {
        var error = new StringWriter();
        Assert.Equal(1, await Launch(executable, false, Path.GetTempPath(), Path.GetTempPath(), null, new StringWriter(), error,
            start => throw new InvalidOperationException("Must not start.")));
        Assert.Contains("could not find its native executable", error.ToString());
    }

    [Fact]
    public async Task Dotnet_host_is_rejected_before_launch()
    {
        var home = NewTemporaryDirectory();
        var executable = Path.Combine(home, "dotnet");
        try
        {
            await File.WriteAllTextAsync(executable, "controlled non-executable fixture");
            var error = new StringWriter();
            Assert.Equal(1, await Launch(executable, true, home, home, null, new StringWriter(), error,
                start => throw new InvalidOperationException("Must not start.")));
            Assert.Contains("installed native forge executable, not dotnet", error.ToString());
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task Real_controlled_process_runner_drains_both_streams_and_preserves_exit_code(int exitCode)
    {
        Skip.If(OperatingSystem.IsWindows(), "Controlled native shell probe requires POSIX.");
        var start = Shell("i=0; while [ $i -lt 2000 ]; do printf 'out\\n'; printf 'err\\n' >&2; i=$((i+1)); done; exit " + exitCode);
        var result = await StartProcess(start).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(string.Concat(Enumerable.Repeat("out\n", 2000)), result.StandardOutput);
        Assert.Equal(string.Concat(Enumerable.Repeat("err\n", 2000)), result.StandardError);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_composed_commands_roundtrip_literal_adversarial_executable_and_argv(bool hands)
    {
        Skip.If(OperatingSystem.IsWindows(), "Controlled native shell probe requires POSIX.");
        var home = NewTemporaryDirectory();
        var executable = Path.Combine(home, "forge 雪 ' \" \\ $ ` executable");
        var project = Path.Combine(home, "project 雪 ' \" \\ $ `", "forge.project.json");
        try
        {
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nprintf '%s\\0' \"$0\" \"$@\"\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var start = Build(executable, hands, project, home, null);
            var expected = new List<string> { executable, "chat" };
            if (hands) expected.Add("--hands");
            expected.AddRange(["--project", project]);
            foreach (var prefix in new[] { "--initial-command=shell:", "--command=shell:" })
            {
                var command = start.ArgumentList.Single(arg => arg.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];
                var result = await StartProcess(Shell(command)).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(0, result.ExitCode);
                Assert.Equal("", result.StandardError);
                Assert.Equal(expected, result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries));
            }
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    public static IEnumerable<object?[]> AdmissionCases()
    {
        foreach (var isMac in new[] { false, true })
        foreach (var interactive in new[] { false, true })
        foreach (var marker in new string?[] { null, "", "context" })
            yield return [isMac, interactive, marker, isMac && interactive && marker is null];
    }

    private static Task<int> RunCommand(Func<Task<int>> run, string? marker,
        Action<(string WorkingDirectory, string? ApiEndpoint)> restore, Action dismiss, TextWriter error) =>
        Method("RunCommandAsync").CreateDelegate<Func<Func<Task<int>>, string?,
            Action<(string WorkingDirectory, string? ApiEndpoint)>, Action, TextWriter, Task<int>>>()(run, marker, restore, dismiss, error);

    private static void Restore((string WorkingDirectory, string? ApiEndpoint) context,
        Action<string> directory, Action<string, string?> environment) =>
        Method("RestoreContext").CreateDelegate<Action<(string WorkingDirectory, string? ApiEndpoint),
            Action<string>, Action<string, string?>>>()(context, directory, environment);

    private static ProcessStartInfo Build(string executable, bool hands, string project, string cwd, string? endpoint) =>
        Method("BuildOpenStartInfo").CreateDelegate<Func<string, bool, string, string, string?, ProcessStartInfo>>()
            (executable, hands, project, cwd, endpoint);

    private static Task<int> Launch(string? executable, bool hands, string project, string cwd, string? endpoint,
        TextWriter output, TextWriter error, Func<ProcessStartInfo, Task<(int ExitCode, string StandardOutput, string StandardError)>> start) =>
        Method("LaunchAsync").CreateDelegate<Func<string?, bool, string, string, string?, TextWriter, TextWriter,
            Func<ProcessStartInfo, Task<(int ExitCode, string StandardOutput, string StandardError)>>, Task<int>>>()
                (executable, hands, project, cwd, endpoint, output, error, start);

    private static Task<(int ExitCode, string StandardOutput, string StandardError)> StartProcess(ProcessStartInfo start) =>
        Method("StartProcessAsync").CreateDelegate<Func<ProcessStartInfo, Task<(int ExitCode, string StandardOutput, string StandardError)>>>()(start);

    private static ProcessStartInfo Shell(string command)
    {
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        return start;
    }

    private static string NewTemporaryDirectory() => Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "forge-window-test-" + Guid.NewGuid().ToString("N"))).FullName;

    private static MethodInfo Method(string name) => ForgeText.Type("ForgeMission.Cli.MacChatWindow")
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
}
