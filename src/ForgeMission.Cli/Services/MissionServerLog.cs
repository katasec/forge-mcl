using Microsoft.Extensions.Logging;

namespace ForgeMission.Cli.Services;

// The mission server's diagnostics (forge serve, forge claude, forge connect), written through the
// CLI host's logger: stderr only, Warning and above.
internal static partial class MissionServerLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The aux chat client for provider '{Provider}' could not be built; housekeeping requests get canned replies.")]
    internal static partial void AuxClientUnavailable(this ILogger logger, Exception exception, string provider);
}
