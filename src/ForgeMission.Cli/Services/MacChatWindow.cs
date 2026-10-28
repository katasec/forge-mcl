using System.Diagnostics;
using System.Text;

namespace ForgeMission.Cli.Services;

// The CLI opens one dedicated Ghostty instance and restores its child context before chat.
// Ghostty owns scoped key forwarding/window lifetime; the existing chat owns its cleanup.
internal static class MacChatWindow
{
    internal const string Marker = "FORGE_CHAT_WINDOW";
    private static readonly UTF8Encoding ContextEncoding = new(false, throwOnInvalidBytes: true);

    internal static async Task<int> RunCommandAsync(Func<Task<int>> runChat, string? marker,
        Action<string> restoreContext,
        Action dismissError, TextWriter error, CancellationToken cancellationToken)
    {
        if (marker is null)
            return await runChat();

        try
        {
            restoreContext(DecodeContext(marker));
            if (await runChat() == 0)
                return 0;
        }
        // The caller's own cancellation propagates; any other chat failure is shown in the window.
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            error.WriteLine($"forge chat: {failure.Message}");
        }

        error.WriteLine("Press any key to close this window.");
        error.Flush();
        dismissError();
        return 1;
    }

    internal static async Task<int> LaunchAsync(string? executable, bool hands, string projectFile,
        string originalCwd, TextWriter output, TextWriter error,
        Func<ProcessStartInfo, CancellationToken, Task<(int ExitCode, string StandardOutput, string StandardError)>>? start = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
        {
            error.WriteLine("forge chat: could not find its native executable for the new window.");
            return 1;
        }
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine("forge chat: the new Mac window requires the installed native forge executable, not dotnet.");
            return 1;
        }

        try
        {
            var result = await (start ?? StartProcessAsync)(BuildOpenStartInfo(executable, hands,
                projectFile, originalCwd), cancellationToken);
            if (result.ExitCode != 0)
            {
                error.WriteLine($"forge chat: could not open its window (open exited {result.ExitCode}).");
                return 1;
            }
            output.WriteLine("Opened Forge chat in a new window.");
            return 0;
        }
        // The caller's own cancellation propagates; any other launch failure is reported.
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            error.WriteLine($"forge chat: could not open its window: {failure.Message}");
            return 1;
        }
    }

    internal static bool ShouldLaunch(bool isMac, bool interactive, string? marker) =>
        isMac && interactive && marker is null;

    internal static string? ChildMarker(bool isMac, bool interactive, string? marker) =>
        isMac && interactive ? marker : null;

    internal static void RestoreContext(string workingDirectory, Action<string> changeDirectory) =>
        changeDirectory(workingDirectory);

    internal static string EncodeContext(string workingDirectory) =>
        Convert.ToBase64String(ContextEncoding.GetBytes(workingDirectory));

    internal static string DecodeContext(string marker)
    {
        try
        {
            var cwd = ContextEncoding.GetString(Convert.FromBase64String(marker));
            if (string.IsNullOrEmpty(cwd) || !Path.IsPathFullyQualified(cwd))
                throw new FormatException("Invalid Forge chat window working directory.");
            return cwd;
        }
        catch (Exception failure) when (failure is FormatException or DecoderFallbackException)
        {
            throw new FormatException("Invalid Forge chat window context.", failure);
        }
    }

    internal static ProcessStartInfo BuildOpenStartInfo(string executable, bool hands, string projectFile,
        string originalCwd)
    {
        var argv = new List<string> { executable, "chat" };
        if (hands)
            argv.Add("--hands");
        argv.AddRange(["--project", projectFile]);
        var command = "shell:" + string.Join(" ", argv.Select(QuoteArgument));
        var start = new ProcessStartInfo("/usr/bin/open")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "-n", "-b", "com.mitchellh.ghostty", "--args", "--config-file=",
            "--initial-command=" + command, "--command=" + command, "--keybind=cmd+a=text:\\x01",
            "--working-directory=\"" + originalCwd + "\"",
            "--env=" + Marker + "=" + EncodeContext(originalCwd),
            "--input=", "--shell-integration=none", "--initial-window=true",
            "--wait-after-command=false", "--quit-after-last-window-closed=true",
        })
            start.ArgumentList.Add(argument);
        return start;
    }

    internal static async Task<(int ExitCode, string StandardOutput, string StandardError)> StartProcessAsync(ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo)
            ?? throw new IOException("The window launch process did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output, await error);
    }

    private static string QuoteArgument(string argument) => "'" + argument.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
