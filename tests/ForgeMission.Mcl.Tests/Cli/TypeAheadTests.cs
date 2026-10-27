using System.Reflection;
using System.Text.RegularExpressions;

namespace ForgeMission.Tests.Cli;

// forge chat TUI (Phase 56 Task 3 review): TypeAhead.Discard flushes the terminal's input queue
// through libc tcflush. The test host's stdin is not a terminal, so tcflush fails (ENODEV or ENOTTY,
// by platform) and Discard reports that errno: a non-zero errno pins the native call's SetLastError.
public sealed class TypeAheadTests
{
    [Fact]
    public void Discard_reports_the_errno_when_stdin_is_not_a_terminal()
    {
        Assert.True(Console.IsInputRedirected, "The test host's stdin is expected to be redirected.");
        if (OperatingSystem.IsWindows())
        {
            Discard();
            return;
        }

        var error = Assert.Throws<IOException>(Discard);

        var errno = Regex.Match(error.Message, @"^Could not discard keys typed before forge chat started \(errno (\d+)\)\.$");
        Assert.True(errno.Success, error.Message);
        Assert.NotEqual("0", errno.Groups[1].Value);
    }

    private static void Discard()
    {
        try { ForgeText.Type("ForgeMission.Cli.Tui.TypeAhead").GetMethod("Discard")!.Invoke(null, null); }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is { } inner) { throw inner; }
    }
}
