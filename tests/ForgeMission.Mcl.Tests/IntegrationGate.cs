namespace ForgeMission.Tests;

// Live-API tests cost money and need the network, so they run only on an explicit opt-in,
// never just because a provider key happens to be exported in the shell.
internal static class IntegrationGate
{
    public const string OptInVariable = "FORGE_RUN_INTEGRATION";

    public static string SkipUnlessOptedIn(string apiKeyVariable)
    {
        Skip.IfNot(Environment.GetEnvironmentVariable(OptInVariable) == "1",
            $"{OptInVariable}=1 not set — skipping integration test");
        var key = Environment.GetEnvironmentVariable(apiKeyVariable);
        Skip.If(string.IsNullOrWhiteSpace(key), $"{apiKeyVariable} not set — skipping integration test");
        return key!;
    }
}
