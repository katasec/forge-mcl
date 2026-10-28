using System.CommandLine;

namespace ForgeMission.Cli.Services;

// A TCP port must fall in the valid range; reject an out-of-range --port at parse time rather than
// letting it fail later when something tries to bind it.
internal static class PortOption
{
    private const int Min = 1;
    private const int Max = 65535;

    public static void RejectOutOfRange(Option<int> option) =>
        option.Validators.Add(result =>
        {
            var port = result.GetValueOrDefault<int>();
            if (port < Min || port > Max)
                result.AddError($"--port must be between {Min} and {Max} (got {port}).");
        });

    public static void RejectOutOfRange(Option<int?> option) =>
        option.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int?>() is int port && (port < Min || port > Max))
                result.AddError($"--port must be between {Min} and {Max} (got {port}).");
        });
}
