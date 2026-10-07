using ForgeMission.Cli.Tui.Graphics;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;

namespace ForgeMission.Cli.Tui;

// Zero-inset native child layout. A Markdown reply also owns its card-local rich range.
internal sealed class ParagraphSelection : Padder
{
    private readonly TextInteraction _interaction;
    private readonly bool _rich;
    private readonly HashSet<Paragraph> _registered = [];
    private readonly List<Member> _members = [];
    private Endpoint? _anchor;
    private Endpoint? _active;

    internal ParagraphSelection(Visual content, TextInteraction interaction) : base(content)
    {
        _interaction = interaction;
        _rich = content is MarkdownControl;
        HorizontalAlignment = content.HorizontalAlignment;
        if (content is Paragraph paragraph) interaction.Configure(paragraph);
        if (_rich) interaction.RegisterCard(this);
    }

    internal bool HasSelection => _anchor is { } anchor && _active is { } active && anchor != active;

    internal bool Owns(Visual visual)
    {
        for (Visual? current = visual; current is not null; current = current.Parent)
            if (ReferenceEquals(current, this)) return true;
        return false;
    }

    internal bool TryBegin(Visual target, int uiX, int uiY)
    {
        if (!TryEndpoint(target, uiX, uiY, out var endpoint)) return false;
        _anchor = _active = endpoint;
        ApplyRange();
        return true;
    }

    internal bool TryExtend(Visual target, int uiX, int uiY)
    {
        if (_anchor is null || !TryEndpoint(target, uiX, uiY, out var endpoint)) return false;
        _active = endpoint;
        ApplyRange();
        return true;
    }

    internal bool TryCopy(out string text)
    {
        text = "";
        if (!HasSelection || _anchor is not { } anchor || _active is not { } active) return false;
        var (first, last) = Ordered(anchor, active);
        for (var index = first.MemberIndex; index <= last.MemberIndex; index++)
        {
            var member = _members[index];
            var start = index == first.MemberIndex ? first.Index : 0;
            var end = index == last.MemberIndex ? last.Index : member.Text.Length;
            if (end <= start) continue;
            if (text.Length > 0) text += Separator(_members[index - 1], member);
            text += member.Text[start..end];
        }
        return text.Length > 0;
    }

    internal void Clear()
    {
        _anchor = _active = null;
        foreach (var member in _members) member.Clear();
    }

    internal void Retire()
    {
        Clear();
        _interaction.RetireCard(this);
        foreach (var paragraph in _registered) _interaction.Retire(paragraph);
        _registered.Clear();
        _members.Clear();
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
        if (_rich)
        {
            RefreshMembers();
            _interaction.RegisterCard(this);
        }
        foreach (var chrome in Content.EnumerateVisualsDepthFirst().OfType<TextBlock>()) chrome.IsSelectable = false;
        foreach (var button in Content.EnumerateVisualsDepthFirst().OfType<CodeCopyButton>())
        {
            if (button.App != App) continue;
            button.Resume();
        }
    }

    protected override void OnDetachedFromApp(TerminalApp app)
    {
        Retire();
        base.OnDetachedFromApp(app);
    }

    private void Register(Paragraph paragraph)
    {
        if (!_registered.Add(paragraph)) return;
        _interaction.Register(paragraph, _rich ? this : null);
    }

    private void RefreshMembers()
    {
        var next = Content!.EnumerateVisualsDepthFirst()
            .Where(visual => visual.App == App)
            .Select(Member.Create)
            .Where(member => member is not null)
            .Cast<Member>()
            .ToArray();
        if (_members.Select(member => member.Visual).SequenceEqual(next.Select(member => member.Visual))) return;
        Clear();
        _members.Clear();
        _members.AddRange(next);
    }

    private bool TryEndpoint(Visual target, int uiX, int uiY, out Endpoint endpoint)
    {
        if (TryMemberEndpoint(target, uiX, uiY, out endpoint)) return true;
        return TryTrailingEndpoint(target, uiX, uiY, out endpoint);
    }

