using XenoAtom.Terminal;
using XenoAtom.Terminal.UI.Input;

namespace Katasec.Forge.Terminal.Extensions;

/// <summary>Extracts a native range once and writes exact text once through native transport.</summary>
public static class ClipboardText
{
    public static ClipboardResult CopySelection(ISelectionOwner source, TerminalInstance terminal)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(terminal);
        if (!source.HasSelection) return ClipboardResult.NoSelection;
        if (!source.TryCopySelection(out var text) || text.Length == 0) return ClipboardResult.CopyFailed;
        return CopyText(text, terminal);
    }

    public static ClipboardResult CopyText(string text, TerminalInstance terminal)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(terminal);
        return terminal.Clipboard.TrySetText(text) ? ClipboardResult.Copied : ClipboardResult.CopyFailed;
    }
}
