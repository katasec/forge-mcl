using System.Diagnostics;

namespace ForgeMission.Tests.Cli.Harness;

/// <summary>
/// The one shared harness for driving the forge CLI out of process (`dotnet forge.dll ...`).
/// Every run gets an isolated HOME, a scrubbed environment (no operator credentials, no FORGE_*),
/// a forced non-forwarding proxy, and a no-op browser launcher first on PATH, so nothing a test starts
/// can reach the internet or open a browser (`forge login`). Tests set
/// what they need through <see cref="Variables"/>, <see cref="WriteConfig"/> and loopback stubs.
/// </summary>
public sealed class ForgeCliHarness : IAsyncDisposable
{
    private static readonly string[] OperatorCredentials =
    [
        "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "XAI_API_KEY", "GROK_API_KEY",
        "GITHUB_TOKEN", "GH_TOKEN", "AZURE_OPENAI_API_KEY",
    ];

    // The commands `forge login` starts to open the sign-in page (macOS, Linux).
    private static readonly string[] BrowserLaunchers = ["open", "xdg-open"];

    private static readonly Lazy<string> ForgeDll = new(LocateForgeDll);

    /// <summary>The built forge.dll matching this test build's configuration.</summary>
    internal static string ForgeDllPath => ForgeDll.Value;
    private readonly List<HttpStub> stubs = [];

    private ForgeCliHarness(string home, NonForwardingProxy proxy)
    {
        Home = home;
        WorkingDirectory = home;
        Proxy = proxy;
    }

    /// <summary>The isolated HOME/USERPROFILE for every run; deleted on dispose.</summary>
    public string Home { get; }

    /// <summary>The child's working directory; defaults to <see cref="Home"/>.</summary>
    public string WorkingDirectory { get; set; }

    /// <summary>The proxy every run is forced through; assert on its attempts for "no outbound call".</summary>
    public NonForwardingProxy Proxy { get; }

    /// <summary>Variables this test sets explicitly, applied after scrubbing; null removes one.</summary>
    public Dictionary<string, string?> Variables { get; } = [];

    public static ForgeCliHarness Start()
    {
        var home = Path.Combine(Path.GetTempPath(), "forge-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, ".forge"));
        InstallNoOpBrowser(BrowserDirectory(home));
        return new ForgeCliHarness(home, NonForwardingProxy.Start());
    }

    /// <summary>Starts a loopback stub owned by this harness (disposed with it).</summary>
    public async Task<HttpStub> StartStubAsync(Func<StubRequest, StubResponse> respond)
    {
        var stub = await HttpStub.StartAsync(respond);
        stubs.Add(stub);
        return stub;
    }

    public void WriteConfig(string json) => WriteForgeFile("config.json", json);

    /// <summary>Saves a test platform login (key <c>fg_test_key</c>) for <paramref name="endpoint"/>.</summary>
    public void SignIn(string endpoint) =>
        WriteForgeFile("credentials.json",
            $$$"""{"platform":{"key":"fg_test_key","user":"harness@test","endpoint":"{{{endpoint}}}"}}""");

    /// <summary>Writes <c>~/.forge/{fileName}</c> in the isolated HOME.</summary>
    public void WriteForgeFile(string fileName, string contents) =>
        File.WriteAllText(Path.Combine(Home, ".forge", fileName), contents);

    public Task<CliResult> RunAsync(params string[] args) =>
        ConverseAsync((_, _) => Task.CompletedTask, args);

    /// <summary>
    /// Runs the CLI while <paramref name="converse"/> drives its stdin and reads its stdout (for a
    /// stdio server such as <c>forge mcp</c>); stdin closes when it returns. The result's
    /// <see cref="CliResult.StandardOutput"/> holds only the output the conversation did not read.
    /// </summary>
    public async Task<CliResult> ConverseAsync(Func<StreamWriter, StreamReader, Task> converse, params string[] args)
    {
        var start = BuildStartInfo(args);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("dotnet did not start the forge CLI.");
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await converse(process.StandardInput, process.StandardOutput).WaitAsync(timeout.Token);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(timeout.Token);
            return new CliResult(process.ExitCode, await output, await error);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    /// <summary>
    /// Runs a long-lived command (a server such as <c>forge serve</c>) while <paramref name="whileRunning"/>
    /// exercises it, then stops it; the result's exit code is that of the stopped process.
    /// </summary>
    public async Task<CliResult> RunWhileAsync(Func<Task> whileRunning, params string[] args)
    {
        using var process = Process.Start(BuildStartInfo(args))
            ?? throw new InvalidOperationException("dotnet did not start the forge CLI.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await whileRunning().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { process.Kill(entireProcessTree: true); }
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, await output, await error);
    }

    internal ProcessStartInfo BuildStartInfo(IEnumerable<string> args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(ForgeDllPath);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        IsolateEnvironment(start.Environment);
        return start;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var stub in stubs) await stub.DisposeAsync();
        await Proxy.DisposeAsync();
        Directory.Delete(Home, recursive: true);
    }

    private void IsolateEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var name in OperatorCredentials) environment.Remove(name);
        foreach (var name in environment.Keys.Where(IsForgeVariable).ToArray()) environment.Remove(name);

        environment["HOME"] = Home;
        environment["USERPROFILE"] = Home;
        var proxy = Proxy.Address.ToString();
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" })
        {
            environment[name] = proxy;
            environment[name.ToLowerInvariant()] = proxy;
        }
        environment["NO_PROXY"] = environment["no_proxy"] = "127.0.0.1,localhost";
        environment["PATH"] = BrowserDirectory(Home) + Path.PathSeparator + environment["PATH"];

        foreach (var (name, value) in Variables) environment[name] = value;
    }

    private static string BrowserDirectory(string home) => Path.Combine(home, "no-op-browser");

    private static void InstallNoOpBrowser(string directory)
    {
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows()) return; // forge opens the browser through the shell there
        foreach (var launcher in BrowserLaunchers)
        {
            var path = Path.Combine(directory, launcher);
            File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static bool IsForgeVariable(string name) => name.StartsWith("FORGE_", StringComparison.OrdinalIgnoreCase);

    private static string LocateForgeDll()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "src", "ForgeMission.Cli", "bin", configuration, "net10.0", "forge.dll");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Could not locate built forge.dll for CLI tests.");
    }
}

public sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
