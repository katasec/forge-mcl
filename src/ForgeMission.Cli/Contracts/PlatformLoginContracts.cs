using System.Text.Json.Serialization;

namespace ForgeMission.Cli.Contracts;

// --- Wire types ------------------------------------------------------------------------------

internal sealed class TokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("id_token")] public string? IdToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
}

internal sealed class IdTokenClaims
{
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("preferred_username")] public string? PreferredUsername { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("sub")] public string? Sub { get; set; }
}

// Response from the issuance endpoint (ForgeUI POST /platform/keys), camelCase on the wire.
internal sealed class IssueKeyResponse
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("balanceMicroUsd")] public long BalanceMicroUsd { get; set; }
}

// Response from GET /me (ForgeUI), camelCase on the wire.
internal sealed class MeResponse
{
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("balanceMicroUsd")] public long BalanceMicroUsd { get; set; }
}

[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(IdTokenClaims))]
[JsonSerializable(typeof(IssueKeyResponse))]
[JsonSerializable(typeof(MeResponse))]
internal partial class PlatformLoginJsonContext : JsonSerializerContext { }
