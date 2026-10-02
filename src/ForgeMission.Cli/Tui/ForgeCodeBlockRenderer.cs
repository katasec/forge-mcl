using ForgeMission.Cli.Tui.Graphics;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Text;

namespace ForgeMission.Cli.Tui;

// forge chat TUI (53.7, Phase 56 Task 3): code blocks in replies — the code wrapped on its fill
// inside the code-block ring (TileFrame), no language label, no syntax highlighting. The Markdown
// package has no code-block style slot, so this renderer is where those tokens are applied. It is
// created when the tile sets arrive (ChatScreen.UseTiles), before any reply is shown. Inside a
// list or quote the package wraps the returned visual in a left-padded Padder.
internal sealed class ForgeCodeBlockRenderer(CodeBlockStyle style, TileSet tiles) : IMarkdownCodeBlockRenderer
{
    public Visual? CreateVisual(in MarkdownCodeBlockRenderContext context)
    {
        var code = context.Code.TrimEnd('\n');
        var body = new Paragraph(code) { Wrap = context.Options.WrapCodeBlocks, HorizontalAlignment = Align.Stretch };
        body.Runs = code.Length == 0 ? [] : [new StyledRun(0, code.Length, style.Text)];
        return new TileFrame(body, tiles, style.Fill, Align.Stretch);
    }
}
