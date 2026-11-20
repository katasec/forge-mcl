using System.ComponentModel;
using System.Diagnostics;
using ForgeMission.Core.Runtime;

namespace ForgeMission.Core.Tools;

public sealed class LocalDiskWorkspace : IWorkspace
{
    private const int MaxStreamBytes = 4 * 1024 * 1024;
    private static readonly ChildProcessLimits Limits = new(MaxStreamBytes, MaxStreamBytes, "Command");

    private readonly TimeSpan _timeout;

    public LocalDiskWorkspace(string root, TimeSpan? timeout = null)
        : this([root], timeout)
    {
    }

    public LocalDiskWorkspace(IEnumerable<string> roots, TimeSpan? timeout = null)
    {
        Roots = roots.Select(root =>
        {
            var fullRoot = Path.GetFullPath(root);
            return WorkspaceGuard.RealPath(fullRoot, fullRoot);
        }).ToList();
        if (Roots.Count == 0)
            throw new ArgumentException("At least one workspace root is required.", nameof(roots));

        _timeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    public IReadOnlyList<string> Roots { get; }

    public bool TryResolvePath(string path, out string resolved, out string? error) =>
        WorkspaceGuard.TryResolve(Roots, path, out resolved, out error);

    public Task<bool> ExistsAsync(string resolvedPath, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(resolvedPath));

    public Task<string> ReadFileAsync(string resolvedPath, CancellationToken ct = default) =>
        File.ReadAllTextAsync(resolvedPath, ct);

    public Task WriteFileAsync(string resolvedPath, string content, CancellationToken ct = default) =>
        File.WriteAllTextAsync(resolvedPath, content, ct);

    public async Task<ToolExecutionResult> ExecuteAsync(
        string command,
        string? workingDir = null,
        CancellationToken ct = default)
    {
        var resolvedWorkingDir = Roots[0];
        if (workingDir is not null && !TryResolvePath(workingDir, out resolvedWorkingDir, out var pathError))
            return ToolExecutionResult.Error(pathError!);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);

        Process process;
        try
        {
            process = Process.Start(ShellStartInfo(command, resolvedWorkingDir))
                ?? throw new InvalidOperationException("No process was started.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ToolExecutionResult.Error($"Failed to start command: {ex.Message}");
        }

        using (process)
        {
            try
            {
                var (stdout, stderr, exitCode) = await ChildProcess.ExchangeAsync(process, "", Limits, cts);
                return ToResult(stdout, stderr, exitCode);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ToolExecutionResult.Error($"Command timed out after {_timeout.TotalSeconds:0}s: {command}");
            }
            catch (IOException ex)
            {
                ct.ThrowIfCancellationRequested();
                return ToolExecutionResult.Error($"Command failed: {ex.Message}");
            }
        }
    }

    // stdin is redirected and closed at once, so the shell never reads the host's stdin (in
    // forge mcp that is the protocol stream).
    private static ProcessStartInfo ShellStartInfo(string command, string workingDir)
    {
        var psi = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        {
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = workingDir,
        };
        psi.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        psi.ArgumentList.Add(command);
        return psi;
    }

    private static ToolExecutionResult ToResult(string stdout, string stderr, int exitCode)
    {
        var combined = string.Join('\n', new[] { stdout, stderr }.Where(s => !string.IsNullOrEmpty(s)));
        return exitCode == 0
            ? new ToolExecutionResult(combined)
            : ToolExecutionResult.Error($"Command exited with code {exitCode}\n{combined}".TrimEnd());
    }
}
