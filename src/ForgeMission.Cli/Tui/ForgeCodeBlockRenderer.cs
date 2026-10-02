using ForgeMission.Cli.Tui.Graphics;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Text;

namespace ForgeMission.Cli.Tui;

// forge chat TUI (53.7, Phase 56 Tasks 3, 4 and 5b): code blocks in replies — the code wrapped on its
// fill inside the code-block ring (TileFrame), no language label, syntax-coloured by CodeColours
// (G12) when the fence names a known language, otherwise plain. The
// Markdown package has no code-block style slot, so this renderer is where those tokens are applied.
// A block ForgeMarkdown marked as a heading is drawn as that heading in Inter (HeadingImage), or as
// its pending bold text while it streams. It is created when the images arrive (ChatScreen.UseImages),
// before any reply is shown. Inside a list or quote the package wraps the returned visual in a
// left-padded Padder.
internal sealed class ForgeCodeBlockRenderer(CodeBlockStyle style, TileSet tiles, TextImages text, HeadingStyle headings)
    : IMarkdownCodeBlockRenderer
{
    public Visual? CreateVisual(in MarkdownCodeBlockRenderContext context)
    {
        var code = context.Code.TrimEnd('\n');
        if (ForgeMarkdown.TryReadHeading(context.FenceInfo, out var kind, out var pending))
            return new HeadingImage(text, kind, code, pending, headings.Fill, headings.Pending);
        var body = new Paragraph(code) { Wrap = context.Options.WrapCodeBlocks, HorizontalAlignment = Align.Stretch };
        body.Runs = code.Length == 0 ? [] : style.Colours.Runs(context.Language, code) ?? [new StyledRun(0, code.Length, style.Text)];
        return new TileFrame(body, tiles, style.Fill, Align.Stretch);
    }
}
