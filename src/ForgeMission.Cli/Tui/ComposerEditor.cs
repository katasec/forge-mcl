using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Styling;

namespace ForgeMission.Cli.Tui;

// forge chat TUI (Phase 56 Task 3): the composer's editor. XenoAtom's PromptEditor measures its
// auto-size height as if it were at most 48 columns wide (PromptEditor.MeasureCore caps the width
// it wraps at), but the composer is stretched to the full width inside its frame, so a long line
// that wraps to 3 rows on screen was measured as 5 or more, and the frame showed empty rows. This
// editor measures its height at the width it is given, the width it is arranged and drawn at.
internal sealed class ComposerEditor(PromptEditorConfig config) : PromptEditor(config)
{
    /// <summary>The prompt glyph: one narrow cell.</summary>
    public const string PromptGlyph = "›";
    private const int PromptCols = 1;

    protected override SizeHints MeasureCore(in LayoutConstraints constraints)
    {
        var hints = base.MeasureCore(in constraints);
        if (!AutoSizeHeight || constraints.MaxWidth == int.MaxValue) return hints;
        var style = GetStyle<PromptEditorStyle>();
        var separator = style.ShowPromptSeparator ? 1 : 0;
        var contentWidth = Math.Max(0, constraints.MaxWidth - style.Padding.Horizontal - PromptCols - separator);
        var height = Math.Max(1, style.Padding.Vertical + MeasureContentRowsForWidth(contentWidth));
        return SizeHints.Fixed(constraints.Clamp(new Size(hints.Natural.Width, height)));
    }
}
