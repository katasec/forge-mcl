using System.Diagnostics;
using System.Text;

namespace ForgeMission.Core.Adapters;

internal static class ExecProcessArguments
{
    internal static string ResolveUnixCommand(string command)
    {
        if (Path.IsPathRooted(command)) return command;
        var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        var candidate = Path.Combine(executableDirectory ?? "", command);
        if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        candidate = Path.Combine(Directory.GetCurrentDirectory(), command);
        if (File.Exists(candidate)) return candidate;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
        {
            if (directory.Length == 0) continue;
            candidate = Path.Combine(directory, command);
            if (File.Exists(candidate) && PosixNative.Access(candidate, 1) == 0) return candidate;
        }
        throw new System.ComponentModel.Win32Exception(2, $"Executable '{command}' was not found.");
    }

    internal static string WindowsCommandLine(ProcessStartInfo options)
    {
        var text = new StringBuilder().Append('"').Append(options.FileName.Trim().Trim('"')).Append('"');
        foreach (var argument in options.ArgumentList) text.Append(' ').Append(Quote(argument));
        return text.ToString();
    }

    private static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"')) return argument;
        var text = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            text.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            text.Append(character);
            slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }
}
