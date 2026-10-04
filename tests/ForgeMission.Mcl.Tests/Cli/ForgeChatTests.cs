using System.Reflection;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using ForgeMission.Conversations.Contracts;
using XenoAtom.Terminal;

namespace ForgeMission.Tests.Cli;

// Portable chat startup, command modes, fresh consent and turn/terminal rules.
// The test project loads the built CLI through reflection like the other CLI tests.
public sealed class ForgeChatTests
{
    private static readonly MethodInfo EndsTurn = LoadForgeChatMethod("EndsTurn");
    private static readonly MethodInfo ParseHands = LoadForgeChatMethod("ParseHands");
    private static readonly MethodInfo PolicyFor = LoadForgeChatMethod("PolicyFor");
    private static readonly MethodInfo ModeFor = LoadForgeChatMethod("ModeFor");
    private static readonly MethodInfo AskApproval = LoadForgeChatMethod("AskApproval");
    private static readonly MethodInfo ImageCell = LoadTerminalFactsMethod("ImageCell");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_project_file_stops_without_login_network_creation_or_ancestor_search(bool explicitFolder)
    {
        var parent = Path.Combine(Path.GetTempPath(), "forge-chat-test-" + Guid.NewGuid().ToString("N"));
        var child = Path.Combine(parent, "child");
        var missing = Path.Combine(parent, "missing");
        Directory.CreateDirectory(child);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(parent, "forge.project.json"),
                "{\"projectId\":\"" + Guid.NewGuid() + "\",\"missions\":[\"Chat@1\"],\"folders\":[]}");
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = child,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(EndsTurn.DeclaringType!.Assembly.Location);
            start.ArgumentList.Add("chat");
            if (explicitFolder)
            {
                start.ArgumentList.Add("--project");
                start.ArgumentList.Add(missing);
            }
            // Controlled negative proof: any unexpected network setup would fail this test.
            start.Environment["FORGE_API_ENDPOINT"] = "invalid-endpoint";
            using var process = Process.Start(start)!;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            Assert.Equal(1, process.ExitCode);
            Assert.Equal("", await output);
            Assert.Equal(explicitFolder ? $"No forge.project.json found in {missing}." :
                "No forge.project.json found in the current directory.", (await error).Trim());
            Assert.Empty(Directory.EnumerateFileSystemEntries(child));
            Assert.False(Directory.Exists(missing));
            Assert.Single(Directory.GetFiles(parent));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Theory]
    [InlineData(false, MissionHandsProfile.ProjectWorkspace)]
    [InlineData(true, MissionHandsProfile.NoHands)]
    [InlineData(true, MissionHandsProfile.ProjectWorkspaceAndTerminal)]
    public async Task A_hosted_profile_outside_the_command_mode_stops_before_consent_or_attachment(bool hands, MissionHandsProfile profile)
    {
        var home = Path.Combine(Path.GetTempPath(), "forge-chat-profile-" + Guid.NewGuid().ToString("N"));
        var projectId = Guid.NewGuid();
        var mission = hands ? "ChatHands" : "Chat";
        Directory.CreateDirectory(home);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(home, "forge.project.json"),
                "{\"projectId\":\"" + projectId + "\",\"missions\":[\"" + mission + "@1\"],\"folders\":[]}");
            using var host = new PinnedHost(projectId, mission, profile);
            var chat = LoadForgeChatMethod("ChatInProjectAsync");
            var appType = chat.GetParameters()[0].ParameterType;
            var create = appType.GetMethod("Create")!;
            var publishType = create.GetParameters()[3].ParameterType;
            var publish = typeof(ForgeChatTests).GetMethod(nameof(IgnoreEvents), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(publishType.GetGenericArguments()[0]).CreateDelegate(publishType);
            await using var app = (IAsyncDisposable)create.Invoke(null,
                [host, null, PolicyFor.Invoke(null, [hands]), publish, CancellationToken.None])!;
            var theme = EndsTurn.DeclaringType!.Assembly.GetType("ForgeMission.Cli.Tui.ForgeTheme")!
                .GetProperty("Dark", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => (Task<int>)chat.Invoke(null,
                [app, ModeFor.Invoke(null, [hands]), home, theme, null, new ConcurrentQueue<string>()])!);
            Assert.Equal("ChatStoppedException", failure.GetType().Name);
            Assert.Contains("does not match this chat mode", failure.Message, StringComparison.Ordinal);
            Assert.Equal(["/api/ListMissionConversations", "/api/GetConversation"], host.Requests);
            Assert.Single(Directory.GetFiles(home));
            Assert.Empty(Directory.GetDirectories(home));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    private static void IgnoreEvents<T>(T item) { }

    // Controlled Host replies: every request after authenticated pin resolution is a failure.
    private sealed class PinnedHost : HttpMessageHandler, IHttpClientFactory
    {
        private readonly Guid _projectId;
        private readonly Guid _conversationId = Guid.NewGuid();
        private readonly DurableMissionLaunch _launch;
        public List<string> Requests { get; } = [];

        public PinnedHost(Guid projectId, string mission, MissionHandsProfile profile)
        {
            _projectId = projectId;
            var definition = $"mission {mission}(message) = {{\n    Assistant using anthropic\n}}\n";
            _launch = new DurableMissionLaunch(Guid.NewGuid(), 1, "sha256:test", definition, profile,
                new DurableMissionPackage(1, "sha256:package", definition, mission, "message", []));
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false)
        {
            BaseAddress = new Uri("https://controlled-host.invalid/"),
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var route = request.RequestUri!.AbsolutePath;
            Requests.Add(route);
            var snapshot = new ConversationSnapshot(_conversationId, null, null, 0, ConversationRunStatus.Completed,
                null, DateTimeOffset.UtcNow, Purpose: ConversationPurpose.MissionConversation, ProjectId: _projectId, PinnedLaunch: _launch);
            var json = route switch
            {
                "/api/ListMissionConversations" => JsonSerializer.Serialize(new ListMissionConversationsResponse(
                    [new MissionConversationSummary(_conversationId, _projectId, _launch, snapshot.Status, 0, snapshot.UpdatedAtUtc)]),
                    ConversationContractsJsonContext.Default.ListMissionConversationsResponse),
                "/api/GetConversation" => JsonSerializer.Serialize(new GetConversationResponse(snapshot),
                    ConversationContractsJsonContext.Default.GetConversationResponse),
                _ => throw new InvalidOperationException($"Profile refusal must precede {route}."),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
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

    // ── --hands (Phase 55) ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Hands_are_off_unless_the_flag_is_given()
    {
        Assert.False((bool)ParseHands.Invoke(null, [Array.Empty<string>()])!);
        Assert.True((bool)ParseHands.Invoke(null, [new[] { "--hands" }])!);
    }

    [Fact]
    public void Plain_chat_denies_every_capability()
    {
        Assert.Equal("AutoDenied", Outcome(false, "file"));
        Assert.Equal("AutoDenied", Outcome(false, "terminal"));
    }

    [Fact]
    public void Hands_auto_approve_files_and_keep_the_terminal_denied()
    {
        Assert.Equal("AutoApproved", Outcome(true, "file"));
        Assert.Equal("AutoDenied", Outcome(true, "terminal"));
    }

    [Fact]
    public void Each_mode_selects_its_own_hosted_mission()
    {
        Assert.Equal(("Chat", false, "NoHands"), Mode(false));
        Assert.Equal(("ChatHands", true, "ProjectWorkspace"), Mode(true));
    }

    [Theory]
    [InlineData("y\n")]
    [InlineData("YES\n")]
    [InlineData(" yes \n")]
    public void Yes_on_a_terminal_approves(string typed)
    {
        var (answer, shown) = Ask(interactive: true, typed);

        Assert.Equal("Approved", answer);
        Assert.Equal("Allow Forge to read, write and edit files in /p/chat? [y/N] ", shown);
    }

    [Theory]
    [InlineData("n\n")]
    [InlineData("\n")]
    [InlineData("yep\n")]
    [InlineData("")]
    public void Anything_else_or_end_of_input_declines(string typed)
    {
        Assert.Equal("Declined", Ask(interactive: true, typed).Answer);
    }

    [Fact]
    public void A_piped_run_is_never_asked()
    {
        var (answer, shown) = Ask(interactive: false, "y\n");

        Assert.Equal("NotInteractive", answer);
        Assert.Equal("", shown);
    }

    private static string Outcome(bool hands, string capability)
    {
        var policy = PolicyFor.Invoke(null, [hands])!;
        var rule = policy.GetType().GetMethod("RuleFor")!.Invoke(policy, [capability])!;
        return Property(rule, "Outcome").ToString()!;
    }

    private static (string Mission, bool Hands, string Profile) Mode(bool hands)
    {
        var mode = ModeFor.Invoke(null, [hands])!;
        return ((string)Property(mode, "MissionName"), (bool)Property(mode, "HasHands"), Property(mode, "ExpectedProfile").ToString()!);
    }

    private static (string Answer, string Shown) Ask(bool interactive, string typed)
    {
        var output = new StringWriter();
        var answer = AskApproval.Invoke(null, ["/p/chat", interactive, new StringReader(typed), output])!;
        return (answer.ToString()!, output.ToString());
    }

    private static object Property(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target)!;

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Interrupted")]
    [InlineData("Rejected")]
    public void A_terminal_run_status_for_this_attempt_ends_the_turn(string status)
    {
        var attempt = Guid.NewGuid();

        Assert.True(Ends("RunStatus", attempt, status, attempt));
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Running")]
    [InlineData("WaitingForTool")]
    public void A_non_terminal_run_status_does_not_end_the_turn(string status)
    {
        var attempt = Guid.NewGuid();

        Assert.False(Ends("RunStatus", attempt, status, attempt));
    }

    [Fact]
    public void Another_attempts_terminal_status_does_not_end_the_turn()
    {
        Assert.False(Ends("RunStatus", Guid.NewGuid(), "Completed", Guid.NewGuid()));
    }

    [Fact]
    public void A_participant_message_does_not_end_the_turn()
    {
        var attempt = Guid.NewGuid();

        Assert.False(Ends("ParticipantMessage", attempt, null, attempt));
    }

    private static bool Ends(string kind, Guid? runId, string? status, Guid attemptId)
    {
        var parameters = EndsTurn.GetParameters();
        var kindValue = Enum.Parse(parameters[0].ParameterType, kind);
        var statusType = Nullable.GetUnderlyingType(parameters[2].ParameterType)!;
        var statusValue = status is null ? null : Enum.Parse(statusType, status);
        return (bool)EndsTurn.Invoke(null, [kindValue, runId, statusValue, attemptId])!;
    }

    /// <summary>The G8 decisions live beside the probe, in Tui/Graphics/TerminalFacts.</summary>
    private static MethodInfo LoadTerminalFactsMethod(string name) =>
        LoadForgeChatMethod("EndsTurn").DeclaringType!.Assembly
            .GetType("ForgeMission.Cli.Tui.Graphics.TerminalFacts", throwOnError: true)!
            .GetMethod(name, BindingFlags.Static | BindingFlags.Public)!;

    private static MethodInfo LoadForgeChatMethod(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "ForgeMission.Cli", "bin", "Debug", "net10.0", "forge.dll");
            if (File.Exists(candidate))
                return Assembly.LoadFrom(candidate).GetType("ForgeMission.Cli.ForgeChat", throwOnError: true)!
                    .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate built forge.dll for CLI reflection tests.");
    }
}
