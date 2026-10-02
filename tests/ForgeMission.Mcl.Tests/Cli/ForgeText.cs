using System.Reflection;

namespace ForgeMission.Tests.Cli;

// Phase 56 Task 4: reflection access to forge.dll's text-image types for the text tests (the CLI is
// an executable the tests do not reference, like the other CLI tests).
internal static class ForgeText
{
    public static readonly Assembly Forge = LoadForge();

    public static readonly int[] AllowedSet = BuildAllowedSet();

    public static Type Type(string name) => Forge.GetType(name, throwOnError: true)!;

    public static object Theme(string name) =>
        Type("ForgeMission.Cli.Tui.ForgeTheme").GetProperty(name, BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;

    public static object Styles(string theme) => Activator.CreateInstance(Type("ForgeMission.Cli.Tui.ForgeStyles"), Theme(theme))!;

    public static object Cell(int width, int height) => Activator.CreateInstance(Type("ForgeMission.Cli.Tui.Graphics.CellSize"), width, height)!;

    public static object Fonts() => Type("ForgeMission.Cli.Tui.Graphics.TextFonts").GetMethod("LoadEmbedded")!.Invoke(null, null)!;

    public static object Glyphs(string resource) =>
        Type("ForgeMission.Cli.Tui.Graphics.GlyphText").GetMethod("LoadEmbedded")!.Invoke(null, [resource])!;

    public static object Art(string theme, int cellWidth, int cellHeight) =>
        Activator.CreateInstance(Type("ForgeMission.Cli.Tui.Graphics.TextArt"),
            Styles(theme).GetType().GetProperty("TextArt")!.GetValue(Styles(theme)), Fonts(), Cell(cellWidth, cellHeight))!;

    public static object Request(string kind, string text, int split = 0) =>
        Activator.CreateInstance(Type("ForgeMission.Cli.Tui.Graphics.TextImageRequest"), Kind(kind), text, split)!;

    public static object Kind(string name) => Enum.Parse(Type("ForgeMission.Cli.Tui.Graphics.TextKind"), name);

    public static bool Allows(string text) =>
        (bool)Type("ForgeMission.Cli.Tui.Graphics.TextArt").GetMethod("Allows")!.Invoke(null, [text])!;

    /// <summary>Calls a method, unwrapping the reflection wrapper from what it throws.</summary>
    public static object? Call(object target, string method, params object?[] args)
    {
        try { return target.GetType().GetMethod(method)!.Invoke(target, args); }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is { } inner) { throw inner; }
    }

    public static T Get<T>(object target, string property) => (T)target.GetType().GetProperty(property)!.GetValue(target)!;

    public static byte[] Resource(string name)
    {
        using var stream = Forge.GetManifestResourceStream(name)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Printable ASCII, Latin-1 letters, then · … → ↵ ⇧ – — ‘ ’ “ ” (Phase 56 Task 4 ruling).</summary>
    private static int[] BuildAllowedSet() =>
    [
        .. Enumerable.Range(0x20, 0x7F - 0x20), .. Enumerable.Range(0xC0, 0xD7 - 0xC0), .. Enumerable.Range(0xD8, 0xF7 - 0xD8),
        .. Enumerable.Range(0xF8, 0x100 - 0xF8), 0xB7, 0x2026, 0x2192, 0x21B5, 0x21E7, 0x2013, 0x2014, 0x2018, 0x2019, 0x201C, 0x201D,
    ];

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
