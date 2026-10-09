using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Runtime;

namespace ForgeMission.Core.Adapters;

public class ExecExpertRunner(string defaultTimeout = "30s") : IExpertRunner
{
    private readonly PipelineExecutionWorkspace? _workspace;
    private readonly string _stepKey = "";
    private readonly int _attempt = 1;

    internal ExecExpertRunner(string defaultTimeout, PipelineExecutionWorkspace? workspace, string stepKey, int attempt)
        : this(defaultTimeout)
    {
        _workspace = workspace;
        _stepKey = stepKey;
        _attempt = attempt;
    }

    public async Task<StepEnvelope> RunAsync(
        ExpertDefinition expert, Dictionary<string, object> context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var inputs = ProcessInputs(expert, context);
        var inputJson = BuildInputJson(expert, inputs);
        if (Encoding.UTF8.GetByteCount(inputJson) > MaxOutputBytes)
            return new("", "fail", "Executable stdin JSON exceeds 4 MiB.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = ParseTimeout(string.IsNullOrWhiteSpace(expert.Timeout) ? defaultTimeout : expert.Timeout);
        cancellation.CancelAfter(timeout);
        ExecProcess process;
        try { process = ExecProcess.Start(ProcessOptions(expert, inputs, context)); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return new("", "fail", $"Failed to start '{expert.Command}': {exception.Message}"); }

        try
        {
            var (stdout, stderr, exitCode) = await ExchangeAsync(process, inputJson, cancellation, ct);
            ct.ThrowIfCancellationRequested();
            if (exitCode != 0)
                return new(stderr, "fail", $"Expert '{expert.Name}' exited with code {exitCode}. stderr: {stderr}".TrimEnd());
            return ApplyOutput(expert, context, stdout);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new("", "fail", $"Expert '{expert.Name}' timed out after {timeout}."); }
        catch (ExecProcessCleanupException) { throw; }
        catch (IOException exception)
        { ct.ThrowIfCancellationRequested(); return new("", "fail", $"Executable I/O failed: {exception.Message}"); }
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ExpertDefinition expert,
        Dictionary<string, object> context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Process backend produces output only on exit; no true streaming for exec.
        // Yield only envelope.Text — not the JSON envelope — so content writers (Open WebUI,
        // CLI) receive plain text. ParseStreamedEnvelope handles non-JSON as a pass envelope.
        var envelope = await RunAsync(expert, context, ct);
        yield return envelope.Text ?? string.Empty;
    }

    private const int MaxOutputBytes = 4 * 1024 * 1024;
    private const int MaxErrorBytes = 64 * 1024;

    private Dictionary<string, object> ProcessInputs(ExpertDefinition expert, Dictionary<string, object> context)
    {
        if (_workspace is null) return context;
        var inputs = new Dictionary<string, object>(context, StringComparer.Ordinal);
        foreach (var name in expert.Inputs ?? [])
            if (inputs.TryGetValue(name, out var value) && VerifiedPath(value) is { } absolute)
                inputs[name] = absolute;
        var outputDirectory = _workspace.GetStepOutputDirectory(_stepKey, _attempt);
        CreateOutputDirectory(outputDirectory);
        inputs["work_dir"] = _workspace.RootDirectory;
        inputs["input_dir"] = Path.Combine(_workspace.RootDirectory, "inputs");
        inputs["output_dir"] = outputDirectory;
        return inputs;
    }

    private string? VerifiedPath(object value)
    {
        if (_workspace is null || value is not string relative || !_workspace.ArtifactPaths.ContainsKey(relative)) return null;
        if (!DurableMissionPackageValidator.IsCanonicalPath(relative))
            throw new InvalidOperationException("Verified artifact registry contains a noncanonical path.");
        return Path.Combine(_workspace.RootDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void CreateOutputDirectory(string path) => Directory.CreateDirectory(path);

    private ProcessStartInfo ProcessOptions(ExpertDefinition expert, Dictionary<string, object> inputs, Dictionary<string, object> context)
    {
        var options = new ProcessStartInfo(expert.Command)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = string.IsNullOrEmpty(expert.ExpertDirectory) ? Directory.GetCurrentDirectory() : expert.ExpertDirectory,
        };
        foreach (var argument in expert.Args ?? []) options.ArgumentList.Add(argument);
        AddForgeEnvironment(options, inputs);
        if (_workspace is not null) AddWorkspaceEnvironment(options, expert, inputs, context);
        return options;
    }

    private void AddWorkspaceEnvironment(ProcessStartInfo options, ExpertDefinition expert,
        Dictionary<string, object> inputs, Dictionary<string, object> context)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var key in options.Environment.Keys.Where(k => k.StartsWith("FORGE_INPUT_", comparison)
            || k.Equals("FORGE_SOURCE_FILE", comparison)).ToArray()) options.Environment.Remove(key);
        options.Environment["FORGE_WORK_DIR"] = inputs["work_dir"].ToString();
        options.Environment["FORGE_INPUT_DIR"] = inputs["input_dir"].ToString();
        options.Environment["FORGE_OUTPUT_DIR"] = inputs["output_dir"].ToString();
        foreach (var name in expert.Inputs ?? [])
        {
            if (!context.TryGetValue(name, out var value) || VerifiedPath(value) is not { } absolute) continue;
            options.Environment["FORGE_INPUT_" + name] = absolute;
            if (name == "source_file") options.Environment["FORGE_SOURCE_FILE"] = absolute;
        }
    }

