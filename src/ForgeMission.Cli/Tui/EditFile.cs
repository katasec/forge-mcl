namespace ForgeMission.Cli.Tui;

/// <summary>A composer line that is an <c>/edit</c> command; <see cref="Path"/> is null when no path
/// was given (the TUI shows <see cref="Usage"/>).</summary>
internal sealed record EditCommand(string? Path)
{
    public const string Usage = "usage: /edit <path>";
}

/// <summary>What Esc does in the editor.</summary>
internal enum EscapeOutcome { Close, Warn }

// forge chat TUI (/edit spike): the rules of the file editor, without the screen. A composer line
// becomes an /edit command; the path resolves against the folder forge chat was started in (a
// leading ~ is the home folder); the file is read when it exists, else the editor starts empty and
// the first save creates it. A save never creates a folder. Esc closes at once when nothing changed
// since the last save; otherwise the first Esc warns and a second Esc, with no edit between,
// discards. FileEditor is the screen over these rules.
internal sealed class EditFile
{
    private const string Command = "/edit";
    public const string UnsavedWarning = "unsaved changes — Esc again to discard, Ctrl+S to save";
    public const string Saved = "saved";

    private string _saved;
    private bool _warned;

    private EditFile(string shownPath, string fullPath, bool isNew, string text)
    {
        ShownPath = shownPath;
        FullPath = fullPath;
        IsNew = isNew;
        _saved = text;
    }

    /// <summary>The path as the user typed it.</summary>
    public string ShownPath { get; }

    public string FullPath { get; }

    /// <summary>The file did not exist when opened and has not been saved yet.</summary>
    public bool IsNew { get; private set; }

    /// <summary>The text the file holds (as read, or as last saved).</summary>
    public string Text => _saved;

    public string Header => IsNew ? $"{ShownPath} · new" : ShownPath;

    /// <summary>The <c>/edit</c> command in a composer line, or null when the line is a message.</summary>
    internal static EditCommand? Parse(string composerText)
    {
        var text = composerText.Trim();
        if (text == Command) return new EditCommand(null);
        if (!text.StartsWith(Command, StringComparison.Ordinal) || !char.IsWhiteSpace(text[Command.Length])) return null;
        return new EditCommand(text[Command.Length..].Trim());
    }

    /// <summary>The full path: a leading <c>~</c> or <c>~/</c> is the home folder; anything else
    /// is relative to <paramref name="cwd"/> (an absolute path stays as it is).</summary>
    internal static string Resolve(string path, string cwd, string home)
    {
        if (path == "~") return Path.GetFullPath(home);
        if (path.StartsWith("~/", StringComparison.Ordinal)) return Path.GetFullPath(path[2..], home);
        return Path.GetFullPath(path, cwd);
    }

    /// <summary>Opens <paramref name="path"/>: its text when the file exists, empty and new when it
    /// does not. A folder or an unreadable file throws IOException or UnauthorizedAccessException.</summary>
    internal static EditFile Open(string path, string cwd, string home)
    {
        var full = Resolve(path, cwd, home);
        if (Directory.Exists(full)) throw new IOException("it is a folder");
        return File.Exists(full)
            ? new EditFile(path, full, isNew: false, File.ReadAllText(full))
            : new EditFile(path, full, isNew: true, "");
    }

    /// <summary>Writes <paramref name="text"/> to the file and returns the message to show. A
    /// missing folder is not created.</summary>
    public string Save(string text)
    {
        var folder = Path.GetDirectoryName(FullPath);
        if (folder is not null && !Directory.Exists(folder)) return $"cannot save: folder {folder} does not exist";
        try
        {
            File.WriteAllText(FullPath, text);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return $"cannot save: {failure.Message}";
        }
        _saved = text;
        IsNew = false;
        _warned = false;
        return Saved;
    }

    /// <summary>Esc with <paramref name="text"/> in the editor: close when it matches the last
    /// save or the warning was already shown; otherwise warn.</summary>
    public EscapeOutcome Escape(string text)
    {
        if (text == _saved || _warned) return EscapeOutcome.Close;
        _warned = true;
        return EscapeOutcome.Warn;
    }

    /// <summary>The text changed: a later Esc warns again before discarding.</summary>
    public void Edited() => _warned = false;
}
