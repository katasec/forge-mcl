using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeMission.Core.Experts;
using ForgeMission.Core.Runtime;

namespace ForgeMission.Core.Adapters;

public class ExecExpertRunner(string defaultTimeout = ExecTimeout.Default) : IExpertRunner
{
    private const int MaxOutputBytes = 4 * 1024 * 1024;
    private const int MaxErrorBytes = 64 * 1024;

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
            return new("", StepEnvelope.Fail, "Executable stdin JSON exceeds 4 MiB.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = ExecTimeout.Parse(string.IsNullOrWhiteSpace(expert.Timeout) ? defaultTimeout : expert.Timeout);
        cancellation.CancelAfter(timeout);
        Process process;
        try
        {
            process = Process.Start(ProcessOptions(expert, inputs, context))
                ?? throw new InvalidOperationException("No process was started.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new("", StepEnvelope.Fail, $"Failed to start '{expert.Command}': {exception.Message}");
        }

        using (process)
        {
            try
            {
                var (stdout, stderr, exitCode) = await ChildProcess.ExchangeAsync(
                    process, inputJson, new(MaxOutputBytes, MaxErrorBytes, "Executable"), cancellation);
                ct.ThrowIfCancellationRequested();
                if (exitCode != 0)
                    return new(stderr, StepEnvelope.Fail, $"Expert '{expert.Name}' exited with code {exitCode}. stderr: {stderr}".TrimEnd());
                return ApplyOutput(expert, context, stdout);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new("", StepEnvelope.Fail, $"Expert '{expert.Name}' timed out after {timeout}.");
            }
            catch (IOException exception)
            {
                ct.ThrowIfCancellationRequested();
                return new("", StepEnvelope.Fail, $"Executable I/O failed: {exception.Message}");
            }
        }
    }

    private Dictionary<string, object> ProcessInputs(ExpertDefinition expert, Dictionary<string, object> context)
    {
        if (_workspace is null) return context;
        var inputs = new Dictionary<string, object>(context, StringComparer.Ordinal);
        foreach (var name in expert.Inputs ?? [])
            if (inputs.TryGetValue(name, out var value) && VerifiedPath(value) is { } absolute)
                inputs[name] = absolute;
        var outputDirectory = _workspace.GetStepOutputDirectory(_stepKey, _attempt);
        CreateOutputDirectory(outputDirectory);
        inputs[ContextKeys.WorkDir] = _workspace.RootDirectory;
        inputs[ContextKeys.InputDir] = Path.Combine(_workspace.RootDirectory, "inputs");
        inputs[ContextKeys.OutputDir] = outputDirectory;
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
        options.Environment["FORGE_WORK_DIR"] = inputs[ContextKeys.WorkDir].ToString();
        options.Environment["FORGE_INPUT_DIR"] = inputs[ContextKeys.InputDir].ToString();
        options.Environment["FORGE_OUTPUT_DIR"] = inputs[ContextKeys.OutputDir].ToString();
        foreach (var name in expert.Inputs ?? [])
        {
            if (!context.TryGetValue(name, out var value) || VerifiedPath(value) is not { } absolute) continue;
            options.Environment["FORGE_INPUT_" + name] = absolute;
            if (name == "source_file") options.Environment["FORGE_SOURCE_FILE"] = absolute;
        }
    }

    private static StepEnvelope ApplyOutput(ExpertDefinition expert, Dictionary<string, object> context, string stdout)
    {
        using var document = ParseOutput(expert, stdout);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(expert.OutputKey, out var outputValue))
            throw new ExpertLoadException($"Expert '{expert.Name}' stdout JSON is missing declared outputKey '{expert.OutputKey}'.");
        var text = outputValue.ValueKind == JsonValueKind.String ? outputValue.GetString() ?? "" : outputValue.GetRawText();
        context[expert.OutputKey] = text;
        context[ContextKeys.Output] = text;
        var reason = root.TryGetProperty("reason", out var rv) ? rv.GetString() : null;
        if (expert.IsJudge) return ApplyVerdict(expert, context, JudgeVerdict.Read(root, text, reason));
        var status = root.TryGetProperty("status", out var sv) ? sv.GetString() : null;
        return new(text, status ?? StepEnvelope.Pass, reason);
    }

    // A failed verdict carries its reason into the next loop attempt as feedback.
    private static StepEnvelope ApplyVerdict(ExpertDefinition expert, Dictionary<string, object> context, StepEnvelope verdict)
    {
        if (verdict.Status == StepEnvelope.Fail)
            context[ContextKeys.Feedback] = !string.IsNullOrWhiteSpace(verdict.Reason) ? verdict.Reason
                : !string.IsNullOrWhiteSpace(expert.OnFail) ? expert.OnFail : "Verification failed.";
        return verdict;
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
}

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(string))]
internal partial class ExecSerializerContext : JsonSerializerContext { }
