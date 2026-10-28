using System.CommandLine;
using ForgeMission.Core.Resolution;
using Microsoft.Extensions.Options;

namespace ForgeMission.Cli.Commands;

// Authentication commands: OCI-registry credentials under `forge registry login` (42.5), and
// platform sign-in / identity under `forge login`, `forge whoami` and `forge logout`. Registry
// credentials are stored by CredentialStore; platform sign-in (loopback + PKCE) lives in
// PlatformLogin. This surface owns only the command wiring.
internal static class AuthCommands
{
    // ---------------------------------------------------------------------------
    // forge registry login  (42.5: OCI-registry credentials move under `forge registry`;
    // the top-level `forge login` becomes platform sign-in)

    internal static Command BuildRegistryCommand()
    {
        var cmd = new Command("registry", "OCI registry operations");
        cmd.Add(BuildRegistryLoginCommand());
        return cmd;
    }

    static Command BuildRegistryLoginCommand()
    {
        var registryArg = new Argument<string>("registry") { Description = "Registry host (e.g. ghcr.io)" };
        var tokenOpt    = new Option<string>("--token") { Description = "Credential token (e.g. GitHub PAT)", Required = true };

        var cmd = new Command("login", "Save registry credentials to ~/.forge/credentials.json");
        cmd.Add(registryArg);
        cmd.Add(tokenOpt);

        cmd.SetCancellableAction((result, _) =>
        {
            SaveRegistryCredential(result.GetValue(registryArg)!, result.GetValue(tokenOpt)!);
            return Task.CompletedTask;
        });
        return cmd;
    }

    static void SaveRegistryCredential(string registry, string token)
    {
        CredentialStore.SaveToken(registry, token);
        Console.WriteLine($"Credentials saved for {registry}");
    }

    // forge login = platform sign-in (loopback + PKCE → platform key).
    // OCI-registry credentials live under `forge registry login`.
    internal static Command BuildLoginCommand(IOptions<PlatformLoginOptions> login, IHttpClientFactory clients)
    {
        var cmd = new Command("login", "Sign in to Forge (browser) and store a platform key");
        cmd.SetCancellableAction((_, cancellationToken) => PlatformLogin.RunAsync(login, clients, cancellationToken));
        return cmd;
    }

    internal static Command BuildWhoamiCommand(IHttpClientFactory clients)
    {
        var cmd = new Command("whoami", "Show the signed-in Forge user and credit balance");
        cmd.SetCancellableAction((_, cancellationToken) => PlatformLogin.WhoAmIAsync(clients, cancellationToken));
        return cmd;
    }

    internal static Command BuildLogoutCommand()
    {
        var cmd = new Command("logout", "Sign out of Forge (remove the stored platform key)");
        cmd.SetCancellableAction((_, _) => Task.FromResult(PlatformLogin.Logout()));
        return cmd;
    }
}
