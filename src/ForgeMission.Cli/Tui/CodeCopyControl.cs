using Katasec.Forge.Terminal.Extensions;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Layout;
using XenoAtom.Terminal.UI.Styling;

namespace ForgeMission.Cli.Tui;

// A reserved native header row and native Button; the payload belongs to this immutable render.
internal sealed class CodeCopyHeader : Padder
{
    internal CodeCopyButton Button { get; }

    internal CodeCopyHeader(string code, ForgeStyles styles, TextInteraction interaction)
    {
        Button = new CodeCopyButton(code, styles, interaction);
        Content = Button.Tooltip(new TextBlock(() => Button.TooltipText) { IsSelectable = false });
        Content.HorizontalAlignment = Align.End;
        HorizontalAlignment = Align.Stretch;
        MinHeight = MaxHeight = ForgeTheme.CodeCopyHeaderRows;
    }

    protected override SizeHints MeasureCore(in LayoutConstraints constraints)
    {
        Button.SetAvailableWidth(constraints.MaxWidth);
        return base.MeasureCore(constraints);
    }

    protected override void ArrangeCore(in Rectangle rectangle)
    {
        Button.SetAvailableWidth(rectangle.Width);
        base.ArrangeCore(rectangle);
    }
}

internal sealed class CodeCopyButton : Button
{
    private readonly string _code;
    private readonly TextInteraction _interaction;
    private readonly State<int> _width = new(ForgeTheme.CodeCopyWidth);
    private int _generation;
    private bool _pending;
    private bool? _enabledBeforeRetirement;

    internal CodeCopyButton(string code, ForgeStyles styles, TextInteraction interaction)
    {
        _code = code;
        _interaction = interaction;
        Content = new TextBlock(() => Label) { IsSelectable = false };
        SetStyle<ButtonStyle>(() => styles.CodeCopy(_interaction.ResultFor(this), _width.Value, HasFocus));
        ClickRouted += (_, e) => { if (e.RoutingPhase == RoutingPhase.Bubble) Copy(); };
    }

    internal string TooltipText => _interaction.ResultFor(this) switch
    {
        "Copied" => "Copied", "Copy failed" => "Copy failed", _ => "Copy code",
    };

    internal void SetAvailableWidth(int width)
    {
        _width.Value = width;
        if (width == 0) Reset();
        IsVisible = IsTabStop = width > 0;
        MinWidth = MaxWidth = width >= ForgeTheme.CodeCopyWidth ? ForgeTheme.CodeCopyWidth
            : width >= ForgeTheme.CodeCopyPaddedWidth ? ForgeTheme.CodeCopyPaddedWidth : width;
    }

    internal void Retire()
    {
        _enabledBeforeRetirement ??= IsEnabled;
        Reset();
        IsEnabled = false;
    }

    internal void Resume()
    {
        if (_enabledBeforeRetirement is not { } enabled) return;
        _enabledBeforeRetirement = null;
        IsEnabled = enabled;
    }

    protected override void OnDetachedFromApp(TerminalApp app)
    {
        Reset();
        base.OnDetachedFromApp(app);
    }

    protected override void OnHoveredChanged(bool value)
    {
        if (!value && !HasFocus) Reset();
        base.OnHoveredChanged(value);
    }

    private void Copy()
    {
        if (_pending || App is not { } app || !HasFocus || !IsEligible()) return;
        var generation = _generation;
        var parent = Parent;
        _interaction.Reset();
        _pending = true;
        try
        {
            var result = ClipboardText.CopyText(_code, app.Terminal);
            if (_generation == generation && App == app && Parent == parent && IsEligible())
                _interaction.Report(this, result);
        }
        finally { _pending = false; }
    }

    private bool IsEligible()
    {
        for (Visual? current = this; current is not null; current = current.Parent)
            if (!current.IsEnabled || !current.IsVisible) return false;
        return true;
    }

    private string Label
    {
        get
        {
            var result = _interaction.ResultFor(this);
            var glyph = result switch { "Copied" => "✓", "Copy failed" => "!", _ => "⧉" };
            return _width.Value < ForgeTheme.CodeCopyWidth ? glyph : $"{glyph} {TooltipText}";
        }
    }

    private void Reset()
    {
        _generation++;
        IsPressed = false;
        _pending = false;
        if (_interaction.ResultFor(this).Length > 0) _interaction.Reset();
    }
}
