using System.Reflection;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using ForgeMission.Conversations.Contracts;
using XenoAtom.Terminal;
using ForgeMission.Tests.Cli.Harness;

namespace ForgeMission.Tests.Cli;

// Portable chat startup and terminal rules.
// The test project loads the built CLI through reflection like the other CLI tests.
public sealed class ForgeChatTests
{
    private static readonly MethodInfo ImageCell = LoadTerminalFactsMethod("ImageCell");

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Missing_project_file_stops_without_network_creation_or_ancestor_search(bool explicitFile, bool childMarker)
    {
        await using var harness = ForgeCliHarness.Start();
        harness.SignIn("https://example.invalid");
        var parent = harness.Home;
        var child = Path.Combine(parent, "child");
        var missing = Path.Combine(parent, "missing", "forge1.project.json");
        Directory.CreateDirectory(child);
        await File.WriteAllTextAsync(Path.Combine(parent, "forge.project.json"),
            "{\"projectId\":\"" + Guid.NewGuid() + "\",\"missions\":[\"Chat@1\"],\"folders\":[\".\"]}");
        var result = await RunChatAsync(harness, child, explicitFile ? missing : null, childMarker, validTheme: true);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Output);
        // Deliberate (Phase 76 decision 2b): forge-client's one missing-project message, after sign-in.
        Assert.Equal(explicitFile
            ? $"chat stopped: Project file not found: {missing}. Run `forge project create` to create one."
            : "chat stopped: No forge.project.json found in the current directory. Run `forge project create` to create one.",
            result.Error.Trim());
        Assert.Empty(harness.Proxy.Attempts);
        Assert.Empty(Directory.EnumerateFileSystemEntries(child));
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
        Assert.Single(Directory.GetFiles(parent));
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("named-directory")]
    public async Task Directory_inputs_stop_before_configuration(string kind)
    {
        await using var harness = ForgeCliHarness.Start();
        var home = harness.Home;
        var path = Path.Combine(home, kind == "named-directory" ? "forge.project.json" : kind);
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "forge.project.json"), "{}");
        var result = await RunChatAsync(harness, home, path);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Equal("Expected a project file path, not a directory.", result.Error.Trim());
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("relative")]
    [InlineData("absolute")]
    public async Task Existing_project_file_reaches_configuration(string selection)
    {
        await using var harness = ForgeCliHarness.Start();
        var home = harness.Home;
        var cwd = Path.Combine(home, "cwd");
        var projectHome = selection == "omitted" ? cwd : Path.Combine(home, "other project");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(projectHome);
        var projectFile = Path.Combine(projectHome, selection == "omitted" ? "forge.project.json" : "forge1.project.json");
        await File.WriteAllTextAsync(projectFile, "{}");
        var path = selection switch
        {
            "relative" => Path.GetRelativePath(cwd, projectFile),
            "absolute" => projectFile,
            _ => null,
        };
        var profile = harness.Home;
        var result = await RunChatAsync(harness, cwd, path);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Equal($"forge chat: Unknown theme \"startup-test\" in {Path.Combine(profile, ".forge", "config.json")}. Valid themes: light, dark.", result.Error.Trim());
        Assert.Equal("{}", await File.ReadAllTextAsync(projectFile));
    }

    private static void IgnoreEvents<T>(T item) { }

    // R10: a non-JSON host 500 on the open path surfaces as ConversationHostProtocolException
    // (internal to ForgeMission.Application, base InvalidOperationException). ForgeChat.RunAsync's
    // catch matches it by exact type name; this guards that the type it matches is the one thrown.
    [Fact]
    public async Task A_non_json_host_error_on_open_raises_the_protocol_exception_run_async_catches()
    {
        var home = Path.Combine(Path.GetTempPath(), "forge-chat-host500-" + Guid.NewGuid().ToString("N"));
        var projectId = Guid.NewGuid();
        Directory.CreateDirectory(home);
        try
        {
            var projectFile = Path.Combine(home, "forge1.project.json");
            await File.WriteAllTextAsync(projectFile,
                "{\"projectId\":\"" + projectId + "\",\"missions\":[\"Chat@1\"],\"folders\":[\".\"]}");
            using var host = new NonJsonErrorHost();
            var chat = LoadForgeChatMethod("ChatInProjectAsync");
            var appType = chat.GetParameters()[0].ParameterType;
            var create = appType.GetMethod("Create")!;
            var publishType = create.GetParameters()[1].ParameterType;
            var publish = typeof(ForgeChatTests).GetMethod(nameof(IgnoreEvents), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(publishType.GetGenericArguments()[0]).CreateDelegate(publishType);
            await using var app = (IAsyncDisposable)create.Invoke(null,
                [host, publish, CancellationToken.None])!;
            var theme = ForgeText.Theme("Dark");
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => (Task<int>)chat.Invoke(null,
                [app, projectFile, theme, null, new ConcurrentQueue<string>(), CancellationToken.None])!);
            // The exact type ForgeChat.RunAsync's `when (failure.GetType().Name == …)` guard depends on.
            Assert.Equal("ConversationHostProtocolException", failure.GetType().Name);
            Assert.IsAssignableFrom<InvalidOperationException>(failure);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    // Answers the open's first conversation read with a non-JSON 500.
    private sealed class NonJsonErrorHost : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false)
        {
            BaseAddress = new Uri("https://controlled-host.invalid/"),
        };

        // The conversation host hands back an HTML/plain 500 instead of a mission reply.
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("<html><body>500 Internal Server Error</body></html>", Encoding.UTF8, "text/html"),
            });
    }

    // ── Terminal check (Phase 56 G8) ────────────────────────────────────────────────────────

    private static readonly MethodInfo ShowsImages = LoadTerminalFactsMethod("ShowsImages");
    private static readonly TerminalPixelMetrics Retina = new(1520, 1680, 19, 42, 80, 40);

    [Fact]
    public void Kitty_with_truecolor_outside_a_multiplexer_passes_the_start_up_check()
    {
        Assert.True(Shows([TerminalGraphicsProtocol.Kitty], false, TerminalColorLevel.TrueColor));
        Assert.True(Shows([TerminalGraphicsProtocol.ITerm2, TerminalGraphicsProtocol.Kitty], false, TerminalColorLevel.TrueColor));
    }

    [Fact]
    public void No_kitty_graphics_stops_forge_chat()
    {
        Assert.False(Shows([], false, TerminalColorLevel.TrueColor));
        Assert.False(Shows([TerminalGraphicsProtocol.ITerm2], false, TerminalColorLevel.TrueColor));
    }

    [Fact]
    public void A_multiplexer_stops_forge_chat_even_when_kitty_is_detected()
    {
        // tmux inside kitty: KITTY_WINDOW_ID reaches the pane, tmux answers the cell-size query
        // itself, then drops the images.
        Assert.False(Shows([TerminalGraphicsProtocol.Kitty], true, TerminalColorLevel.TrueColor));
    }

    [Theory]
    [InlineData(TerminalColorLevel.Color256)]
    [InlineData(TerminalColorLevel.Color16)]
    [InlineData(TerminalColorLevel.None)]
    public void Less_than_truecolor_stops_forge_chat(TerminalColorLevel colors)
    {
        Assert.False(Shows([TerminalGraphicsProtocol.Kitty], false, colors));
    }

    [Fact]
    public void A_cell_size_reply_gives_the_cell_the_ring_is_drawn_for()
    {
        Assert.Equal((19, 42), Cell(Retina));
    }

    [Fact]
    public void No_cell_size_reply_stops_the_TUI()
    {
        Assert.Null(Cell(null));
        Assert.Null(Cell(new TerminalPixelMetrics(0, 0, 0, 0, 80, 40)));
        Assert.Null(Cell(new TerminalPixelMetrics(1520, 0, 19, 0, 80, 40)));
    }

    private static bool Shows(TerminalGraphicsProtocol[] protocols, bool multiplexer, TerminalColorLevel colors)
    {
        var environmentType = ShowsImages.GetParameters()[0].ParameterType;
        var environment = Activator.CreateInstance(environmentType, protocols, multiplexer, colors);
        return (bool)ShowsImages.Invoke(null, [environment])!;
    }

    private static (int Width, int Height)? Cell(TerminalPixelMetrics? metrics)
    {
        var cell = ImageCell.Invoke(null, [metrics]);
        if (cell is null) return null;
        int Value(string name) => (int)cell.GetType().GetProperty(name)!.GetValue(cell)!;
        return (Value("Width"), Value("Height"));
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunChatAsync(
        ForgeCliHarness harness, string cwd, string? projectPath, bool childMarker = false, bool validTheme = false)
    {
        harness.WriteConfig(validTheme ? "{\"theme\":\"dark\"}" : "{\"theme\":\"startup-test\"}");
        harness.WorkingDirectory = cwd;
        // Isolated invalid configuration/endpoint proves selection precedes all downstream work.
        harness.Variables["FORGE_API_ENDPOINT"] = "invalid-endpoint";
        if (childMarker) harness.Variables["FORGE_CHAT_WINDOW"] = "invalid-context-must-be-ignored-when-piped";
        var result = await harness.RunAsync(projectPath is null ? ["chat"] : ["chat", "--project", projectPath]);
        return (result.ExitCode, result.StandardOutput, result.StandardError);
    }

    /// <summary>The G8 decisions live beside the probe, in Tui/Graphics/TerminalFacts.</summary>
    private static MethodInfo LoadTerminalFactsMethod(string name) =>
        LoadForgeChatMethod("LoadFonts").DeclaringType!.Assembly
            .GetType("ForgeMission.Cli.Tui.Graphics.TerminalFacts", throwOnError: true)!
            .GetMethod(name, BindingFlags.Static | BindingFlags.Public)!;

    private static MethodInfo LoadForgeChatMethod(string name) =>
        ForgeText.Type("ForgeMission.Cli.Commands.ForgeChat").GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
}
