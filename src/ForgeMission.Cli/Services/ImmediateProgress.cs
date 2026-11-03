namespace ForgeMission.Cli.Services;

/// <summary>Reports each message at once, on the reporting thread. <see cref="Progress{T}"/> would
/// post it to the thread pool, so a report could land after the line that follows it.</summary>
internal sealed class ImmediateProgress(Action<string> report) : IProgress<string>
{
    public void Report(string value) => report(value);
}
