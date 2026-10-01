using System.Reflection;

namespace ForgeMission.Tests.Cli;

// forge chat (53.6): the theme named in ~/.forge/config.json. Missing file or key → dark (56 G10); "light"
// and "dark" pick those themes; anything else stops forge chat with the valid names. Read through
// reflection like the other CLI tests (the test project does not reference the forge assembly).
public sealed class ForgeConfigTests : IDisposable
{
    private static readonly Assembly Forge = LoadForge();
    private static readonly MethodInfo ReadTheme = Forge.GetType("ForgeMission.Cli.ForgeConfig", throwOnError: true)!
        .GetMethod("ReadTheme", BindingFlags.Static | BindingFlags.Public)!;
    private static readonly Type ThemeType = Forge.GetType("ForgeMission.Cli.Tui.ForgeTheme", throwOnError: true)!;

    private readonly string _dir = Directory.CreateTempSubdirectory("forge-config-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_missing_file_is_the_dark_theme()
    {
        Assert.Same(Theme("Dark"), Read(Path.Combine(_dir, "config.json")));
    }

    [Fact]
    public void A_file_without_a_theme_is_the_dark_theme()
    {
        Assert.Same(Theme("Dark"), Read(Write("""{ "other": 1 }""")));
    }

    [Theory]
    [InlineData("light", "Light")]
    [InlineData("dark", "Dark")]
    public void A_named_theme_is_that_theme(string name, string theme)
    {
        Assert.Same(Theme(theme), Read(Write($$"""{ "theme": "{{name}}" }""")));
    }

    [Theory]
    [InlineData("solarized")]
    [InlineData("Dark")]
    public void An_unknown_theme_stops_with_the_valid_names(string name)
    {
        var error = ReadFailure(Write($$"""{ "theme": "{{name}}" }"""));

        Assert.Contains($"Unknown theme \"{name}\"", error.Message);
        Assert.Contains("Valid themes: light, dark.", error.Message);
    }

    [Fact]
    public void Invalid_json_stops_with_a_clear_error()
    {
        Assert.Contains("is not valid JSON", ReadFailure(Write("{ theme: ")).Message);
    }

    private string Write(string json)
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static object Read(string path) => ReadTheme.Invoke(null, [path])!;

    private static Exception ReadFailure(string path)
    {
        var failure = Assert.Throws<TargetInvocationException>(() => Read(path)).InnerException!;
        Assert.Equal("ForgeConfigException", failure.GetType().Name);
        return failure;
    }

    private static object Theme(string name) =>
        ThemeType.GetProperty(name, BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;

    private static Assembly LoadForge()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "ForgeMission.Cli", "bin", "Debug", "net10.0", "forge.dll");
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate built forge.dll for CLI reflection tests.");
    }
}
