using XenoAtom.Terminal.UI.Controls;

namespace Katasec.Forge.Terminal.Extensions;

public static class ParagraphClipboardExtensions
{
    public static void ConfigureClipboard(this Paragraph paragraph, Action<ClipboardResult> report)
    {
        ArgumentNullException.ThrowIfNull(paragraph);
        ArgumentNullException.ThrowIfNull(report);
        paragraph.IsSelectable = false;
        paragraph.ContextMenuFactory = _ =>
        {
            var text = paragraph.Text;
            var eligible = ClipboardMenus.CaptureEligibility(paragraph, () => paragraph.Text == text);
            return [ClipboardMenus.Item("Copy", paragraph, () => eligible() && paragraph.HasSelection,
                () => report(ClipboardText.CopySelection(paragraph, paragraph.App!.Terminal)))];
        };
    }
}
