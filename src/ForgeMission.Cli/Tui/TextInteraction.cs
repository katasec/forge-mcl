using Katasec.Forge.Terminal.Extensions;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace ForgeMission.Cli.Tui;

// Coordinates only Forge-owned native ranges. Controls keep editing; the extension keeps transport.
internal sealed class TextInteraction
{
    private readonly HashSet<Visual> _sources = [];
    private Visual? _last;
    private readonly State<Visual?> _feedbackSource = new(null);
    internal State<string> Feedback { get; } = new("");
    internal int SourceCount => _sources.Count;

    internal void Bind(Visual root) => root.PointerPressedRouted += (_, e) =>
    {
        if (e.RoutingPhase != RoutingPhase.Preview || e.Button != TerminalMouseButton.Left) return;
        for (var source = e.OriginalSource as Visual; source is not null; source = source.Parent)
            if (_sources.Contains(source)) { Claim(source); return; }
        ClearRanges();
    };

    internal void Configure(TextEditorBase editor)
    {
        _sources.Add(editor);
        editor.ConfigureClipboard(result => { Claim(editor, reset: false); Report(editor, result); });
        editor.KeyDownRouted += (_, e) => { if (e.RoutingPhase == RoutingPhase.Bubble) Claim(editor); };
        editor.TextInputRouted += (_, e) => { if (e.RoutingPhase == RoutingPhase.Bubble) Claim(editor); };
        editor.PasteRouted += (_, e) => { if (e.RoutingPhase == RoutingPhase.Bubble) Claim(editor); };
        editor.TextDocument.Changed += (_, _) => Reset();
        Decorate(editor);
    }

    internal void Register(Paragraph paragraph)
    {
        Configure(paragraph);
        paragraph.IsEnabled = true;
        _sources.Add(paragraph);
    }

    internal void Configure(Paragraph paragraph)
    {
        paragraph.ConfigureClipboard(result => { Claim(paragraph, reset: false); Report(paragraph, result); });
    }

    internal void Retire(Visual source)
    {
        _sources.Remove(source);
        ((ISelectionOwner)source).ClearSelection();
        if (source is Paragraph) source.IsEnabled = false;
        if (_last == source) _last = null;
        if (_feedbackSource.Value == source) Reset();
    }

    internal void ChangeView(Visual view, TextEditorBase composer)
    {
        ClearRanges();
        var retained = view.EnumerateVisualsDepthFirst().ToHashSet();
        foreach (var source in _sources.ToArray())
            if (source != composer && !retained.Contains(source)) Retire(source);
    }

    internal bool CopySelection()
    {
        var editor = _sources.OfType<TextEditorBase>().FirstOrDefault(source => source.HasFocus && source.HasSelection && Eligible(source));
        var target = (Visual?)editor ?? (_last is Paragraph paragraph && paragraph.HasSelection && Eligible(paragraph) ? paragraph : null);
        if (target is null) return false;
        Claim(target);
        Report(target, ClipboardText.CopySelection((ISelectionOwner)target, target.App!.Terminal));
        return true;
    }

    internal void Claim(Visual source, bool reset = true)
    {
        if (!_sources.Contains(source) || !Eligible(source)) return;
        if (reset) Reset();
        foreach (var other in _sources)
            if (other != source) ((ISelectionOwner)other).ClearSelection();
        _last = source;
    }

    internal void Report(Visual source, ClipboardResult result)
    {
        _feedbackSource.Value = source;
        Feedback.Value = result switch
        {
            ClipboardResult.Copied => "Copied",
            ClipboardResult.CopyFailed => "Copy failed",
            ClipboardResult.PasteReadFailed => "Paste failed",
            _ => "",
        };
    }

    internal string Status(string status) => Feedback.Value.Length == 0 ? status
        : status.Length == 0 ? Feedback.Value : $"{Feedback.Value} · {status}";

    internal void CheckFeedback()
    {
        if (_feedbackSource.Value is { } source && (!Eligible(source) || (!source.HasFocus && !source.IsHovered))) Reset();
    }

    internal void Reset() { _feedbackSource.Value = null; Feedback.Value = ""; }

    internal string ResultFor(Visual source) => _feedbackSource.Value == source ? Feedback.Value : "";

    private void ClearRanges()
    {
        foreach (var source in _sources) ((ISelectionOwner)source).ClearSelection();
        _last = null;
        Reset();
    }

    private static bool Eligible(Visual source)
    {
        if (source.App is null) return false;
        for (Visual? current = source; current is not null; current = current.Parent)
            if (!current.IsEnabled || !current.IsVisible) return false;
        return true;
    }

    private void Decorate(TextEditorBase editor)
    {
        foreach (var original in editor.Commands.Where(command => command.Id != "TextEditor.Copy").ToArray())
            editor.AddCommand(new Command
            {
                Id = original.Id, LabelMarkup = original.LabelMarkup, Name = original.Name,
                DescriptionMarkup = original.DescriptionMarkup, SearchText = original.SearchText,
                Gesture = original.Gesture, Sequence = original.Sequence, Importance = original.Importance,
                Presentation = original.Presentation, CanExecute = original.CanExecute, IsVisible = original.IsVisible,
                ConsumesGestureWhenUnavailable = original.ConsumesGestureWhenUnavailable, RouteGesture = original.RouteGesture,
                Execute = target => { Claim(editor); original.Execute(target); },
            });
    }
}
