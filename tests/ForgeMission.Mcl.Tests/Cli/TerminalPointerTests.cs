using System.Reflection;
using static ForgeMission.Tests.Cli.ForgeText;

namespace ForgeMission.Tests.Cli;

// Phase 56 Task 5: the pointer is a hand over links (OSC 22 pointer) and the terminal's default
// again on every way out — a normal quit, the G8 stop (the TUI returns normally) and an exception —
// even when the hand was never shown. The link pointer writes only when the shape changes.
public sealed class TerminalPointerTests
{
    private const string Hand = "\u001b]22;pointer\u001b\\";
    private const string Default = "\u001b]22;default\u001b\\";
    private static readonly Type PointerType = Type("ForgeMission.Cli.Tui.TerminalPointer");

    [Fact]
    public void The_escapes_are_osc_22_pointer_and_default()
    {
        Assert.Equal(Hand, PointerType.GetField("HandEscape")!.GetValue(null));
        Assert.Equal(Default, PointerType.GetField("DefaultEscape")!.GetValue(null));
    }

    [Fact]
    public async Task A_run_that_ends_normally_puts_the_default_pointer_back()
    {
        var written = new List<string>();

        await WhileRunning(() => Task.CompletedTask, written.Add);

        Assert.Equal([Default], written);
    }

    [Fact]
    public async Task A_run_that_throws_puts_the_default_pointer_back_and_still_throws()
    {
        var written = new List<string>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WhileRunning(() => throw new InvalidOperationException("boom"), written.Add));

        Assert.Equal("boom", error.Message);
        Assert.Equal([Default], written);
    }

    private static Task WhileRunning(Func<Task> run, Action<string> write) =>
        (Task)PointerType.GetMethod("WhileRunning", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Func<Task>), typeof(Action<string>)])!
            .Invoke(null, [run, write])!;
}
