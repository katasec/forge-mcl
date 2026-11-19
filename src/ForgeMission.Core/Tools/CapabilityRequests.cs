namespace ForgeMission.Core.Tools;

// These are data-only requests: they cannot carry a provider reference or delegate. The dispatcher
// resolves and invokes the provider after approval.
public interface ICapabilityRequest
{
    string Summary { get; }
}

public sealed record InvalidCapabilityRequest(string Summary, string Error) : ICapabilityRequest;

public sealed record ReadFileCapabilityRequest(string FilePath, int Offset, int? Limit) : ICapabilityRequest
{
    public string Summary => $"Read file: {FilePath}";
}

public sealed record EditFileCapabilityRequest(
    string FilePath,
    string OldString,
    string NewString,
    bool ReplaceAll) : ICapabilityRequest
{
    public string Summary => $"Edit file: {FilePath}";
}

public sealed record WriteFileCapabilityRequest(string FilePath, string Content) : ICapabilityRequest
{
    public string Summary => $"Write file: {FilePath} ({Content.Length} characters)";
}

public sealed record ExecuteTerminalCapabilityRequest(string Command) : ICapabilityRequest
{
    public string Summary => $"Run terminal command: {Command}";
}
