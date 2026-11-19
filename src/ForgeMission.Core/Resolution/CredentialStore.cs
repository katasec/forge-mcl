using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeMission.Core.Resolution;

public class ForgeCredentials
{
    public Dictionary<string, RegistryCredential> Credentials { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ProviderCredential> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Platform sign-in: the forge platform key + who/where it came from.</summary>
    public PlatformCredential? Platform { get; set; }
}

public class RegistryCredential
{
    public string Token { get; set; } = "";
}

public class ProviderCredential
{
    public string ApiKey { get; set; } = "";
}

public class PlatformCredential
{
    /// <summary>The platform key (fg_live_…) — the bearer token for hosted forge endpoints.</summary>
    public string Key { get; set; } = "";

    /// <summary>Display label of the signed-in user (email), informational only.</summary>
    public string User { get; set; } = "";

    /// <summary>Base URL of the platform that issued the key (e.g. https://forge.katasec.com).</summary>
    public string Endpoint { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ForgeCredentials))]
[JsonSerializable(typeof(RegistryCredential))]
[JsonSerializable(typeof(ProviderCredential))]
[JsonSerializable(typeof(PlatformCredential))]
internal partial class CredentialsJsonContext : JsonSerializerContext { }

public static class CredentialStore
{
    private static string CredentialsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".forge", "credentials.json");

    public static string? GetToken(string registry)
    {
        // Env var takes precedence for simple CI setups
        var envToken = Environment.GetEnvironmentVariable("FORGE_REGISTRY_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken)) return envToken;

        // A null registry has always resolved to "no token" rather than throwing.
        if (registry is null) return null;

        var creds = ReadCredentials();
        if (creds?.Credentials?.TryGetValue(registry, out var cred) != true) return null;

        return string.IsNullOrWhiteSpace(cred?.Token) ? null : cred.Token;
    }

    public static void SaveToken(string registry, string token) =>
        Mutate(creds => creds.Credentials[registry] = new RegistryCredential { Token = token });

    // --- Platform credential ---------------------------------------------------------------

    public static PlatformCredential? GetPlatform()
    {
        var creds = ReadCredentials();
        return string.IsNullOrWhiteSpace(creds?.Platform?.Key) ? null : creds!.Platform;
    }

    public static void SavePlatform(PlatformCredential platform) =>
        Mutate(creds => creds.Platform = platform);

    public static void ClearPlatform() =>
        Mutate(creds => creds.Platform = null);

    // --- Shared read-modify-write -----------------------------------------------------------

    private static void Mutate(Action<ForgeCredentials> change)
    {
        var dir = Path.GetDirectoryName(CredentialsPath)!;
        Directory.CreateDirectory(dir);

        // The credentials directory holds secrets — keep it owner-only on Unix.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var existing = ReadCredentialsForUpdate();
        change(existing);
        WriteSecretAtomic(CredentialsPath,
            JsonSerializer.Serialize(existing, CredentialsJsonContext.Default.ForgeCredentials));
    }

    // Missing file — start empty. A file that is not valid JSON is refused rather than treated as
    // empty: writing over it would silently lose every credential it holds.
    private static ForgeCredentials ReadCredentialsForUpdate()
    {
        if (!File.Exists(CredentialsPath)) return new();

        try
        {
            var json = File.ReadAllText(CredentialsPath);
            return JsonSerializer.Deserialize(json, CredentialsJsonContext.Default.ForgeCredentials) ?? new();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{CredentialsPath} is not valid JSON; fix or delete it.");
        }
    }

    // Reads: a missing or unreadable/corrupt credentials file is treated as missing.
    private static ForgeCredentials? ReadCredentials()
    {
        if (!File.Exists(CredentialsPath)) return null;

        try
        {
            return ReadCredentialsForUpdate();
        }
        catch
        {
            return null;
        }
    }

    // Atomic owner-only write: stage into a temp file in the SAME directory, set
    // owner read/write permissions before it reaches the destination (so the secret
    // is never briefly readable by others), then atomically replace/move into place.
    private static void WriteSecretAtomic(string destination, string contents)
    {
        var dir = Path.GetDirectoryName(destination)!;
        var temp = Path.Combine(dir, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, contents);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            if (File.Exists(destination))
                File.Replace(temp, destination, null);
            else
                File.Move(temp, destination);
        }
        catch
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best effort cleanup */ }
            }
            throw;
        }
    }
}
