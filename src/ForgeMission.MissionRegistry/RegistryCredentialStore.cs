using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeMission.MissionRegistry;

internal static class RegistryCredentialStore
{
    private static readonly string CredentialsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".forge", "credentials.json");

    public static string? GetToken(string registry)
    {
        var environmentToken = Environment.GetEnvironmentVariable("FORGE_REGISTRY_TOKEN");
        if (!string.IsNullOrWhiteSpace(environmentToken))
            return environmentToken;

        if (!File.Exists(CredentialsPath))
            return null;

        try
        {
            var credentials = JsonSerializer.Deserialize(
                File.ReadAllText(CredentialsPath),
                RegistryCredentialsJsonContext.Default.RegistryCredentials);
            return credentials?.Credentials.TryGetValue(registry, out var credential) == true
                && !string.IsNullOrWhiteSpace(credential.Token)
                ? credential.Token
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed class RegistryCredentials
{
    public Dictionary<string, RegistryCredential> Credentials { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class RegistryCredential
{
    public string Token { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RegistryCredentials))]
internal partial class RegistryCredentialsJsonContext : JsonSerializerContext;
