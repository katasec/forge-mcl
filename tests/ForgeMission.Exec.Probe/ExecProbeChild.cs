using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeMission.Exec.Probe;

internal static class ExecProbeChild
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        switch (arguments[0])
        {
            case "echo":
                using (var input = JsonDocument.Parse(await Console.In.ReadToEndAsync()))
                    Result(input.RootElement.GetProperty("input").GetString() ?? "");
                return 0;
            case "args": Result(JsonSerializer.Serialize(arguments[1..], ProbeJsonContext.Default.StringArray)); return 0;
            case "cwd": Result(Directory.GetCurrentDirectory()); return 0;
            case "decline":
                CloseInput();
                Result("declined");
                return 0;
            case "parent": return await ParentAsync(arguments[1]);
            case "exited-descendant":
                using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, ArgumentList = { "--child", "args", "already exited" } })!)
                { await child.StandardOutput.ReadToEndAsync(); await child.WaitForExitAsync(); }
                Result("descendant exited");
                return 0;
            case "descendant": return await DescendantAsync(arguments[1], int.Parse(arguments[2]));
            case "unrelated":
                while (!File.Exists(arguments[1])) await Task.Delay(10);
                return 37;
            default: throw new InvalidOperationException("Unknown probe child mode.");
        }
    }

    internal static ProcessStartInfo Command(params string[] arguments)
    {
        var options = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        options.ArgumentList.Add("--child");
        foreach (var argument in arguments) options.ArgumentList.Add(argument);
        return options;
    }

    private static async Task<int> ParentAsync(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "root.pid"), Environment.ProcessId.ToString());
        using var child = Process.Start(Command("descendant", directory, "1"))!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Directory.GetFiles(directory, "child-*.pid").Length != 2) await Task.Delay(10, deadline.Token);
        Result("root exited");
        return 0;
    }

    private static async Task<int> DescendantAsync(string directory, int depth)
    {
        using var child = depth > 0 ? Process.Start(Command("descendant", directory, "0")) : null;
        File.WriteAllText(Path.Combine(directory, $"child-{Environment.ProcessId}.pid"), Environment.ProcessId.ToString());
        await Task.Delay(2200);
        await File.WriteAllTextAsync(Path.Combine(directory, $"sentinel-{Environment.ProcessId}"), "escaped");
        await Task.Delay(30_000);
        return 0;
    }

    private static void Result(string result) => Console.WriteLine(JsonSerializer.Serialize(
        new Dictionary<string, string> { ["result"] = result }, ProbeJsonContext.Default.DictionaryStringString));

    private static void CloseInput()
    {
        var success = OperatingSystem.IsWindows() ? CloseHandle(GetStdHandle(-10)) : Close(0) == 0;
        if (!success) throw new IOException("Probe could not close stdin.");
    }

    [DllImport("libc", EntryPoint = "close")] private static extern int Close(int descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int standardHandle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class ProbeJsonContext : JsonSerializerContext;
