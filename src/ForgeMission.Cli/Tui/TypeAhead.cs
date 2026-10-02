using System.Runtime.InteropServices;

namespace ForgeMission.Cli.Tui;

// forge chat TUI (Phase 56 Task 3 review): keys typed before the TUI takes the terminal — during
// sign-in, the --hands prompt or opening the conversation — wait in the terminal's input queue.
// XenoAtom reads them when it starts, the Enter among them does nothing (no conversation yet), and
// the letters became composer text ("y Please reply…"). The TUI discards that queue as it starts,
// so only keys typed into the running TUI reach the composer. The terminal's replies to the TUI's
// own probes are not affected: the probes are sent after this, inside Terminal.Run.
internal static class TypeAhead
{
    private const int StandardInput = 0;

    /// <summary>Drops every byte typed but not yet read from the terminal. Call just before the
    /// TUI starts; stdin is a terminal there (ForgeChat.UsesTui). Windows has no tty queue here and
    /// is left as is.</summary>
    public static void Discard()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = OperatingSystem.IsMacOS()
            ? FlushMacOS(StandardInput, MacOSInputQueue)
            : FlushLinux(StandardInput, LinuxInputQueue);
        if (result != 0)
            throw new IOException($"Could not discard keys typed before forge chat started (errno {Marshal.GetLastPInvokeError()}).");
    }

    // TCIFLUSH: 1 on macOS (sys/termios.h), 0 on Linux (asm-generic/termbits.h).
    private const int MacOSInputQueue = 1;
    private const int LinuxInputQueue = 0;

    // DllImport, not LibraryImport: both arguments are blittable, so no marshalling code (and no
    // unsafe code in the project) is needed; Native AOT binds these directly.
    [DllImport("libSystem.dylib", EntryPoint = "tcflush", SetLastError = true)]
    private static extern int FlushMacOS(int fd, int queue);

    [DllImport("libc.so.6", EntryPoint = "tcflush", SetLastError = true)]
    private static extern int FlushLinux(int fd, int queue);
}
