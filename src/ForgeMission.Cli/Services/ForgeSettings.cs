using ForgeMission.Cli.Tui;
using Microsoft.Extensions.Options;

namespace ForgeMission.Cli.Services;

// The user's local settings in ~/.forge/config.json, loaded by the CLI host's JSON configuration
// provider: { "theme": "light" | "dark", "api": { "endpoint": "..." } }. Each consumer binds only
// what it uses: forge chat the theme (ForgeSettings, validated when chat reads it), the hosted
// commands the API endpoint (ForgeApiSettings). A missing file or key means the dark theme
// (Phase 56 G10) and the hosted API endpoint.
internal sealed class ForgeSettings
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".forge", "config.json");

    public string? Theme { get; set; }

    /// <summary>The theme named in the config file, once <see cref="ForgeSettingsValidator"/> has
    /// admitted it (light, dark or none).</summary>
    public ForgeTheme SelectedTheme() => Theme == "light" ? ForgeTheme.Light : ForgeTheme.Dark;
}

/// <summary>Admits only a known theme name, or none.</summary>
internal sealed class ForgeSettingsValidator : IValidateOptions<ForgeSettings>
{
    private static readonly string[] ValidThemes = ["light", "dark"];

    public ValidateOptionsResult Validate(string? name, ForgeSettings options) =>
        options.Theme is null || ValidThemes.Contains(options.Theme)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"Unknown theme \"{options.Theme}\" in {ForgeSettings.DefaultPath}. Valid themes: {string.Join(", ", ValidThemes)}.");
}

/// <summary>The config file's <c>api</c> section.</summary>
internal sealed class ForgeApiSettings
{
    internal const string Section = "Api";
    internal const string DefaultEndpoint = "https://api.forge.katasec.com";

    public string Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>Trims whitespace and trailing slashes from the endpoint; blank means the default.</summary>
    public static void NormalizeEndpoint(ForgeApiSettings settings) =>
        settings.Endpoint = settings.Endpoint?.Trim().TrimEnd('/') is { Length: > 0 } endpoint
            ? endpoint
            : DefaultEndpoint;
}