    private bool TryMemberEndpoint(Visual target, int uiX, int uiY, out Endpoint endpoint)
    {
        for (var index = 0; index < _members.Count; index++)
        {
            var member = _members[index];
            if (!member.Owns(target)) continue;
            endpoint = EndpointAt(index, member, uiX, uiY);
            return true;
        }
        endpoint = default;
        return false;
    }

    private bool TryTrailingEndpoint(Visual target, int uiX, int uiY, out Endpoint endpoint)
    {
        if (Content is null || IsInteractiveDescendant(target) || !Content.Bounds.Contains(uiX, uiY))
        {
            endpoint = default;
            return false;
        }
        var candidate = _members
            .Select((member, index) => (member, index))
            .Where(item => item.member.Visual.Bounds.Y <= uiY && uiY < item.member.Visual.Bounds.Bottom
                && item.member.Visual.Bounds.Right <= uiX)
            .OrderByDescending(item => item.member.Visual.Bounds.Right)
            .FirstOrDefault();
        if (candidate.member is null)
        {
            endpoint = default;
            return false;
        }
        endpoint = EndpointAt(candidate.index, candidate.member, uiX, uiY);
        return true;
    }

    private bool IsInteractiveDescendant(Visual target)
    {
        for (Visual? current = target; current is not null && current != Content; current = current.Parent)
            if (current is Button) return true;
        return false;
    }

    private static Endpoint EndpointAt(int index, Member member, int uiX, int uiY) =>
        new(index, member.TextIndexAt(uiX - member.Visual.Bounds.X, uiY - member.Visual.Bounds.Y));

    private void ApplyRange()
    {
        if (_anchor is not { } anchor || _active is not { } active) return;
        var (first, last) = Ordered(anchor, active);
        for (var index = 0; index < _members.Count; index++)
        {
            var member = _members[index];
            if (index < first.MemberIndex || index > last.MemberIndex) { member.Clear(); continue; }
            var start = index == first.MemberIndex ? first.Index : 0;
            var end = index == last.MemberIndex ? last.Index : member.Text.Length;
            member.SetSelection(start, end);
        }
    }

    private static (Endpoint First, Endpoint Last) Ordered(Endpoint anchor, Endpoint active) =>
        anchor.MemberIndex < active.MemberIndex || anchor.MemberIndex == active.MemberIndex && anchor.Index <= active.Index
            ? (anchor, active) : (active, anchor);

    private static string Separator(Member before, Member after) => before.IsList && after.IsList ? "\n" : "\n\n";

    private readonly record struct Endpoint(int MemberIndex, int Index);

    private sealed class Member(Visual visual, string text, bool isList)
    {
        internal Visual Visual { get; } = visual;
        internal string Text { get; } = text;
        internal bool IsList { get; } = isList;

        internal static Member? Create(Visual visual) => visual switch
        {
            Paragraph paragraph => new Member(paragraph, paragraph.Text ?? "", IsListParagraph(paragraph)),
            HeadingImage heading => new Member(heading, heading.Text, false),
            _ => null,
        };

        internal bool Owns(Visual target)
        {
            for (Visual? current = target; current is not null; current = current.Parent)
                if (ReferenceEquals(current, Visual)) return true;
            return false;
        }

        internal int TextIndexAt(int localX, int localY) => Visual switch
        {
            Paragraph paragraph => XenoCells.TextIndexAt(paragraph, localX, localY),
            HeadingImage heading => heading.TextIndexAt(localX, localY),
            _ => 0,
        };

        internal void SetSelection(int anchor, int active)
        {
            switch (Visual)
            {
                case Paragraph paragraph: XenoCells.SetSelection(paragraph, anchor, active); break;
                case HeadingImage heading: heading.SetSelection(anchor, active); break;
            }
        }

        internal void Clear()
        {
            switch (Visual)
            {
                case Paragraph paragraph: ((ISelectionOwner)paragraph).ClearSelection(); break;
                case HeadingImage heading: heading.ClearSelection(); break;
            }
        }

        private static bool IsListParagraph(Paragraph paragraph) => paragraph.LinePrefix is { } prefix
            && !prefix.TrimStart().StartsWith('>');
    }
}
