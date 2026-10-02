using ForgeMission.Cli.Tui.Graphics;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Text;

namespace ForgeMission.Cli.Tui;

// forge chat TUI (/edit spike): the full-screen file editor that ChatScreen shows in place of the
// transcript. One row with the path (and "· new" until the first save), XenoAtom's CodeEditor with
// line numbers and the file's syntax colours (CodeColours), and one message row (saved, a save
// error, the unsaved-changes warning). Ctrl+S saves and Esc closes, by EditFile's rules; the
// commands sit on this view, so they act only while the editor has focus. The editor scrolls inside
// a ScrollViewer (the CodeEditor ignores the mouse wheel by itself).
internal sealed class FileEditor
{
    private readonly EditFile _file;
    private readonly TextDocument _document;
    private readonly Action _close;
    private readonly State<string> _header;
    private readonly State<string> _message = new("");

    public FileEditor(EditFile file, ForgeStyles styles, Action close)
    {
        _file = file;
        _close = close;
        _header = new State<string>(file.Header);
        _document = new TextDocument(file.Text);
        _document.Changed += (_, _) => OnEdited();
        Editor = BuildEditor(file.FullPath, _document);
        View = BuildView(styles, Editor);
        View.AddCommand(KeyCommand("Forge.Edit.Save", new KeyGesture(TerminalChar.CtrlS, TerminalModifiers.Ctrl), Save));
        View.AddCommand(KeyCommand("Forge.Edit.Close", new KeyGesture(TerminalKey.Escape), Escape));
    }

    /// <summary>The whole editor screen (path row, editor, message row).</summary>
    public Visual View { get; }

    /// <summary>The editor control; it takes focus when the view is shown.</summary>
    public CodeEditor Editor { get; }

    /// <summary>The text in the editor now.</summary>
    internal string Text => CopyText(_document.CurrentSnapshot);

    // ── Keys ────────────────────────────────────────────────────────────────────────────────

    private void Save()
    {
        _message.Value = _file.Save(Text);
        _header.Value = _file.Header;
    }

    private void Escape()
    {
        if (_file.Escape(Text) == EscapeOutcome.Close)
        {
            _close();
            return;
        }
        _message.Value = EditFile.UnsavedWarning;
    }

    private void OnEdited()
    {
        _file.Edited();
        _message.Value = "";
    }

    // ── Views ───────────────────────────────────────────────────────────────────────────────

    private static CodeEditor BuildEditor(string fullPath, TextDocument document)
    {
        var editor = new CodeEditor
        {
            ShowLineNumbers = true,
            HighlightCurrentLine = true,
            SyntaxHighlighter = CodeColours.EditorHighlighter(fullPath),
        };
        editor.TextDocument = document;
        return editor;
    }

    private Visual BuildView(ForgeStyles styles, CodeEditor editor) => new DockLayout()
        .Top(new TextBlock(() => _header.Value).Style(styles.Label))
        .Content(new ScrollViewer(editor.Stretch(), focusable: false)
            .IsTabStop(false)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch)
            .Style(styles.Scroll))
        .Bottom(new TextBlock(() => _message.Value).Style(styles.Progress));

    private static Command KeyCommand(string id, KeyGesture gesture, Action action) => new()
    {
        Id = id,
        LabelMarkup = string.Empty,
        Gesture = gesture,
        Execute = _ => action(),
    };

    private static string CopyText(ITextSnapshot snapshot) =>
        string.Create(snapshot.Length, snapshot, static (span, source) => source.CopyTo(0, span));
}
