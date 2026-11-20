namespace ForgeMission.Core.Resolution;

/// <summary>Writes a file so a crash or concurrent read never sees it half-written.</summary>
internal static class AtomicFile
{
    // Stages into a temp file in the SAME directory, applies the optional Unix mode before the
    // file reaches the destination (so a secret is never briefly readable by others), then
    // replaces or moves it into place. The temp file is removed on failure, best effort.
    internal static void Write(string destination, string contents, UnixFileMode? unixMode = null)
    {
        var dir = Path.GetDirectoryName(destination)!;
        var temp = Path.Combine(dir, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, contents);
            if (unixMode is { } mode && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, mode);

            if (File.Exists(destination))
                File.Replace(temp, destination, null);
            else
                File.Move(temp, destination);
        }
        catch
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best effort cleanup */ }
            }
            throw;
        }
    }
}
