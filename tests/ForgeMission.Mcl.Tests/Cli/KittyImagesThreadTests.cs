using System.Reflection;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using static ForgeMission.Tests.Cli.ForgeText;

namespace ForgeMission.Tests.Cli;

// forge chat TUI (Phase 56 Task 4): text images are sent while the TUI runs, so KittyImages.Transmit
// refuses any thread but the UI thread: XenoAtom writes each frame in one go between UI-thread work,
// and an image escape from another thread could land inside a frame. It throws before writing.
[Collection(XenoAtomUiCollection.Name)]
public sealed class KittyImagesThreadTests
{
    [Fact]
    public async Task A_transmit_off_the_UI_thread_throws_while_the_TUI_runs()
    {
        var backend = new VirtualTerminalBackend(initialSize: new TerminalSize(40, 10));
        using var terminal = Terminal.Open(backend, force: true);
        Exception? thrown = null;

        async ValueTask<TerminalLoopResult> Update(TerminalRunningContext context)
        {
            thrown = await Task.Run(TransmitCatching);
            return TerminalLoopResult.Stop;
        }

        await Task.Run(async () => await Terminal.RunAsync(new TextBlock("chat"), Update, new TerminalRunOptions()))
            .WaitAsync(TimeSpan.FromSeconds(10));

        var refused = Assert.IsType<InvalidOperationException>(thrown);
        Assert.Contains("thread", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static Exception? TransmitCatching()
    {
        try
        {
            Type("ForgeMission.Cli.Tui.Graphics.KittyImages").GetMethod("Transmit")!.Invoke(null, [(uint)1 << 23, new byte[] { 1 }, 1, 1]);
            return null;
        }
        catch (TargetInvocationException wrapped) { return wrapped.InnerException; }
    }
}
