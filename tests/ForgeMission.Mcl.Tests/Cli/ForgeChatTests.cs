using System.Reflection;

namespace ForgeMission.Tests.Cli;

// forge chat (53.2, 53.4): the rules that decide a turn has ended and which conversation to open,
// read through reflection like the other CLI tests (the test project does not reference the forge
// executable's assembly).
public sealed class ForgeChatTests
{
    private static readonly MethodInfo EndsTurn = LoadForgeChatMethod("EndsTurn");
    private static readonly MethodInfo ReusesLatest = LoadForgeChatMethod("ReusesLatest");

    [Fact]
    public void The_latest_conversation_on_Chat_is_reopened()
    {
        Assert.True((bool)ReusesLatest.Invoke(null, ["Chat"])!);
    }

    [Theory]
    [InlineData("Janus")]
    [InlineData(null)]
    public void A_latest_conversation_on_another_mission_or_none_creates_a_new_one(string? latestMissionName)
    {
        Assert.False((bool)ReusesLatest.Invoke(null, [latestMissionName])!);
    }

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
