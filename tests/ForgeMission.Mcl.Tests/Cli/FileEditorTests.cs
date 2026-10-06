using System.Reflection;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.CodeEditor.TextMateSharp;
using XenoAtom.Terminal.UI.Rendering;

namespace ForgeMission.Tests.Cli;

// forge chat TUI (/edit spike): the /edit command, path resolution, open/save, the Esc guard
// (EditFile), the editor's colours and text (FileEditor, CodeColours), its keys through a real
// XenoAtom app, and the screen swap (ChatScreen). Read through reflection like the other CLI tests.
[Collection(XenoAtomUiCollection.Name)]
public sealed class FileEditorTests : IDisposable
{
    private static readonly Assembly Forge = LoadForge();
    private static readonly Type EditFileType = Type("ForgeMission.Cli.Tui.EditFile");
    private static readonly Type FileEditorType = Type("ForgeMission.Cli.Tui.FileEditor");
    // VS Code Dark+ keyword blue (#569CD6) as a truecolor foreground: the screen theme is Dark.
    private const string DarkPlusKeyword = "38;2;86;156;214";

    private readonly string _dir = Directory.CreateTempSubdirectory("forge-edit-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ── Command ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/edit")]
    [InlineData("/edit   ")]
    [InlineData("  /edit\n")]
    public void Edit_without_a_path_is_the_usage(string line)
    {
        var command = Parse(line);
        Assert.NotNull(command);
        Assert.Null(PathOf(command));
    }

    [Theory]
    [InlineData("/edit a.go", "a.go")]
    [InlineData(" /edit a.go ", "a.go")]
    [InlineData("/edit  my file.txt ", "my file.txt")]
    [InlineData("/edit ~/notes.md", "~/notes.md")]
    public void Edit_with_a_path_names_it(string line, string path) => Assert.Equal(path, PathOf(Parse(line)));

    [Theory]
    [InlineData("hello")]
    [InlineData("/editor x")]
    [InlineData("/editx")]
    [InlineData("please /edit a.go")]
    public void Other_lines_are_messages(string line) => Assert.Null(Parse(line));

    // ── Paths ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_path_resolves_against_the_start_folder_and_home()
    {
        Assert.Equal(Path.Combine(_dir, "src", "a.go"), Resolve("src/a.go", _dir, "/home/u"));
        Assert.Equal("/etc/hosts", Resolve("/etc/hosts", _dir, "/home/u"));
        Assert.Equal("/home/u", Resolve("~", _dir, "/home/u"));
        Assert.Equal("/home/u/notes.md", Resolve("~/notes.md", _dir, "/home/u"));
        Assert.Equal(Path.Combine(_dir, "~x"), Resolve("~x", _dir, "/home/u"));
    }

    // ── Open and save ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_existing_file_opens_with_its_text_and_path()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "hello");
        var file = Open("a.txt");
        Assert.Equal("hello", Get<string>(file, "Text"));
        Assert.False(Get<bool>(file, "IsNew"));
        Assert.Equal("a.txt", Get<string>(file, "Header"));
    }

    [Fact]
    public void A_missing_file_opens_empty_and_new()
    {
        var file = Open("b.txt");
        Assert.Equal("", Get<string>(file, "Text"));
        Assert.True(Get<bool>(file, "IsNew"));
        Assert.Equal("b.txt · new", Get<string>(file, "Header"));
        Assert.False(File.Exists(Path.Combine(_dir, "b.txt")));
    }

    [Fact]
    public void A_folder_does_not_open()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        var failure = Assert.Throws<TargetInvocationException>(() => Open("sub"));
        Assert.IsType<IOException>(failure.InnerException);
    }

    [Fact]
    public void A_save_into_a_missing_folder_fails_and_creates_nothing()
    {
        var file = Open("missing/c.txt");
        var message = Save(file, "x");
        Assert.Equal($"cannot save: folder {Path.Combine(_dir, "missing")} does not exist", message);
        Assert.False(Directory.Exists(Path.Combine(_dir, "missing")));
        Assert.True(Get<bool>(file, "IsNew"));
    }

    [Fact]
    public void The_first_save_creates_the_file_and_later_saves_overwrite()
    {
        var file = Open("d.txt");
        Assert.Equal("saved", Save(file, "one"));
        Assert.Equal("one", File.ReadAllText(Path.Combine(_dir, "d.txt")));
        Assert.Equal("d.txt", Get<string>(file, "Header"));
        Assert.Equal("saved", Save(file, "two"));
        Assert.Equal("two", File.ReadAllText(Path.Combine(_dir, "d.txt")));
    }

    // ── Esc ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Esc_with_no_changes_closes() => Assert.Equal("Close", Escape(Open("e.txt"), ""));

    [Fact]
    public void Esc_with_changes_warns_then_discards()
    {
        var file = Open("e.txt");
        Assert.Equal("Warn", Escape(file, "x"));
        Assert.Equal("Close", Escape(file, "x"));
    }

    [Fact]
    public void An_edit_after_the_warning_warns_again()
    {
        var file = Open("e.txt");
        Assert.Equal("Warn", Escape(file, "x"));
        EditFileType.GetMethod("Edited")!.Invoke(file, null);
        Assert.Equal("Warn", Escape(file, "xy"));
    }

    [Fact]
    public void After_a_save_Esc_closes_and_a_later_change_warns()
    {
        var file = Open("e.txt");
        Save(file, "x");
        Assert.Equal("Close", Escape(file, "x"));
        Assert.Equal("Warn", Escape(file, "xy"));
    }

    // ── Editor ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_go_file_gets_go_colours_and_an_unknown_extension_none()
    {
        var go = EditorHighlighter("main.go");
        Assert.Equal("source.go", Assert.IsType<TextMateCodeEditorSyntaxHighlighter>(go).ScopeName);
        Assert.Null(EditorHighlighter("notes.unknownext"));
    }

    [Fact]
    public void The_editor_keeps_crlf_line_endings()
    {
        File.WriteAllText(Path.Combine(_dir, "w.txt"), "a\r\nb\r\n");
        var editor = NewEditor(Open("w.txt"), () => { });
        Assert.Equal("a\r\nb\r\n", EditorText(editor));
    }

    [Fact]
    public async Task A_go_file_is_coloured_on_first_paint_without_input()
    {
        File.WriteAllText(Path.Combine(_dir, "main.go"), "package main\n\nimport \"fmt\"\n\nfunc main() {\n\tfmt.Println(\"hi\")\n}\n");
        var output = new StringWriter();
        await RunKeys(NewEditor(Open("main.go"), () => { }), () => false, [], output: output);
        Assert.Contains(DarkPlusKeyword, output.ToString());
    }

    [Fact]
    public void A_long_header_keeps_its_end_and_starts_with_an_ellipsis()
    {
        var path = "/private/tmp/some/deeply/nested/folder/with/a/long/name/notes.txt";
        var editor = NewEditor(Open(path), () => { });
        var view = (Visual)FileEditorType.GetProperty("View")!.GetValue(editor)!;
        var header = Plain(VisualSnapshotRenderer.Render(view, 30, 6).ToMarkupLines()[0]).TrimEnd();
        Assert.StartsWith("…", header);
        Assert.EndsWith("notes.txt · new", header);
        Assert.True(header.Length <= 30);
    }

    [Fact]
    public async Task Ctrl_s_saves_and_esc_closes_through_the_app()
    {
        var closed = false;
        var editor = NewEditor(Open("k.txt"), () => closed = true);
        await RunKeys(editor, () => closed, [[Text("hi"), Ctrl(TerminalChar.CtrlS), Key(TerminalKey.Escape)]]);
        Assert.True(closed);
        Assert.Equal("hi", File.ReadAllText(Path.Combine(_dir, "k.txt")));
    }

    [Fact]
    public async Task Esc_with_unsaved_changes_needs_a_second_esc()
    {
        var closes = 0;
        var closesAfterFirstEsc = -1;
        var textAfterFirstEsc = "";
        var editor = NewEditor(Open("u.txt"), () => closes++);
        await RunKeys(editor, () => closes > 0, [[Text("hi"), Key(TerminalKey.Escape)], [Key(TerminalKey.Escape)]], () =>
        {
            closesAfterFirstEsc = closes;
            textAfterFirstEsc = EditorText(editor);
        });
        Assert.Equal("hi", textAfterFirstEsc);
        Assert.Equal(0, closesAfterFirstEsc);
        Assert.Equal(1, closes);
        Assert.False(File.Exists(Path.Combine(_dir, "u.txt")));
    }

    [Fact]
    public void The_screen_swaps_the_transcript_and_key_bar_for_the_editor_and_back()
    {
        var (screen, screenType, root) = NewScreen();
        var editor = NewEditor(Open("s.txt"), () => { });
        var view = (Visual)FileEditorType.GetProperty("View")!.GetValue(editor)!;

        screenType.GetMethod("ShowEditor")!.Invoke(screen, [view, true]);
        Assert.True((bool)screenType.GetProperty("Editing")!.GetValue(screen)!);
        var editing = RenderText(root);
        Assert.Contains("s.txt · new", editing);
        Assert.Contains("save", editing);
        Assert.DoesNotContain("newline", editing);

        screenType.GetMethod("ShowChat")!.Invoke(screen, null);
        Assert.False((bool)screenType.GetProperty("Editing")!.GetValue(screen)!);
        var chat = RenderText(root);
        Assert.Contains("newline", chat);
        Assert.DoesNotContain("s.txt", chat);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private static object? Parse(string line) =>
        EditFileType.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [line]);

    private static string? PathOf(object? command) => (string?)command!.GetType().GetProperty("Path")!.GetValue(command);

    private static string Resolve(string path, string cwd, string home) =>
        (string)EditFileType.GetMethod("Resolve", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [path, cwd, home])!;

    private object Open(string path) =>
        EditFileType.GetMethod("Open", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [path, _dir, "/home/u"])!;

    private static string Save(object file, string text) => (string)EditFileType.GetMethod("Save")!.Invoke(file, [text])!;

    private static string Escape(object file, string text) => EditFileType.GetMethod("Escape")!.Invoke(file, [text])!.ToString()!;

    private static T Get<T>(object target, string property) => (T)target.GetType().GetProperty(property)!.GetValue(target)!;

    private static object? EditorHighlighter(string fileName) =>
        Type("ForgeMission.Cli.Tui.Graphics.CodeColours").GetMethod("EditorHighlighter")!.Invoke(null, [fileName]);

    private static object Styles() => Activator.CreateInstance(Type("ForgeMission.Cli.Tui.ForgeStyles"),
        Type("ForgeMission.Cli.Tui.ForgeTheme").GetProperty("Dark", BindingFlags.Static | BindingFlags.Public)!.GetValue(null))!;

    private static object NewEditor(object file, Action close) => Activator.CreateInstance(FileEditorType, file, Styles(), close, Activator.CreateInstance(Type("ForgeMission.Cli.Tui.TextInteraction"), nonPublic: true)!)!;

    private static string EditorText(object editor) =>
        (string)FileEditorType.GetProperty("Text", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;

    private static (object Screen, Type ScreenType, Visual Root) NewScreen()
    {
        var screenType = Type("ForgeMission.Cli.Tui.ChatScreen");
        var styles = Styles();
        var header = Activator.CreateInstance(Type("ForgeMission.Cli.Tui.ChatHeader"), "chat", "Chat", 1, "anthropic", "ameer")!;
        var screen = Activator.CreateInstance(screenType, [header, styles])!;
        var cell = Activator.CreateInstance(Type("ForgeMission.Cli.Tui.Graphics.CellSize"), 19, 42)!;
        var tiles = Type("ForgeMission.Cli.Tui.ScreenTiles").GetMethod("Create")!.Invoke(null, [styles, cell]);
        screenType.GetMethod("UseImages")!.Invoke(screen, [tiles, ChatScreenTileTests.TextImagesFor(styles, cell)]);
        return (screen, screenType, (Visual)screenType.GetProperty("Root")!.GetValue(screen)!);
    }

    /// <summary>A snapshot markup line without its style tags.</summary>
    private static string Plain(string markup) =>
        System.Text.RegularExpressions.Regex.Replace(markup, @"\[(?!\[)[^\]]*\]", "").Replace("[[", "[").Replace("]]", "]");

    private static string RenderText(Visual root) => string.Join("\n", VisualSnapshotRenderer.Render(root, 100, 30).ToMarkupLines());

    /// <summary>Runs the editor's view in a virtual terminal with the editor focused and sends each
    /// phase of events ten ticks after the last (calling <paramref name="betweenPhases"/> first);
    /// stops once <paramref name="done"/> holds or the ticks run out.</summary>
    private static async Task RunKeys(object editor, Func<bool> done, TerminalEvent[][] phases, Action? betweenPhases = null,
        TextWriter? output = null)
    {
        const int TicksPerPhase = 10;
        var view = (Visual)FileEditorType.GetProperty("View")!.GetValue(editor)!;
        var control = (Visual)FileEditorType.GetProperty("Editor")!.GetValue(editor)!;
        var backend = output is null
            ? new VirtualTerminalBackend(initialSize: new TerminalSize(60, 12))
            : new VirtualTerminalBackend(output, new StringWriter(), new TerminalSize(60, 12), null, false);
        using var terminal = Terminal.Open(backend, new TerminalOptions { RespectNoColor = false }, force: true);
        var root = new Padder(view).Style((XenoAtom.Terminal.UI.Styling.Theme)Styles().GetType().GetProperty("Screen")!.GetValue(Styles())!);
        var ticks = 0;

        ValueTask<TerminalLoopResult> Update(TerminalRunningContext context)
        {
            if (ticks == 0) context.App.Focus(control);
            if (ticks % TicksPerPhase == 0 && ticks / TicksPerPhase < phases.Length)
            {
                if (ticks > 0) betweenPhases?.Invoke();
                foreach (var item in phases[ticks / TicksPerPhase]) backend.PushEvent(item);
            }
            ticks++;
            return ValueTask.FromResult(done() || ticks > TicksPerPhase * (phases.Length + 1) ? TerminalLoopResult.Stop : TerminalLoopResult.Continue);
        }

        await Task.Run(async () => await Terminal.RunAsync(root, Update, new TerminalRunOptions())).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static TerminalTextEvent Text(string text) => new() { Text = text };

    private static TerminalKeyEvent Key(TerminalKey key) => new() { Key = key };

    private static TerminalKeyEvent Ctrl(char c) => new() { Key = TerminalKey.Unknown, Char = c, Modifiers = TerminalModifiers.Ctrl };

    private static Type Type(string name) => Forge.GetType(name, throwOnError: true)!;

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
