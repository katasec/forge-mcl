using System.Diagnostics;
using XenoAtom.Terminal.UI;
using static ForgeMission.Tests.Cli.ForgeText;

namespace ForgeMission.Tests.Cli;

// Phase 56 Task 5: the motion rules as pure functions of time — the fade's CSS ease-out, the colour
// mix (sRGB, like CSS opacity), the caret's 500 ms blink and the spinner's 10 frames per 0.8 s.
public sealed class MotionTests
{
    private static readonly Type MotionType = Type("ForgeMission.Cli.Tui.Motion");

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.25, 0.37814)]
    [InlineData(0.5, 0.68464)]
    [InlineData(0.75, 0.90654)]
    [InlineData(1.0, 1.0)]
    public void The_fade_eases_out_like_css(double t, double expected)
    {
        // cubic-bezier(0, 0, .58, 1), solved numerically.
        Assert.Equal(expected, (double)Static("EaseOut", t)!, 3);
    }

    [Fact]
    public void A_fade_runs_220_ms_from_nothing_to_full()
    {
        var start = 1_000_000L;

        Assert.Equal(0.0, Progress(start, start));
        Assert.InRange(Progress(start, start + Ms(110)), 0.5, 0.95);
        Assert.Equal(1.0, Progress(start, start + Ms(220)));
        Assert.True(Fading(start, start + Ms(219)));
        Assert.False(Fading(start, start + Ms(220)));
    }

    [Fact]
    public void The_mix_goes_from_the_background_to_the_text_colour_in_srgb()
    {
        var background = Color.Rgb(255, 255, 255);
        var text = Color.Rgb(16, 29, 52);

        Assert.Equal((255, 255, 255), Rgb(Mix(background, text, 0)));
        Assert.Equal((16, 29, 52), Rgb(Mix(background, text, 1)));
        var half = Rgb(Mix(background, text, 0.5));
        Assert.InRange(half.R, 134, 137);     // sRGB midpoint of 255 and 16, not linear light (~188)
    }

    [Fact]
    public void The_caret_is_on_for_500_ms_then_off_for_500_ms()
    {
        Assert.True(CaretOn(0));
        Assert.True(CaretOn(Ms(499)));
        Assert.False(CaretOn(Ms(500)));
        Assert.False(CaretOn(Ms(999)));
        Assert.True(CaretOn(Ms(1000)));
        Assert.Equal(Ms(500), (long)Static("NextCaretToggle", 0L, Ms(10))!);
    }

    [Fact]
    public void The_spinner_shows_ten_frames_of_80_ms_per_turn()
    {
        Assert.Equal(0, Frame(0));
        Assert.Equal(0, Frame(Ms(79)));
        Assert.Equal(1, Frame(Ms(80)));
        Assert.Equal(9, Frame(Ms(799)));
        Assert.Equal(0, Frame(Ms(800)));
        Assert.Equal(Ms(160), (long)Static("NextSpinnerFrame", 0L, Ms(81))!);
    }

    private static object? Static(string method, params object?[] args) => MotionType.GetMethod(method)!.Invoke(null, args);

    private static long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000);

    private static double Progress(long start, long now) => (double)Static("FadeProgress", start, now)!;

    private static bool Fading(long start, long now) => (bool)Static("Fading", start, now)!;

    private static bool CaretOn(long elapsed) => (bool)Static("CaretOn", elapsed)!;

    private static int Frame(long elapsed) => (int)Static("SpinnerFrame", elapsed)!;

    private static Color Mix(Color from, Color to, double t) => (Color)Static("Mix", from, to, t)!;

    private static (int R, int G, int B) Rgb(Color c) => (c.R, c.G, c.B);
}
