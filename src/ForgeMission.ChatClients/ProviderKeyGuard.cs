using ForgeMission.Core.Manifest;

namespace ForgeMission.ChatClients;

// A provider key is a credential issued by one vendor; sending it to any other vendor hands it to a
// third party. Provider resolution falls back to "openai" when nothing names a provider, so a mission
// whose MCL_API_KEY held an Anthropic credential sent that credential to api.openai.com (R16). The
// guard recognises a key's issuer from its documented prefix and refuses to build a client that would
// send it to a different provider. A key with no recognisable prefix (Azure, gateways, test stubs)
// passes: only a positive mismatch is refused, and the error never echoes the key.
internal static class ProviderKeyGuard
{
    // Most specific prefix first: an Anthropic key also starts with "sk-".
    private static readonly (string Prefix, string Issuer, string Label, string Domain)[] Issuers =
    [
        ("sk-ant-", "anthropic", "an Anthropic", "anthropic.com"),
        ("xai-",    "xai",       "an xAI",       "x.ai"),
        ("sk-",     "openai",    "an OpenAI",    "openai.com"),
    ];

    // Providers whose client sends profile.ApiKey on the wire (ollama sends a fixed placeholder).
    private static readonly HashSet<string> KeySendingProviders = ["openai", "azure", "anthropic", "xai"];

    public static void EnsureKeyMatchesProvider(ProviderProfile profile)
    {
        var provider = profile.Provider.ToLowerInvariant();
        if (!KeySendingProviders.Contains(provider) || string.IsNullOrWhiteSpace(profile.ApiKey))
            return;

        var key = profile.ApiKey.Trim();
        var issuer = Issuers.FirstOrDefault(i => key.StartsWith(i.Prefix, StringComparison.Ordinal));
        if (issuer.Issuer is null || issuer.Issuer == provider || EndpointBelongsTo(profile.Endpoint, issuer.Domain))
            return;

        throw new InvalidOperationException(
            $"Refusing to send {issuer.Label} key ({issuer.Prefix}…) to provider '{profile.Provider}'. " +
            $"A key is only sent to the provider that issued it: set provider = \"{issuer.Issuer}\" " +
            $"(or MCL_PROVIDER={issuer.Issuer}), or supply a {profile.Provider} key.");
    }

    // An OpenAI-compatible client pointed at the issuer's own API (e.g. provider "openai" with
    // endpoint https://api.anthropic.com/v1/) still sends the key only to its issuer.
    private static bool EndpointBelongsTo(string? endpoint, string domain) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
}
