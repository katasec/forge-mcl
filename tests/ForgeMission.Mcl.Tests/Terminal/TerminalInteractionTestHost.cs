using System.Diagnostics.CodeAnalysis;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Rendering;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Geometry;
using NativeTerminal = XenoAtom.Terminal.Terminal;

namespace ForgeMission.Tests.TerminalInteraction;

// Memory-only native loop. Phases allow the normal relay to deliver a complete input batch.
internal static class TerminalInteractionTestHost
{
    internal static async Task Run(Visual root, Action<TerminalRunningContext, int, ClipboardBackend> phase,
        int last = 5, ClipboardBackend? backend = null)
    {
        backend ??= new ClipboardBackend();
        using var session = NativeTerminal.Open(backend, new TerminalOptions { ImplicitStartInput = true, RespectNoColor = false }, force: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var count = 0;
        await Task.Run(async () => await session.Instance.RunAsync(root, context =>
        {
            if (count % 5 == 0) phase(context, count / 5, backend);
            Thread.Sleep(5);
            return count++ >= last * 5 ? TerminalLoopResult.Stop : TerminalLoopResult.Continue;
        }, new TerminalRunOptions(), deadline.Token));
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
    internal int Writes { get; private set; }
    internal int Reads { get; private set; }
    internal bool FailWrite { get; set; }
    internal bool FailRead { get; set; }
    internal Action? DuringWrite { get; set; }
    internal string? Written { get; private set; }

    internal ClipboardBackend() : base(TextWriter.Null, TextWriter.Null, new TerminalSize(100, 32)) { }

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
