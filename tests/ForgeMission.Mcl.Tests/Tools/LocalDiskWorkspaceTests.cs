using ForgeMission.Core.Tools;

namespace ForgeMission.Tests.Tools;

public sealed class LocalDiskWorkspaceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("forge-workspace-").FullName;
    private readonly string _secondRoot = Directory.CreateTempSubdirectory("forge-workspace-second-").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_secondRoot, recursive: true);
    }

    [Fact]
    public void TryResolvePath_AcceptsAnyConfiguredRoot()
    {
        var workspace = new LocalDiskWorkspace([_root, _secondRoot]);
        var secondRootFile = Path.Combine(_secondRoot, "file.txt");

        Assert.True(workspace.TryResolvePath("local.txt", out var primaryResolved, out var primaryError));
        Assert.Null(primaryError);
        Assert.StartsWith(_root, primaryResolved);

        Assert.True(workspace.TryResolvePath(secondRootFile, out var secondResolved, out var secondError));
        Assert.Null(secondError);
        Assert.Equal(secondRootFile, secondResolved);
    }

    [Fact]
    public void TryResolvePath_RejectsPathOutsideEveryRoot()
    {
        var workspace = new LocalDiskWorkspace([_root, _secondRoot]);
        var outsideRoot = Directory.CreateTempSubdirectory("forge-workspace-outside-").FullName;
        try
        {
            var outsideFile = Path.Combine(outsideRoot, "file.txt");

            Assert.False(workspace.TryResolvePath(outsideFile, out _, out var error));
            Assert.Contains("outside the workspace roots", error);
        }
        finally
        {
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WorkingDirOutsideRoots_ReturnsError()
    {
        var workspace = new LocalDiskWorkspace(_root);
        var outsideRoot = Directory.CreateTempSubdirectory("forge-workspace-outside-").FullName;
        try
        {
            var result = await workspace.ExecuteAsync("echo hello", workingDir: outsideRoot);

            Assert.True(result.IsError);
            Assert.Contains("outside the workspace roots", result.Content);
        }
        finally
        {
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [SkippableFact]
    public async Task ExecuteAsync_CallerCancellation_KillsTheShellBeforeThrowing()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses /bin/sh");
        var workspace = new LocalDiskWorkspace(_root);
        var pidFile = Path.Combine(_root, "pid");
        using var cancel = new CancellationTokenSource();

        var run = workspace.ExecuteAsync("echo $$ > pid; sleep 30", ct: cancel.Token);
        var pid = await WaitForPidAsync(pidFile);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(IsRunning(pid));
    }

    [SkippableFact]
    public async Task ExecuteAsync_Timeout_KillsTheShellAndReportsTheTimeout()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses /bin/sh");
        var workspace = new LocalDiskWorkspace(_root, TimeSpan.FromMilliseconds(500));

        var result = await workspace.ExecuteAsync("echo $$ > pid; sleep 30");

        Assert.True(result.IsError);
        Assert.Contains("timed out", result.Content);
        Assert.False(IsRunning(int.Parse(File.ReadAllText(Path.Combine(_root, "pid")).Trim())));
    }

    [SkippableFact]
    public async Task ExecuteAsync_ShellReadsEmptyStdin()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses /bin/sh");
        var workspace = new LocalDiskWorkspace(_root, TimeSpan.FromSeconds(10));

        var result = await workspace.ExecuteAsync("cat; echo done");

        Assert.False(result.IsError, result.Content);
        Assert.Equal("done", result.Content.Trim());
    }

    [SkippableFact]
    public async Task ExecuteAsync_OutputOverTheLimit_ReturnsError()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses /bin/sh");
        var workspace = new LocalDiskWorkspace(_root, TimeSpan.FromSeconds(30));

        var result = await workspace.ExecuteAsync("head -c 5000000 /dev/zero");

        Assert.True(result.IsError);
        Assert.Contains("Command stdout exceeds", result.Content);
    }

    private static async Task<int> WaitForPidAsync(string pidFile)
    {
        for (var i = 0; i < 100; i++)
        {
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid)) return pid;
            await Task.Delay(50);
        }
        throw new TimeoutException("The shell never wrote its pid.");
    }

    private static bool IsRunning(int pid)
    {
        try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }
}
