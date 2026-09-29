using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeMission.Cli.Tui;

namespace ForgeMission.Cli;

// forge chat (53.6): the user's local settings in ~/.forge/config.json. Today it holds one key,
// the TUI theme: { "theme": "light" | "dark" }. A missing file or key means light; anything else
// stops forge chat with an error naming the valid themes. Read once at startup.
internal static class ForgeConfig
{
    private static readonly string[] ValidThemes = ["light", "dark"];

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".forge", "config.json");

    /// <summary>The theme named in the config file at <paramref name="path"/>.</summary>
    /// <exception cref="ForgeConfigException">The file cannot be read, is not valid JSON, or names
    /// an unknown theme.</exception>
    public static ForgeTheme ReadTheme(string path)
    {
        var name = ReadFile(path)?.Theme;
        return name switch
        {
            null or "light" => ForgeTheme.Light,
            "dark" => ForgeTheme.Dark,
            _ => throw new ForgeConfigException(
                $"Unknown theme \"{name}\" in {path}. Valid themes: {string.Join(", ", ValidThemes)}."),
        };
    }

    private static ForgeConfigFile? ReadFile(string path)
    {
        string json;
        try
        {
            if (!File.Exists(path)) return null;
            json = File.ReadAllText(path);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new ForgeConfigException($"Could not read {path}: {failure.Message}");
        }

        try
        {
            return JsonSerializer.Deserialize(json, ForgeConfigJsonContext.Default.ForgeConfigFile);
        }
        catch (JsonException failure)
        {
            throw new ForgeConfigException($"{path} is not valid JSON: {failure.Message}");
        }
    }
}

internal sealed class ForgeConfigException(string message) : Exception(message);

internal sealed record ForgeConfigFile(string? Theme);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ForgeConfigFile))]
internal partial class ForgeConfigJsonContext : JsonSerializerContext { }
