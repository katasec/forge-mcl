using System.Diagnostics;
using XenoAtom.Terminal.UI;

namespace ForgeMission.Cli.Tui;

// Phase 56 Task 5: the timing and colour rules of the motion effects (fade-in, streaming caret,
// spinner), as pure functions of elapsed time, so tests can check them without a running app.
// Times are Stopwatch ticks (what XenoAtom passes to IAnimatedVisual); every constant is a
// ForgeTheme token.
internal static class Motion
{
    /// <summary>The clock the motion visuals read: Stopwatch ticks, like XenoAtom's loop clock.</summary>
    public static long Now() => Stopwatch.GetTimestamp();

    /// <summary>Milliseconds as Stopwatch ticks.</summary>
    public static long Ticks(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000);

    /// <summary>How far a fade started at <paramref name="start"/> has come at <paramref name="now"/>:
    /// 0 at the start, 1 once ForgeTheme.FadeMs has passed, eased like CSS ease-out.</summary>
    public static double FadeProgress(long start, long now)
    {
        var t = (now - start) / (double)Ticks(ForgeTheme.FadeMs);
        return t >= 1 ? 1 : t <= 0 ? 0 : EaseOut(t);
    }

    /// <summary>Whether a fade started at <paramref name="start"/> still runs at <paramref name="now"/>.</summary>
    public static bool Fading(long start, long now) => now - start < Ticks(ForgeTheme.FadeMs);

    /// <summary>CSS cubic-bezier(x1, y1, x2, y2) with the FadeEase tokens: solves x(s) = t, returns y(s).</summary>
    public static double EaseOut(double t)
    {
        double lo = 0, hi = 1, s = t;
        for (var i = 0; i < 40; i++)
        {
            s = (lo + hi) / 2;
            if (Bezier(s, ForgeTheme.FadeEaseX1, ForgeTheme.FadeEaseX2) < t) lo = s;
            else hi = s;
        }
        return Bezier(s, ForgeTheme.FadeEaseY1, ForgeTheme.FadeEaseY2);
    }

    /// <summary><paramref name="from"/> moved towards <paramref name="to"/> by <paramref name="t"/>,
    /// per channel in sRGB (how CSS opacity composites).</summary>
    public static Color Mix(Color from, Color to, double t) => Color.Mix(from, to, (float)t, ColorMixSpace.Srgb);

    /// <summary>Whether the streaming caret shows <paramref name="elapsed"/> ticks after it appeared:
    /// on for CaretBlinkMs, then off for CaretBlinkMs.</summary>
    public static bool CaretOn(long elapsed) => elapsed / Ticks(ForgeTheme.CaretBlinkMs) % 2 == 0;

    /// <summary>When the caret next turns on or off.</summary>
    public static long NextCaretToggle(long start, long now) => NextStep(start, now, Ticks(ForgeTheme.CaretBlinkMs));

    /// <summary>Which spinner frame shows <paramref name="elapsed"/> ticks after it appeared.</summary>
    public static int SpinnerFrame(long elapsed) => (int)(elapsed / SpinnerFrameTicks % ForgeTheme.SpinnerFrames);

    /// <summary>When the spinner next changes frame.</summary>
    public static long NextSpinnerFrame(long start, long now) => NextStep(start, now, SpinnerFrameTicks);

    private static long SpinnerFrameTicks => Ticks(ForgeTheme.SpinnerTurnMs / ForgeTheme.SpinnerFrames);

    private static long NextStep(long start, long now, long step) => start + ((now - start) / step + 1) * step;

    private static double Bezier(double s, double p1, double p2)
    {
        var u = 1 - s;
        return 3 * u * u * s * p1 + 3 * u * s * s * p2 + s * s * s;
    }
}
