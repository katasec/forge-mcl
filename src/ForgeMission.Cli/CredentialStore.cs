using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeMission.Cli;

/// <summary>CLI-owned persistence for platform, provider, and registry credentials.</summary>
public static class CredentialStore
{
    private static string CredentialsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".forge", "credentials.json");

    public static string? GetToken(string registry)
    {
        var environmentToken = Environment.GetEnvironmentVariable("FORGE_REGISTRY_TOKEN");
        if (!string.IsNullOrWhiteSpace(environmentToken)) return environmentToken;

        return Read().Credentials.TryGetValue(registry, out var credential) && !string.IsNullOrWhiteSpace(credential.Token)
            ? credential.Token
            : null;
    }

    public static void SaveToken(string registry, string token) =>
        Mutate(credentials => credentials.Credentials[registry] = new RegistryCredential { Token = token });

    public static ProviderCredential? GetProvider(string provider) =>
        Read().Providers.TryGetValue(provider, out var credential) && !string.IsNullOrWhiteSpace(credential.ApiKey)
            ? credential
            : null;

    public static void SaveProvider(string provider, string apiKey) =>
        Mutate(credentials => credentials.Providers[provider] = new ProviderCredential { ApiKey = apiKey });

    public static PlatformCredential? GetPlatform() =>
        Read().Platform is { Key.Length: > 0 } platform ? platform : null;

    public static void SavePlatform(PlatformCredential platform) =>
        Mutate(credentials => credentials.Platform = platform);

    public static void ClearPlatform() =>
        Mutate(credentials => credentials.Platform = null);

    private static ForgeCredentials Read()
    {
        if (!File.Exists(CredentialsPath)) return new ForgeCredentials();

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(CredentialsPath), CredentialStoreJsonContext.Default.ForgeCredentials)
                ?? new ForgeCredentials();
        }
        catch (JsonException) { return new ForgeCredentials(); }
        catch (IOException) { return new ForgeCredentials(); }
        catch (UnauthorizedAccessException) { return new ForgeCredentials(); }
    }

    private static void Mutate(Action<ForgeCredentials> change)
    {
        var directory = Path.GetDirectoryName(CredentialsPath)!;
        Directory.CreateDirectory(directory);
        var credentials = Read();
        change(credentials);
        File.WriteAllText(CredentialsPath,
            JsonSerializer.Serialize(credentials, CredentialStoreJsonContext.Default.ForgeCredentials));
    }
}

public sealed class ProviderCredential
{
    public string ApiKey { get; set; } = "";
}

/// <summary>CLI-owned platform sign-in data used to compose authenticated hosted requests.</summary>
public sealed class PlatformCredential
{
    public string Key { get; set; } = "";
    public string User { get; set; } = "";
    public string Endpoint { get; set; } = "";
}

internal sealed class ForgeCredentials
{
    public Dictionary<string, RegistryCredential> Credentials { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ProviderCredential> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public PlatformCredential? Platform { get; set; }
}

internal sealed class RegistryCredential
{
    public string Token { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ForgeCredentials))]
[JsonSerializable(typeof(PlatformCredential))]
internal partial class CredentialStoreJsonContext : JsonSerializerContext { }
