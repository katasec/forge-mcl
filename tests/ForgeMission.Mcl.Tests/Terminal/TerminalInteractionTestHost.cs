using System.Diagnostics.CodeAnalysis;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Rendering;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Geometry;
using NativeTerminal = XenoAtom.Terminal.Terminal;

namespace ForgeMission.Tests.TerminalInteraction;

// Memory-only native loop. A backend marker acknowledges each delivered batch before layout.
internal static class TerminalInteractionTestHost
{
    internal static async Task Run(Visual root, Action<TerminalRunningContext, int, ClipboardBackend> phase,
        int last = 5, ClipboardBackend? backend = null)
    {
        backend ??= new ClipboardBackend();
        using var session = NativeTerminal.Open(backend, new TerminalOptions { ImplicitStartInput = true, RespectNoColor = false }, force: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var nextPhase = 0;
        var acknowledged = true;
        var laidOut = false;
        await Task.Run(async () => await session.Instance.RunAsync(root, context =>
        {
            if (!acknowledged) return TerminalLoopResult.Continue;
            if (!laidOut) { laidOut = true; return TerminalLoopResult.Continue; }
            phase(context, nextPhase, backend);
            if (nextPhase++ >= last) return TerminalLoopResult.Stop;
            acknowledged = laidOut = false;
            // The relay enqueues prior input before reading this marker. Post runs before the
            // event drain; the extra update above then waits through that drain and render.
            backend.PushEvent(new ClipboardBackend.InputBatchEnd(() => context.App.Post(() => acknowledged = true)));
            return TerminalLoopResult.Continue;
        }, new TerminalRunOptions(), deadline.Token));
        if (nextPhase <= last) throw new TimeoutException("Native input batch and layout did not complete before the test deadline.");
    }

    internal static void Ctrl(ClipboardBackend backend, char key) => backend.PushEvent(new TerminalKeyEvent
        { Key = TerminalKey.Unknown, Char = key, Modifiers = TerminalModifiers.Ctrl });

    internal static void Key(ClipboardBackend backend, TerminalKey key, TerminalModifiers modifiers = TerminalModifiers.None) =>
        backend.PushEvent(new TerminalKeyEvent { Key = key, Modifiers = modifiers });

    internal static void Mouse(ClipboardBackend backend, TerminalMouseKind kind, Visual source, int offset = 0) =>
        backend.PushEvent(new TerminalMouseEvent
        { Kind = kind, Button = TerminalMouseButton.Left, X = source.Bounds.X + offset, Y = source.Bounds.Y });

    internal static void Drag(ClipboardBackend backend, Visual source, int start = 0, int end = 4)
    {
        Mouse(backend, TerminalMouseKind.Down, source, start);
        Mouse(backend, TerminalMouseKind.Drag, source, end);
        Mouse(backend, TerminalMouseKind.Up, source, end);
    }
}

internal sealed class NativeFrame : Visual
{
    internal string[] Lines { get; private set; } = [];
    internal NativeFrame()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = VerticalAlignment = Align.Stretch;
    }
    protected override SizeHints MeasureCore(in LayoutConstraints constraints) => SizeHints.Fixed(new Size(0, 0));
    protected override void RenderOverride(CellBuffer buffer) => Lines = [.. buffer.ToMarkupLines()];
}

internal sealed class ClipboardBackend : VirtualTerminalBackend, ITerminalBackend
{
    internal sealed record InputBatchEnd(Action Acknowledge) : TerminalEvent;
    internal int Writes { get; private set; }
    internal int Reads { get; private set; }
    internal bool FailWrite { get; set; }
    internal bool FailRead { get; set; }
    internal Action? DuringWrite { get; set; }
    internal string? Written { get; private set; }

    internal ClipboardBackend() : base(TextWriter.Null, TextWriter.Null, new TerminalSize(100, 32)) { }

    async ValueTask<TerminalEvent> ITerminalBackend.ReadEventAsync(CancellationToken cancellationToken)
    {
        var input = await base.ReadEventAsync(cancellationToken).ConfigureAwait(false);
        while (input is InputBatchEnd end)
        {
            end.Acknowledge();
            input = await base.ReadEventAsync(cancellationToken).ConfigureAwait(false);
        }
        return input;
    }

    bool ITerminalBackend.TrySetClipboardText(ReadOnlySpan<char> text)
    {
        Writes++;
        DuringWrite?.Invoke();
        if (FailWrite) return false;
        Written = text.ToString();
        return base.TrySetClipboardText(text);
    }

    bool ITerminalBackend.TryGetClipboardText([NotNullWhen(true)] out string? text)
    {
        Reads++;
        if (FailRead) { text = null; return false; }
        return base.TryGetClipboardText(out text);
    }
}
