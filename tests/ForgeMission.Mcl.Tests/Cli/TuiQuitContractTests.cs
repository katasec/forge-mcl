using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace ForgeMission.Tests.Cli;

// forge chat TUI (53.9 L2): Ctrl-D must not stop the app while an update step is still awaiting,
// or the step resumes on a stopped dispatcher and crashes the process. ChatTui relies on two
// XenoAtom behaviours, pinned here against the package directly: a command registered under
// DefaultQuitCommandId replaces the built-in quit (its Execute runs, the app keeps running), and an
// update step returning Stop ends the run.
[Collection(XenoAtomUiCollection.Name)]
public sealed class TuiQuitContractTests
{
    private static readonly KeyGesture CtrlD = new(TerminalChar.CtrlD, TerminalModifiers.Ctrl);

    [Fact]
    public async Task A_replaced_quit_command_runs_without_stopping_and_Stop_ends_the_run()
    {
        var backend = new VirtualTerminalBackend(initialSize: new TerminalSize(40, 10));
        using var terminal = Terminal.Open(backend, force: true);
        var quitRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = false;
        var stoppedByUpdate = false;

        async ValueTask<TerminalLoopResult> Update(TerminalRunningContext context)
        {
            if (started)
            {
                stoppedByUpdate = true;
                return TerminalLoopResult.Stop;
            }
            started = true;
            context.App.AddGlobalCommand(new Command
            {
                Id = TerminalApp.DefaultQuitCommandId,
                LabelMarkup = "Quit",
                Gesture = CtrlD,
                Execute = _ => quitRequested.TrySetResult(),
            });
            backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Unknown, Char = TerminalChar.CtrlD, Modifiers = TerminalModifiers.Ctrl });
            await quitRequested.Task;
            return TerminalLoopResult.Continue;
        }

        var run = Task.Run(async () =>
            await Terminal.RunAsync(new TextBlock("chat"), Update, new TerminalRunOptions { ExitGesture = CtrlD }));

        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(quitRequested.Task.IsCompleted);
        Assert.True(stoppedByUpdate);
    }
}
