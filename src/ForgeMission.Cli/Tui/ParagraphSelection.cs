using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace ForgeMission.Cli.Tui;

// Zero-inset native child layout, with a bounded registry of its currently realized Paragraphs.
internal sealed class ParagraphSelection : Padder
{
    private readonly TextInteraction _interaction;
    private readonly HashSet<Paragraph> _registered = [];

    internal ParagraphSelection(Visual content, TextInteraction interaction) : base(content)
    {
        _interaction = interaction;
        HorizontalAlignment = content.HorizontalAlignment;
        if (content is Paragraph paragraph) interaction.Configure(paragraph);
    }

    internal void Retire()
    {
        foreach (var paragraph in _registered) _interaction.Retire(paragraph);
        _registered.Clear();
        if (Content is null || App is null) return;
        foreach (var button in Content.EnumerateVisualsDepthFirst().OfType<CodeCopyButton>())
            if (button.App == App) button.Retire();
    }

    protected override void ArrangeCore(in Rectangle rectangle)
    {
        base.ArrangeCore(rectangle);
        if (Content is null || App is null) return;
        var live = Content.EnumerateVisualsDepthFirst().OfType<Paragraph>().Where(paragraph => paragraph.App == App).ToHashSet();
        foreach (var old in _registered.Except(live).ToArray())
        {
            _interaction.Retire(old);
            _registered.Remove(old);
        }
        foreach (var paragraph in live) Register(paragraph);
        foreach (var chrome in Content.EnumerateVisualsDepthFirst().OfType<TextBlock>()) chrome.IsSelectable = false;
        foreach (var button in Content.EnumerateVisualsDepthFirst().OfType<CodeCopyButton>())
            if (button.App == App) button.Resume();
    }

    protected override void OnDetachedFromApp(TerminalApp app)
    {
        Retire();
        base.OnDetachedFromApp(app);
    }

    private void Register(Paragraph paragraph)
    {
        _interaction.Register(paragraph);
        _registered.Add(paragraph);
    }
}