    private static async Task<(string Output, string Error, int ExitCode)> ExchangeAsync(
        ExecProcess process, string input, CancellationTokenSource cancellation, CancellationToken callerToken)
    {
        var write = WriteInputAsync(process.StandardInput, input, cancellation);
        var output = ReadBoundedAsync(process.StandardOutput, MaxOutputBytes, "stdout", cancellation);
        var error = ReadBoundedAsync(process.StandardError, MaxErrorBytes, "stderr", cancellation);
        var io = Task.WhenAll(write, output, error);
        var observer = process.ObserveExitAsync(cancellation.Token);
        Exception? failure = null;
        try { await observer; await io; }
        catch (Exception exception) { failure = exception; }
        cancellation.Cancel();
        List<IOException> cleanupFailures = [];
        try { await observer; }
        catch (OperationCanceledException) { }
        catch (IOException exception) { cleanupFailures.Add(exception); }
        try { process.Terminate(); }
        catch (IOException exception) { cleanupFailures.Add(exception); }
        var exitCode = await JoinProcessAsync(process, cleanupFailures);
        try { await io; }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        { failure = io.Exception?.InnerExceptions.OfType<IOException>().FirstOrDefault() ?? failure ?? exception; }
        try { await process.DisposeAsync(); }
        catch (IOException exception) { cleanupFailures.Add(exception); }
        if (failure is not null || cleanupFailures.Count > 0)
            ThrowExchangeFailure(failure ?? new IOException("Process cleanup failed."), cleanupFailures, callerToken);
        callerToken.ThrowIfCancellationRequested();
        return (await output, await error, exitCode);
    }

    private static async Task<int> JoinProcessAsync(ExecProcess process, List<IOException> failures)
    {
        using var deadline = new CancellationTokenSource(ExecProcess.CleanupBudget);
        try { return await process.JoinAsync(deadline.Token); }
        catch (IOException exception) { failures.Add(exception); return -1; }
    }

