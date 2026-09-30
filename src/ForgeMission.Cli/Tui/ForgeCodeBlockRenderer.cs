using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;

namespace ForgeMission.Cli.Tui;

// forge chat TUI (53.7): code blocks in replies — a rounded box in the code-block tokens, the code
// wrapped inside, no language label, no syntax highlighting. The Markdown package has no
// code-block style slot, so this renderer is where those tokens are applied.
internal sealed class ForgeCodeBlockRenderer(CodeBlockStyle style) : IMarkdownCodeBlockRenderer
{
    public Visual? CreateVisual(in MarkdownCodeBlockRenderContext context)
    {
        var code = context.Code.TrimEnd('\n');
        var body = new Paragraph(code) { Wrap = context.Options.WrapCodeBlocks, HorizontalAlignment = Align.Stretch };
        body.Runs = code.Length == 0 ? [] : [new StyledRun(0, code.Length, style.Text)];

        return new Border(new Padder(body) { Padding = new Thickness(1, 0, 1, 0), HorizontalAlignment = Align.Stretch })
        {
            HorizontalAlignment = Align.Stretch,
        }.Style(BorderStyle.Rounded with
        {
            BorderCellStyle = style.Border,
            BackgroundStyle = style.Fill,
            HighlightOnFocusWithin = false,
        });
    }
}
