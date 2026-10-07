using Katasec.Forge.Terminal.Extensions;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace ForgeMission.Cli.Tui;

// Coordinates source ownership, card-local ranges, Copy-before-Stop, and clipboard feedback.
internal sealed class TextInteraction
{
    private readonly HashSet<Visual> _sources = [];
    private readonly HashSet<ParagraphSelection> _cards = [];
    private readonly Dictionary<Paragraph, ParagraphSelection> _paragraphCards = [];
    private Visual? _last;
    private ParagraphSelection? _activeCard;
    private ParagraphSelection? _dragCard;
    private ParagraphSelection? _invalidCard;
    private readonly State<Visual?> _feedbackSource = new(null);
    internal State<string> Feedback { get; } = new("");
    internal int SourceCount => _sources.Count;

    internal void Bind(Visual root)
    {
        root.PointerPressedRouted += (_, e) => Press(root, e);
        root.PointerMovedRouted += (_, e) => Drag(root, e);
        root.PointerReleasedRouted += (_, e) => Release(root, e);
    }

    internal void RegisterCard(ParagraphSelection card) => _cards.Add(card);

    internal void RetireCard(ParagraphSelection card)
    {
        _cards.Remove(card);
        if (_activeCard == card) _activeCard = null;
        if (_dragCard == card) _dragCard = null;
        if (_invalidCard == card) _invalidCard = null;
        foreach (var paragraph in _paragraphCards.Where(pair => pair.Value == card).Select(pair => pair.Key).ToArray())
            _paragraphCards.Remove(paragraph);
    }

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

    internal void Register(Paragraph paragraph) => Register(paragraph, null);

    internal void Register(Paragraph paragraph, ParagraphSelection? card)
    {
        if (card is null) Configure(paragraph);
        else ConfigureRich(paragraph);
        paragraph.IsEnabled = true;
        _sources.Add(paragraph);
        if (card is null) return;
        _paragraphCards[paragraph] = card;
    }

    internal void Configure(Paragraph paragraph) => paragraph.ConfigureClipboard(
        result => { Claim(paragraph, reset: false); Report(paragraph, result); });

    private void ConfigureRich(Paragraph paragraph) => paragraph.ConfigureClipboard(
        () => CanCopy(paragraph),
        () => Copy(paragraph),
        result => ReportParagraph(paragraph, result));

    internal void Retire(Visual source)
    {
        _sources.Remove(source);
        ((ISelectionOwner)source).ClearSelection();
        if (source is Paragraph paragraph)
        {
            paragraph.IsEnabled = false;
            _paragraphCards.Remove(paragraph);
        }
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
        if (editor is not null)
        {
            Claim(editor);
            Report(editor, ClipboardText.CopySelection(editor, editor.App!.Terminal));
            return true;
        }
        if (_activeCard is { } card && Eligible(card) && card.TryCopy(out var text))
        {
            Report(card, ClipboardText.CopyText(text, card.App!.Terminal));
            return true;
        }
        var paragraph = _last as Paragraph;
        if (paragraph is null || !paragraph.HasSelection || !Eligible(paragraph))
            paragraph = _sources.OfType<Paragraph>().FirstOrDefault(source => source.HasSelection && Eligible(source));
        if (paragraph is null) return false;
        Claim(paragraph);
        Report(paragraph, ClipboardText.CopySelection(paragraph, paragraph.App!.Terminal));
        return true;
    }

    internal void Claim(Visual source, bool reset = true)
    {
        if (!_sources.Contains(source) || !Eligible(source)) return;
        if (reset) Reset();
        ClearCard();
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

    private void Press(Visual root, PointerEventArgs e)
    {
        if (e.RoutingPhase != RoutingPhase.Preview || e.Button != TerminalMouseButton.Left) return;
        ClearInvalidCard();
        if (e.ClickCount >= 2 || e.Kind == TerminalMouseKind.DoubleClick || (e.Modifiers & TerminalModifiers.Shift) != 0)
        {
            ClaimSource(e.OriginalSource as Visual);
            return;
        }
        var target = root.HitTest(e.UiX, e.UiY);
        var card = CardFor(target);
        if (card is null)
        {
            ClaimSource(e.OriginalSource as Visual);
            return;
        }
        Activate(card);
        if (!card.TryBegin(target!, e.UiX, e.UiY))
        {
            ClearCard();
            ClaimSource(e.OriginalSource as Visual);
            return;
        }
        _dragCard = card;
        e.Handled = true;
    }

    private void Drag(Visual root, PointerEventArgs e)
    {
        if (e.RoutingPhase != RoutingPhase.Preview || e.Kind != TerminalMouseKind.Drag || _dragCard is not { } card) return;
        var target = root.HitTest(e.UiX, e.UiY);
        if (CardFor(target) == card && card.TryExtend(target!, e.UiX, e.UiY))
        {
            e.Handled = true;
            return;
        }
        ClearCard();
        _invalidCard = card;
        e.Handled = true;
    }

    private void Release(Visual root, PointerEventArgs e)
    {
        if (e.RoutingPhase != RoutingPhase.Preview || e.Button != TerminalMouseButton.Left) return;
        if (_invalidCard is { } invalid)
        {
            invalid.Clear();
            _invalidCard = null;
            e.Handled = true;
            return;
        }
        if (_dragCard is not { } card) return;
        var target = root.HitTest(e.UiX, e.UiY);
        if (CardFor(target) == card) card.TryExtend(target!, e.UiX, e.UiY);
        else ClearCard();
        _dragCard = null;
        e.Handled = true;
    }

    private bool CanCopy(Paragraph paragraph) => _paragraphCards.TryGetValue(paragraph, out var card) && card.HasSelection
        || paragraph.HasSelection;

    private ClipboardResult Copy(Paragraph paragraph)
    {
        if (_paragraphCards.TryGetValue(paragraph, out var card) && card.TryCopy(out var text))
        {
            _activeCard = card;
            return ClipboardText.CopyText(text, paragraph.App!.Terminal);
        }
        return ClipboardText.CopySelection(paragraph, paragraph.App!.Terminal);
    }

    private void ReportParagraph(Paragraph paragraph, ClipboardResult result)
    {
        if (_paragraphCards.TryGetValue(paragraph, out var card) && card.HasSelection)
        {
            _activeCard = card;
            Report(card, result);
            return;
        }
        Claim(paragraph, reset: false);
        Report(paragraph, result);
    }

    private void Activate(ParagraphSelection card)
    {
        Reset();
        ClearCard();
        foreach (var source in _sources) ((ISelectionOwner)source).ClearSelection();
        _activeCard = card;
    }

    private void ClearRanges()
    {
        foreach (var source in _sources) ((ISelectionOwner)source).ClearSelection();
        ClearCard();
        ClearInvalidCard();
        _last = null;
        Reset();
    }

    private void ClearInvalidCard()
    {
        _invalidCard?.Clear();
        _invalidCard = null;
    }

    private void ClearCard()
    {
        _dragCard?.Clear();
        if (_activeCard is { } card && card != _dragCard) card.Clear();
        _dragCard = null;
        _activeCard = null;
    }

    private ParagraphSelection? CardFor(Visual? target)
    {
        for (var current = target; current is not null; current = current.Parent)
            if (current is ParagraphSelection card && _cards.Contains(card)) return card;
        return null;
    }

    private void ClaimSource(Visual? source)
    {
        for (var current = source; current is not null; current = current.Parent)
            if (_sources.Contains(current)) { Claim(current); return; }
        ClearRanges();
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
