using XenoAtom.Terminal.UI.Controls;

namespace Katasec.Forge.Terminal.Extensions;

public static class ParagraphClipboardExtensions
{
    public static void ConfigureClipboard(this Paragraph paragraph, Action<ClipboardResult> report)
        => ConfigureClipboard(
            paragraph,
            () => paragraph.HasSelection,
            () => ClipboardText.CopySelection(paragraph, paragraph.App!.Terminal),
            report);

    /// <summary>Configures the fixed native menu while a caller retains selection composition ownership.</summary>
    public static void ConfigureClipboard(this Paragraph paragraph, Func<bool> available,
        Func<ClipboardResult> copy, Action<ClipboardResult> report)
    {
        ArgumentNullException.ThrowIfNull(paragraph);
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(report);
        paragraph.IsSelectable = false;
        paragraph.ContextMenuFactory = _ =>
        {
            var text = paragraph.Text;
            var eligible = ClipboardMenus.CaptureEligibility(paragraph, () => paragraph.Text == text);
            return [ClipboardMenus.Item("Copy", paragraph, () => eligible() && available(),
                () => report(copy()))];
        };
    }
}
