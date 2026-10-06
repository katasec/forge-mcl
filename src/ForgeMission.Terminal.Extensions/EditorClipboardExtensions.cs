using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace Katasec.Forge.Terminal.Extensions;

public static class EditorClipboardExtensions
{
    public static void ConfigureClipboard(this TextEditorBase editor, Action<ClipboardResult> report)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(report);
        editor.IsSelectable = false;
        editor.RemoveCommand("TextEditor.Copy");
        editor.AddCommand(new Command
        {
            Id = "TextEditor.Copy", LabelMarkup = "Copy",
            Gesture = new KeyGesture(TerminalChar.CtrlC, TerminalModifiers.Ctrl),
            CanExecute = _ => editor.HasSelection, ConsumesGestureWhenUnavailable = false,
            Execute = _ => { if (editor.App is { } app) report(ClipboardText.CopySelection(editor, app.Terminal)); },
        });
        editor.ClipboardPasteHandler((TextEditorClipboardPasteContext context) =>
        {
            report(context.Text is null ? ClipboardResult.PasteReadFailed : ClipboardResult.PasteReadSucceeded);
            return null;
        });
        editor.ContextMenuFactory = _ => Menu(editor, report);
    }

    private static IEnumerable<MenuItem> Menu(TextEditorBase editor, Action<ClipboardResult> report)
    {
        var document = editor.TextDocument;
        var version = document.CurrentSnapshot.Version;
        var eligible = ClipboardMenus.CaptureEligibility(editor,
            () => ReferenceEquals(editor.TextDocument, document) && document.CurrentSnapshot.Version == version);
        var paste = editor.Commands.Single(command => command.Id == "TextEditor.Paste");
        return
        [
            ClipboardMenus.Item("Copy", editor, () => eligible() && editor.HasSelection,
                () => report(ClipboardText.CopySelection(editor, editor.App!.Terminal))),
            ClipboardMenus.Item("Paste", editor, () => eligible() && (paste.CanExecute?.Invoke(editor) ?? true),
                () => paste.Execute(editor)),
        ];
    }
}