    private static void ThrowExchangeFailure(Exception failure,
        IReadOnlyList<IOException> cleanupFailures, CancellationToken callerToken)
    {
        if (cleanupFailures.Count > 0)
            throw new ExecProcessCleanupException("exchange cleanup", new AggregateException(cleanupFailures), failure);
        if (failure is ExecProcessCleanupException) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        callerToken.ThrowIfCancellationRequested();
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task<string> WriteInputAsync(Stream stream, string input, CancellationTokenSource cancellation)
    {
        try { await stream.WriteAsync(Encoding.UTF8.GetBytes(input), cancellation.Token); }
        catch (IOException exception) when (DeclinedInput(exception, cancellation.Token)) { }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        { cancellation.Cancel(); throw; }
        finally { stream.Dispose(); }
        return "";
    }

    private static bool DeclinedInput(IOException exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        if (OperatingSystem.IsWindows())
            return exception.HResult is unchecked((int)0x8007006D) or unchecked((int)0x800700E8) or unchecked((int)0x800700E9);
        return exception.InnerException is System.Net.Sockets.SocketException
            { NativeErrorCode: 32, SocketErrorCode: System.Net.Sockets.SocketError.Shutdown };
    }
    private static async Task<string> ReadBoundedAsync(Stream stream, int limit, string name, CancellationTokenSource cancellation)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellation.Token)) > 0)
            {
                if (bytes.Length + count > limit) throw new IOException($"Executable {name} exceeds {limit} bytes.");
                bytes.Write(buffer, 0, count);
            }
            return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        { cancellation.Cancel(); throw; }
    }

    private static StepEnvelope ApplyOutput(ExpertDefinition expert, Dictionary<string, object> context, string stdout)
    {
        using var document = ParseOutput(expert, stdout);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(expert.OutputKey, out var outputValue))
            throw new ExpertLoadException($"Expert '{expert.Name}' stdout JSON is missing declared outputKey '{expert.OutputKey}'.");
        var text = outputValue.ValueKind == JsonValueKind.String ? outputValue.GetString() ?? "" : outputValue.GetRawText();
        context[expert.OutputKey] = text;
        context["output"] = text;
        var status = root.TryGetProperty("status", out var sv) ? sv.GetString() : null;
        var reason = root.TryGetProperty("reason", out var rv) ? rv.GetString() : null;
        if (expert.IsJudge && status == "fail")
            context["feedback"] = !string.IsNullOrWhiteSpace(reason) ? reason
                : !string.IsNullOrWhiteSpace(expert.OnFail) ? expert.OnFail : "Verification failed.";
        return new(text, status ?? "pass", reason);
    }

    private static JsonDocument ParseOutput(ExpertDefinition expert, string stdout)
    {
        try { return JsonDocument.Parse(stdout); }
        catch (JsonException exception)
        { throw new ExpertLoadException($"Expert '{expert.Name}' produced invalid JSON on stdout: {exception.Message}. kind:exec experts must write a JSON object to stdout."); }
    }
    // Serialise the declared inputs keys from the context bag to a JSON object.
    private static string BuildInputJson(ExpertDefinition expert, Dictionary<string, object> context)
    {
        var keys = expert.Inputs ?? [];
        var sb   = new StringBuilder("{");
        var first = true;
        foreach (var key in keys)
        {
            if (!context.TryGetValue(key, out var value)) continue;
            if (!first) sb.Append(',');
            sb.Append(JsonSerializer.Serialize(key, ExecSerializerContext.Default.String));
            sb.Append(':');
            sb.Append(value is string s
                ? JsonSerializer.Serialize(s, ExecSerializerContext.Default.String)
                : JsonSerializer.Serialize(value?.ToString() ?? "", ExecSerializerContext.Default.String));
            first = false;
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void AddForgeEnvironment(ProcessStartInfo psi, Dictionary<string, object> context)
    {
        foreach (var (key, value) in context)
        {
            if (!key.StartsWith("FORGE_", StringComparison.Ordinal)) continue;
            psi.Environment[key] = value?.ToString() ?? "";
        }
    }

    private static TimeSpan ParseTimeout(string timeout)
    {
        if (string.IsNullOrWhiteSpace(timeout))
            return TimeSpan.FromSeconds(30);

        if (timeout.EndsWith('s') && int.TryParse(timeout[..^1], out var secs))
            return TimeSpan.FromSeconds(secs);

        if (timeout.EndsWith('m') && int.TryParse(timeout[..^1], out var mins))
            return TimeSpan.FromMinutes(mins);

        return TimeSpan.FromSeconds(30);
    }
}

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(string))]
internal partial class ExecSerializerContext : JsonSerializerContext { }

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(ForgeMission.Core.Runtime.StepEnvelope))]
internal partial class ExecEnvelopeSerializerContext : JsonSerializerContext { }
