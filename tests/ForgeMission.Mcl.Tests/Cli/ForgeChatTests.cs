using System.Reflection;
using XenoAtom.Terminal;

namespace ForgeMission.Tests.Cli;

// forge chat (53.2, 53.4, Phase 55): the rules that decide a turn has ended, which conversation to
// open, and the --hands flag, policy, mission and one-time approval, read through reflection like the
// other CLI tests (the test project does not reference the forge executable's assembly).
public sealed class ForgeChatTests
{
    private static readonly MethodInfo EndsTurn = LoadForgeChatMethod("EndsTurn");
    private static readonly MethodInfo LatestFor = LoadForgeChatMethod("LatestFor").MakeGenericMethod(typeof(Listed));
    private static readonly MethodInfo ParseHands = LoadForgeChatMethod("ParseHands");
    private static readonly MethodInfo PolicyFor = LoadForgeChatMethod("PolicyFor");
    private static readonly MethodInfo ModeFor = LoadForgeChatMethod("ModeFor");
    private static readonly MethodInfo AskApproval = LoadForgeChatMethod("AskApproval");
    private static readonly MethodInfo ImageCell = LoadForgeChatMethod("ImageCell");

    private sealed record Listed(string Id, string? MissionName);

    // Listed newest-first, as Application returns them: the other chat mode is the most recent.
    private static readonly Listed[] Mixed = [new("hands", "ChatHands"), new("chat", "Chat"), new("janus", "Janus"), new("orphan", null)];

    private static Listed? Select(Listed[] listed, string mission) =>
        (Listed?)LatestFor.Invoke(null, [listed, (Func<Listed, string?>)(item => item.MissionName), mission]);

    [Theory]
    [InlineData("Chat", "chat")]
    [InlineData("ChatHands", "hands")]
    public void Each_mode_reopens_its_own_latest_conversation_even_when_the_other_mode_is_newer(string mission, string expected)
    {
        Assert.Equal(expected, Select(Mixed, mission)?.Id);
    }

    [Fact]
    public void The_newest_conversation_on_the_mode_wins_over_older_ones()
    {
        Assert.Equal("new", Select([new("new", "Chat"), new("old", "Chat")], "Chat")?.Id);
    }

    [Theory]
    [InlineData("Chat")]
    [InlineData("ChatHands")]
    public void No_conversation_on_the_mode_creates_a_new_one(string mission)
    {
        Assert.Null(Select([new("janus", "Janus"), new("orphan", null)], mission));
        Assert.Null(Select([], mission));
    }

    // ── Terminal check (Phase 56 G8) ────────────────────────────────────────────────────────

    private static readonly TerminalPixelMetrics Retina = new(1520, 1680, 19, 42, 80, 40);

    [Fact]
    public void Kitty_with_truecolor_and_a_cell_size_runs_the_TUI_at_that_cell_size()
    {
        Assert.Equal((19, 42), Cell([TerminalGraphicsProtocol.Kitty], TerminalColorLevel.TrueColor, Retina));
        Assert.Equal((19, 42), Cell([TerminalGraphicsProtocol.ITerm2, TerminalGraphicsProtocol.Kitty], TerminalColorLevel.TrueColor, Retina));
    }

    [Fact]
    public void No_kitty_graphics_stops_forge_chat()
    {
        Assert.Null(Cell([], TerminalColorLevel.TrueColor, Retina));
        Assert.Null(Cell([TerminalGraphicsProtocol.ITerm2], TerminalColorLevel.TrueColor, Retina));
    }

    [Theory]
    [InlineData(TerminalColorLevel.Color256)]
    [InlineData(TerminalColorLevel.Color16)]
    [InlineData(TerminalColorLevel.None)]
    public void Less_than_truecolor_stops_forge_chat(TerminalColorLevel colors)
    {
        Assert.Null(Cell([TerminalGraphicsProtocol.Kitty], colors, Retina));
    }

    [Fact]
    public void No_cell_size_reply_stops_forge_chat()
    {
        Assert.Null(Cell([TerminalGraphicsProtocol.Kitty], TerminalColorLevel.TrueColor, null));
        Assert.Null(Cell([TerminalGraphicsProtocol.Kitty], TerminalColorLevel.TrueColor, new TerminalPixelMetrics(0, 0, 0, 0, 80, 40)));
    }

    private static (int Width, int Height)? Cell(TerminalGraphicsProtocol[] protocols, TerminalColorLevel colors, TerminalPixelMetrics? metrics)
    {
        var cell = ImageCell.Invoke(null, [protocols, colors, metrics]);
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
    public void Each_mode_has_its_own_mission_and_hands_profile()
    {
        Assert.Equal(("Chat", "NoHands"), Mode(false));
        Assert.Equal(("ChatHands", "ProjectWorkspace"), Mode(true));
        // The starter definitions from Katasec.Forge.Client (one owner): ChatHands runs the agent-role Assistant.
        Assert.Equal("mission Chat(message) = {\n    Answerer using anthropic\n}\n", (string)Property(ModeFor.Invoke(null, [false])!, "Definition"));
        Assert.Equal("mission ChatHands(message) = {\n    Assistant using anthropic\n}\n", (string)Property(ModeFor.Invoke(null, [true])!, "Definition"));
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

    private static (string Mission, string Profile) Mode(bool hands)
    {
        var mode = ModeFor.Invoke(null, [hands])!;
        return ((string)Property(mode, "MissionName"), Property(mode, "Profile").ToString()!);
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
