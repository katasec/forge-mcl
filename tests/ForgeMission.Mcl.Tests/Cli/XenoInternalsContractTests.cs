using System.Reflection;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Animation;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Rendering;
using static ForgeMission.Tests.Cli.ForgeText;

namespace ForgeMission.Tests.Cli;

// Phase 56 Task 5, the XenoCells Type-2 exception: forge reads six XenoAtom internals through
// [UnsafeAccessor]. This pins them, and the public behaviour the motion overlays rely on, to
// XenoAtom.Terminal.UI 3.10.0: a package bump fails here first, before any overlay misbehaves.
[Collection(XenoAtomUiCollection.Name)]
public sealed class XenoInternalsContractTests
{
    private const string Placeholder = "\U0010EEEE̅̍";
    private static readonly Type Cells = Type("ForgeMission.Cli.Tui.XenoCells");
    private static readonly Color Red = Color.Rgb(200, 0, 0);
    private static readonly Color Blue = Color.Rgb(0, 0, 200);
    private static readonly Color Green = Color.Rgb(0, 200, 0);

    [Fact]
    public void The_package_is_the_pinned_version()
    {
        var version = typeof(CellBuffer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.StartsWith("3.10.0", version, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cell_reads_back_its_glyph_style_link_and_placeholder()
    {
        var buffer = new CellBuffer(10, 2);
        var style = Style.None.WithForeground(Red).WithBackground(Blue);
        buffer.SetCell(1, 0, new System.Text.Rune('A'), style);
        buffer.WriteText(2, 0, Placeholder, style);
        buffer.WriteText(4, 0, "L", style, buffer.RegisterHyperlink("https://example.com"));

        var a = Read(buffer, 1, 0);
        Assert.Equal('A', (int)Get(a, "Rune"));
        Assert.Equal(style, (Style)Get(a, "Style"));
        Assert.False((bool)Get(a, "Link"));
        Assert.False((bool)Get(a, "Placeholder"));
        Assert.True((bool)Get(Read(buffer, 2, 0), "Placeholder"));
        Assert.True((bool)Get(Read(buffer, 4, 0), "Link"));
        Assert.True((bool)Get(Read(buffer, 5, 0), "Blank"));
    }

    [Fact]
    public void The_clip_is_the_pushed_rectangle_inside_the_buffer()
    {
        var buffer = new CellBuffer(10, 4);
        buffer.PushClip(new Rectangle(2, 1, 20, 2));

        Assert.Equal(new Rectangle(2, 1, 8, 2), (Rectangle)Cells.GetMethod("Clip")!.Invoke(null, [buffer])!);
    }

    [Fact]
    public void An_opaque_overlay_keeps_the_glyph_and_link_and_a_translucent_one_drops_the_text_colour()
    {
        var buffer = new CellBuffer(4, 1);
        var token = buffer.RegisterHyperlink("https://example.com");
        buffer.WriteText(0, 0, "ab", Style.None.WithForeground(Red).WithBackground(Blue), token);

        buffer.OverlayCellStyle(0, 0, Style.None.WithForeground(Green));
        buffer.OverlayCellStyle(1, 0, Style.None.WithForeground(Green.WithAlpha(0)));

        var opaque = Read(buffer, 0, 0);
        Assert.Equal('a', (int)Get(opaque, "Rune"));
        Assert.True((bool)Get(opaque, "Link"));
        Assert.True(((Style)Get(opaque, "Style")).TryGetForeground(out var green) && green == Green);
        // Why FadeIn mixes colours itself: alpha 0 gives the background, not the cell's own red.
        Assert.True(((Style)Get(Read(buffer, 1, 0), "Style")).TryGetForeground(out var under) && under == Blue);
    }

    [Fact]
    public async Task RequestAnimation_wakes_an_animation_that_was_idle()
    {
        var backend = new VirtualTerminalBackend(initialSize: new TerminalSize(20, 4));
        using var terminal = Terminal.Open(backend, force: true);
        var idle = new IdleAnimation();
        var ticks = 0;

        ValueTask<TerminalLoopResult> Update(TerminalRunningContext context)
        {
            ticks++;
            if (ticks == 3)
            {
                idle.Next = 0;    // due now, but the app still holds its old long.MaxValue deadline
                Cells.GetMethod("RequestAnimation")!.Invoke(null, [context.App]);
            }
            return ValueTask.FromResult(idle.Advanced || ticks > 200 ? TerminalLoopResult.Stop : TerminalLoopResult.Continue);
        }

        await Task.Run(async () => await Terminal.RunAsync(idle, Update, new TerminalRunOptions())).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(idle.Advanced, "RequestAnimation did not make the app advance the animation");
    }

    [Fact]
    public async Task A_pointer_move_hovers_the_visual_under_it()
    {
        var backend = new VirtualTerminalBackend(initialSize: new TerminalSize(20, 4));
        using var terminal = Terminal.Open(backend, force: true);
        var below = new TextBlock("below");
        var hovered = false;
        var ticks = 0;

        ValueTask<TerminalLoopResult> Update(TerminalRunningContext context)
        {
            if (++ticks == 2) backend.PushEvent(new TerminalMouseEvent { Kind = TerminalMouseKind.Move, X = 1, Y = 1 });
            hovered |= below.IsHovered;
            return ValueTask.FromResult(hovered || ticks > 200 ? TerminalLoopResult.Stop : TerminalLoopResult.Continue);
        }

        await Task.Run(async () => await Terminal.RunAsync(new VStack(new TextBlock("above"), below), Update, new TerminalRunOptions()))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(hovered);
    }

    private static object Read(CellBuffer buffer, int x, int y) => Cells.GetMethod("Read")!.Invoke(null, [buffer, x, y])!;

    private static object Get(object target, string property) => target.GetType().GetProperty(property)!.GetValue(target)!;

    /// <summary>An animation that reports no next tick until told otherwise.</summary>
    private sealed class IdleAnimation : Visual, IAnimatedVisual
    {
        public long Next { get; set; } = long.MaxValue;

        public bool Advanced { get; private set; }

        long IAnimatedVisual.NextAnimationTick => Next;

        bool IAnimatedVisual.AdvanceAnimation(long timestamp)
        {
            if (Next == long.MaxValue) return false;
            Advanced = true;
            Next = long.MaxValue;
            return false;
        }

        protected override SizeHints MeasureCore(in LayoutConstraints constraints) => SizeHints.Fixed(new Size(1, 1));
    }
}
