using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeMission.Cli.Tui;
using Microsoft.Extensions.Options;

namespace ForgeMission.Cli.Services;

// The user's local settings in ~/.forge/config.json, loaded by the CLI host's JSON configuration
// provider: { "theme": "light" | "dark", "api": { "endpoint": "..." }, "oci": { "endpoint": "..." } }.
// Each consumer binds only what it uses: forge chat the theme (ForgeSettings, validated when chat
// reads it), the hosted commands the API endpoint (ForgeApiSettings), forge run the OCI base for a
// short mission selector (ForgeOciSettings). A missing file or key means the dark theme
// (Phase 56 G10), the hosted API endpoint and ghcr.io/katasec. On first use forge writes the two
// endpoint defaults into the file and keeps whatever is already there (Phase 76 decision 8).
internal sealed class ForgeSettings
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".forge", "config.json");

    public string? Theme { get; set; }

    /// <summary>Adds each missing endpoint default to the config file at <paramref name="path"/>,
    /// creating the file when absent. A present value, a section that is not an object and a file
    /// that does not parse are left alone (the host's JSON load reports the last). Stops forge with
    /// one error line when the file cannot be written: forge needs its config file.</summary>
    public static void WriteDefaults(string path)
    {
        try
        {
            if (WithDefaults(path) is { } config) Save(config, path);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            CliDiagnostics.Die($"Cannot update {path}: {failure.Message}");
        }
    }

    /// <summary>The file's settings with the missing defaults added, or null when nothing changes.</summary>
    private static JsonObject? WithDefaults(string path)
    {
        var config = File.Exists(path) ? Parse(File.ReadAllText(path)) : new JsonObject();
        return config is not null && AddDefaults(config) ? config : null;
    }

    private static JsonObject? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text, new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Adds each missing endpoint; true when it added one.</summary>
    // | not ||: both sections must be filled.
    private static bool AddDefaults(JsonObject config) =>
        AddEndpoint(config, "api", ForgeApiSettings.DefaultEndpoint) | AddEndpoint(config, "oci", ForgeOciSettings.DefaultEndpoint);

    private static bool AddEndpoint(JsonObject config, string section, string endpoint)
    {
        if (!config.TryGetPropertyValue(section, out var node)) config[section] = node = new JsonObject();
        if (node is not JsonObject settings || settings.ContainsKey("endpoint")) return false;
        settings["endpoint"] = endpoint;
        return true;
    }

    /// <summary>Writes beside the file (same volume) and renames over it, so a failed write leaves
    /// the old file intact and no temp file behind.</summary>
    private static void Save(JsonObject config, string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            using (var stream = File.Create(temp))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                config.WriteTo(writer);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

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

/// <summary>The config file's <c>oci</c> section: the registry base a short mission selector
/// resolves against. A fully qualified reference names its own registry.</summary>
internal sealed class ForgeOciSettings
{
    internal const string Section = "Oci";
    internal const string DefaultEndpoint = "ghcr.io/katasec";

    public string Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>Trims whitespace from the endpoint; blank means the default.</summary>
    public static void NormalizeEndpoint(ForgeOciSettings settings) =>
        settings.Endpoint = settings.Endpoint?.Trim() is { Length: > 0 } endpoint
            ? endpoint
            : DefaultEndpoint;
}
