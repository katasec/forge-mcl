using ForgeMission.Cli.Tui;

namespace ForgeMission.Cli;

// The user's local settings in ~/.forge/config.json, loaded by the CLI host's JSON configuration
// provider and bound here: { "theme": "light" | "dark", "api": { "endpoint": "..." } }. A missing
// file or key means the dark theme (Phase 56 G10) and the hosted API endpoint.
internal sealed class ForgeSettings
{
    private static readonly string[] ValidThemes = ["light", "dark"];

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".forge", "config.json");

    public string? Theme { get; set; }

    public ForgeApiSettings Api { get; set; } = new();

    /// <summary>The theme named in the config file.</summary>
    /// <exception cref="ForgeConfigException">The file names an unknown theme.</exception>
    public ForgeTheme SelectedTheme() => Theme switch
    {
        "light" => ForgeTheme.Light,
        null or "dark" => ForgeTheme.Dark,
        _ => throw new ForgeConfigException(
            $"Unknown theme \"{Theme}\" in {DefaultPath}. Valid themes: {string.Join(", ", ValidThemes)}."),
    };

    /// <summary>Trims whitespace and trailing slashes from the endpoint; blank means the default.</summary>
    public static void NormalizeEndpoint(ForgeSettings settings) =>
        settings.Api.Endpoint = settings.Api.Endpoint?.Trim().TrimEnd('/') is { Length: > 0 } endpoint
            ? endpoint
            : ForgeApiSettings.DefaultEndpoint;
}

internal sealed class ForgeApiSettings
{
    internal const string DefaultEndpoint = "https://api.forge.katasec.com";

    public string Endpoint { get; set; } = DefaultEndpoint;
}

internal sealed class ForgeConfigException(string message) : Exception(message);
