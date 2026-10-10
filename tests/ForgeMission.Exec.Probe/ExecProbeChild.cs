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
            case "pressure": return await PressureAsync();
            case "workspace": return await WorkspaceAsync();
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

    internal static async Task<string> BuildSignalWitnessAsync(string directory)
    {
        var source = Path.Combine(directory, "signal-witness.c");
        var executable = Path.Combine(directory, "signal-witness");
        await File.WriteAllTextAsync(source, SignalWitnessSource);
        var options = new ProcessStartInfo("/usr/bin/xcrun") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "clang", "-std=c11", "-Wall", "-Wextra", "-Werror", source, "-o", executable }) options.ArgumentList.Add(argument);
        using var compiler = Process.Start(options) ?? throw new IOException("Could not start signal witness compiler.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = compiler.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = compiler.StandardError.ReadToEndAsync(deadline.Token);
        Exception? failure = null;
        try
        {
            await compiler.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(output, error);
            if (compiler.ExitCode != 0 || output.Result.Length != 0 || error.Result.Length != 0)
                throw new IOException($"Signal witness compiler failed or emitted diagnostics: exit={compiler.ExitCode}; {output.Result}{error.Result}");
        }
        catch (Exception exception) { failure = exception; }
        var cleanupErrors = await JoinSignalCompilerAsync(compiler, output, error, deadline);
        if (cleanupErrors.Count != 0)
            throw new AggregateException("Signal witness compiler cleanup failed.", failure is null ? cleanupErrors : [failure, .. cleanupErrors]);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return executable;
    }

    internal static MacSignalAction QueryMacAction(int signal)
    {
        if (QuerySignalAction(signal, IntPtr.Zero, out var action) == 0) return action;
        var error = Marshal.GetLastPInvokeError();
        throw new IOException($"Probe query signal {signal} failed (OS error {error}).");
    }

    internal static void SetMacAction(int signal, MacSignalAction action)
    {
        if (SetSignalAction(signal, ref action, IntPtr.Zero) == 0) return;
        var error = Marshal.GetLastPInvokeError();
        throw new IOException($"Probe set signal {signal} failed (OS error {error}).");
    }

    internal static MacSignalAction CaughtMacAction() => new()
    {
        Handler = Marshal.GetFunctionPointerForDelegate(TestSignalHandler), Flags = 0x42
    };

    internal static void AssertMacAction(int signal, MacSignalAction expected)
    {
        var actual = QueryMacAction(signal);
        if (actual.Handler != expected.Handler || actual.Mask != expected.Mask || actual.Flags != expected.Flags)
            throw new IOException($"Probe parent signal {signal} changed.");
    }

    internal static List<Exception> RestoreMacActions(Dictionary<int, MacSignalAction> originals)
    {
        var errors = new List<Exception>();
        foreach (var pair in originals)
        {
            try { SetMacAction(pair.Key, pair.Value); AssertMacAction(pair.Key, pair.Value); }
            catch (Exception exception) { errors.Add(exception); }
        }
        return errors;
    }

    private static async Task<List<Exception>> JoinSignalCompilerAsync(
        Process compiler, Task<string> output, Task<string> error, CancellationTokenSource exchange)
    {
        var errors = new List<Exception>();
        try { if (!compiler.HasExited) compiler.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (compiler.HasExited) { }
        catch (Exception exception) { errors.Add(exception); }
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await compiler.WaitForExitAsync(cleanup.Token); }
        catch (Exception exception) { errors.Add(exception); }
        exchange.Cancel();
        try { await Task.WhenAll(output, error); }
        catch (OperationCanceledException) when (exchange.IsCancellationRequested) { }
        catch (Exception exception) { errors.Add(exception); }
        return errors;
    }

    private const string SignalWitnessSource = """
        #define _DARWIN_C_SOURCE 1
        #include <signal.h>
        #include <stddef.h>
        #include <stdint.h>
        #include <stdio.h>
        #include <errno.h>
        _Static_assert(sizeof(sigset_t) == 4, "sigset size");
        _Static_assert(sizeof(struct sigaction) == 16, "sigaction size");
        _Static_assert(offsetof(struct sigaction, sa_handler) == 0, "handler offset");
        _Static_assert(offsetof(struct sigaction, sa_mask) == 8, "mask offset");
        _Static_assert(offsetof(struct sigaction, sa_flags) == 12, "flags offset");
        int main(void) {
            struct sigaction caught, ignored;
            if (sigaction(SIGUSR2, NULL, &caught) != 0 || sigaction(SIGURG, NULL, &ignored) != 0) {
                fprintf(stderr, "sigaction witness failed: errno=%d\n", errno);
                return 1;
            }
            printf("{\"result\":\"%llu:%d:%llu:%d\"}\n",
                (unsigned long long)(uintptr_t)caught.sa_handler, caught.sa_flags,
                (unsigned long long)(uintptr_t)ignored.sa_handler, ignored.sa_flags);
            return 0;
        }
        """;

    private static async Task<int> ParentAsync(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "root.pid"), Environment.ProcessId.ToString());
        using var child = Process.Start(Command("descendant", directory, "1"))!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Directory.GetFiles(directory, "child-*.pid").Length != 2) await Task.Delay(10, deadline.Token);
        Result("root exited");
        return 0;
    }

    private static async Task<int> PressureAsync()
    {
        var output = Task.Run(() => Result(new string('o', 1_000_000)));
        var error = Task.Run(() => Console.Error.Write(new string('e', 64 * 1024)));
        // Both drains must finish before stdin: an input-first sequential parent cannot pass.
        await Task.WhenAll(output, error);
        using var input = JsonDocument.Parse(await Console.In.ReadToEndAsync());
        var text = input.RootElement.GetProperty("input").GetString()!;
        if (text.Length != 2_000_000 || text.Any(value => value != 'i')) throw new Exception("Concurrent stdin payload mismatch.");
        return 0;
    }

    private static async Task<int> WorkspaceAsync()
    {
        using var input = JsonDocument.Parse(await Console.In.ReadToEndAsync());
        var result = input.RootElement.EnumerateObject().ToDictionary(value => value.Name, value => value.Value.GetString()!);
        string[] names = ["FORGE_INPUT_source_file", "FORGE_INPUT_document", "FORGE_SOURCE_FILE", "FORGE_WORK_DIR", "FORGE_INPUT_DIR", "FORGE_OUTPUT_DIR",
            "FORGE_INPUT_stale", "FORGE_INPUT_mode", "FORGE_PROBE_INHERITED", "FORGE_PROBE_AUTHORED"];
        foreach (var name in names) result[name] = Environment.GetEnvironmentVariable(name) ?? "";
        result["content"] = File.ReadAllText(result["source_file"]);
        result["cwd_marker"] = File.ReadAllText("cwd-marker");
        File.WriteAllText(Path.Combine(result["output_dir"], "probe.txt"), result["content"]);
        Result(JsonSerializer.Serialize(result, ProbeJsonContext.Default.DictionaryStringString));
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

    [StructLayout(LayoutKind.Sequential)]
    internal struct MacSignalAction
    {
        internal IntPtr Handler;
        internal uint Mask;
        internal int Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SignalInfoHandler(int signal, IntPtr info, IntPtr context);
    private static readonly SignalInfoHandler TestSignalHandler = (_, _, _) => { };

    [DllImport("libc", EntryPoint = "sigaction", SetLastError = true)] private static extern int QuerySignalAction(int signal, IntPtr action, out MacSignalAction previous);
    [DllImport("libc", EntryPoint = "sigaction", SetLastError = true)] private static extern int SetSignalAction(int signal, ref MacSignalAction action, IntPtr previous);
    [DllImport("libc", EntryPoint = "close")] private static extern int Close(int descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int standardHandle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class ProbeJsonContext : JsonSerializerContext;
