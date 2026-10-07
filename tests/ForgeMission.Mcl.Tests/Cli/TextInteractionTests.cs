using System.Reflection;
using ForgeMission.Tests.TerminalInteraction;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Text;

namespace ForgeMission.Tests.Cli;

[Collection(XenoAtomUiCollection.Name)]
public sealed class TextInteractionTests
{
    [Theory]
    [InlineData("composer", false)]
    [InlineData("composer", true)]
    [InlineData("file", false)]
    [InlineData("file", true)]
    [InlineData("paragraph", false)]
    [InlineData("paragraph", true)]
    public async Task Actual_ChatTui_Copy_command_never_stops_selected_text_and_edit_is_isolated(string source, bool failed)
    {
        using var session = new CancellationTokenSource();
        var (tui, screen, policy, composer, root) = ActiveChat(session);
        var editor = composer;
        if (source == "file") editor = FileView(screen, policy);
        else editor.TextDocument = new TextDocument("alpha beta");
        if (source == "paragraph") Show(screen, New("YouBlock", "alpha beta", DateTimeOffset.UnixEpoch));
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            if (phase == 1)
            {
                backend.FailWrite = failed;
                context.App.Focus(editor);
                if (source == "paragraph") TerminalInteractionTestHost.Drag(backend, root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Single(), 6, 10);
                else TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlA);
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlC);
            }
            if (phase == 2)
            {
                Assert.Equal(1, backend.Writes);
                Assert.False(StopRequested(tui));
                Assert.Equal(failed ? "Copy failed" : "Copied", Feedback(policy));
                ((ISelectionOwner)editor).ClearSelection();
                foreach (var paragraph in root.EnumerateVisualsDepthFirst().OfType<Paragraph>()) ((ISelectionOwner)paragraph).ClearSelection();
                context.App.Focus(editor);
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlC);
            }
            if (phase == 3)
            {
                Assert.Equal(source != "file", StopRequested(tui));
                Assert.Equal(1, backend.Writes);
            }
        }, last: 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_root_consumes_failed_or_empty_extraction_without_transport_or_Stop(bool empty)
    {
        using var session = new CancellationTokenSource();
        var (tui, screen, policy, _, root) = ActiveChat(session);
        var editor = new ExtractionFaultEditor(empty) { Text = "alpha beta" };
        Call(policy, "Configure", editor);
        Call(screen, "ShowEditor", editor, false);
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            if (phase == 1)
            {
                context.App.Focus(editor);
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlA);
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlC);
            }
            if (phase == 2)
            {
                Assert.True(editor.HasSelection);
                Assert.Equal(1, editor.Extractions);
                Assert.Equal(0, backend.Writes);
                Assert.False(StopRequested(tui));
                Assert.Equal("Copy failed", Feedback(policy));
                editor.CaretIndex = 0;
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlC);
            }
            if (phase == 3) { Assert.True(StopRequested(tui)); Assert.Equal(0, backend.Writes); }
        }, last: 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_decorated_native_command_keeps_nondefault_metadata_and_executes_once(bool codeEditor)
    {
        TextEditorBase editor = codeEditor ? new CodeEditor() : new PromptEditor();
        editor.TextDocument = new TextDocument("alpha beta");
        var calls = new Dictionary<string, int>();
        var originals = CommandsWithMetadata(editor, calls);
        Call(New("TextInteraction"), "Configure", editor);
        foreach (var original in originals)
            AssertMetadata(original, editor.Commands.Single(command => command.Id == original.Id));
        await TerminalInteractionTestHost.Run(editor, (context, phase, backend) =>
        {
            if (phase == 1)
            {
                context.App.Focus(editor);
                foreach (var original in originals)
                {
                    editor.Commands.Single(command => command.Id == original.Id).Execute(editor);
                    Assert.Equal(1, calls[original.Id]);
                }
                editor.TextDocument = new TextDocument("alpha beta");
            }
            if (phase == 2)
            {
                editor.CaretIndex = 0;
                editor.Commands.Single(command => command.Id == "TextEditor.SelectAll").Execute(editor);
                Assert.True(editor.TryCopySelection(out var selected));
                Assert.Equal("alpha beta", selected);
                Assert.Equal(2, calls["TextEditor.SelectAll"]);
                backend.TrySetClipboardText("replacement");
                editor.Commands.Single(command => command.Id == "TextEditor.Paste").Execute(editor);
                Assert.Equal("replacement", EditorText(editor));
                editor.Commands.Single(command => command.Id == "TextEditor.Undo").Execute(editor);
                Assert.Equal("alpha beta", EditorText(editor));
            }
        }, last: 3);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Keyboard_select_all_copy_before_click_consumes_failures(bool codeEditor, bool failed)
    {
        var policy = New("TextInteraction");
        TextEditorBase editor = codeEditor ? new CodeEditor() : new PromptEditor();
        editor.TextDocument = new TextDocument("alpha beta\nsecond");
        var native = editor.Commands.Single(command => command.Id == "TextEditor.SelectAll");
        Call(policy, "Configure", editor);
        var decorated = editor.Commands.Single(command => command.Id == native.Id);
        AssertMetadata(native, decorated);
        var root = new Padder(editor);
        Call(policy, "Bind", root);
        var stops = 0;
        root.AddCommand(new Command
        {
            Id = "Test.CopyOrStop", LabelMarkup = "", Gesture = new KeyGesture(TerminalChar.CtrlC, TerminalModifiers.Ctrl),
            Execute = _ => { if (!(bool)Call(policy, "CopySelection")!) stops++; },
        });
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            if (phase == 1)
            {
                backend.FailWrite = failed;
                context.App.Focus(editor);
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlA);
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlC);
            }
            if (phase == 2)
            {
                Assert.True(editor.HasSelection);
                Assert.Equal(1, backend.Writes);
                Assert.Equal(0, stops);
                Assert.Equal(failed ? "Copy failed" : "Copied", Feedback(policy));
                ((ISelectionOwner)editor).ClearSelection();
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlC);
            }
            if (phase == 3) { Assert.Equal(1, stops); Assert.Equal(1, backend.Writes); }
        }, last: 4);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("key")]
    [InlineData("paste")]
    [InlineData("menu")]
    public async Task Editor_claim_clears_other_native_ranges_without_replacing_editing(string input)
    {
        var policy = New("TextInteraction");
        var paragraph = new Paragraph("alpha beta");
        var wrapper = (Visual)New("ParagraphSelection", paragraph, policy);
        var editor = new PromptEditor { Text = "draft" };
        Call(policy, "Configure", editor);
        var root = new DockLayout().Top(wrapper).Content(editor);
        Call(policy, "Bind", root);
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            if (phase == 1) TerminalInteractionTestHost.Drag(backend, paragraph, 6, 10);
            if (phase == 2)
            {
                Assert.True(paragraph.HasSelection);
                context.App.Focus(editor);
                switch (input)
                {
                    case "text": backend.PushEvent(new TerminalTextEvent { Text = "x" }); break;
                    case "key": TerminalInteractionTestHost.Key(backend, TerminalKey.Left); break;
                    case "paste": backend.PushEvent(new TerminalPasteEvent { Text = "x" }); break;
                    case "menu":
                        backend.TrySetClipboardText("x");
                        editor.ContextMenuFactory!(editor).Last().Command!.Execute(editor);
                        break;
                }
            }
            if (phase == 3)
            {
                Assert.False(paragraph.HasSelection);
                Assert.Equal(input == "key" ? "draft" : "xdraft", editor.Text);
                Assert.Equal(input == "menu" ? 1 : 0, backend.Reads);
            }
        }, last: 4);
    }

    [Theory]
    [InlineData(TerminalKey.Left)]
    [InlineData(TerminalKey.Home)]
    public async Task Native_keyboard_range_and_word_selection_need_no_prior_click(TerminalKey key)
    {
        var editor = new PromptEditor { Text = "alpha beta" };
        var policy = New("TextInteraction");
        Call(policy, "Configure", editor);
        await TerminalInteractionTestHost.Run(editor, (context, phase, backend) =>
        {
            if (phase == 1)
            {
                context.App.Focus(editor);
                TerminalInteractionTestHost.Key(backend, TerminalKey.End);
                var navigation = key == TerminalKey.Left && OperatingSystem.IsMacOS() ? TerminalModifiers.Alt : TerminalModifiers.Ctrl;
                TerminalInteractionTestHost.Key(backend, key, TerminalModifiers.Shift | navigation);
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlC);
            }
            if (phase == 2) { Assert.True(editor.HasSelection); Assert.Equal(1, backend.Writes); Assert.Equal(key == TerminalKey.Left ? "beta" : "alpha beta", backend.Written); }
        }, last: 3);
    }

    [Fact]
    public async Task Real_Markdown_registration_retires_before_replacement_and_detach()
    {
        var policy = New("TextInteraction");
        var markdown = new MarkdownControl("alpha beta\n\n> quote\n\n- list\n\n<table><tr><td>cell</td></tr></table>");
        var wrapper = (Visual)New("ParagraphSelection", markdown, policy);
        var slot = new Padder(wrapper);
        Command? stale = null;
        Paragraph? old = null;
        var routedCopies = 0;
        Call(policy, "Bind", slot);
        slot.AddCommand(new Command
        {
            Id = "Test.PreLayoutCopy", LabelMarkup = "", Gesture = new KeyGesture(TerminalChar.CtrlC, TerminalModifiers.Ctrl),
            Execute = _ =>
            {
                routedCopies++;
                Assert.False((bool)Call(policy, "CopySelection")!);
                Assert.False(stale!.CanExecute!(old!));
                stale.Execute(old!);
            },
        });
        await TerminalInteractionTestHost.Run(slot, (context, phase, backend) =>
        {
            var live = markdown.EnumerateVisualsDepthFirst().OfType<Paragraph>().Where(p => p.App == context.App).ToArray();
            if (phase == 1)
            {
                Assert.NotEmpty(live);
                Assert.All(live, p => { Assert.False(p.IsSelectable); Assert.NotNull(p.ContextMenuFactory); });
                old = live.First();
                TerminalInteractionTestHost.Drag(backend, old, 6, 10);
            }
            if (phase == 2)
            {
                stale = old!.ContextMenuFactory!(old).Single().Command;
                Assert.True(stale!.CanExecute!(old));
                context.App.Post(() =>
                {
                    Call(wrapper, "Retire");
                    markdown.Markdown = "replacement text";
                    Assert.False(old.IsEnabled);
                });
                TerminalInteractionTestHost.Drag(backend, old, 0, 4);
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlC);
                Thread.Sleep(50);
            }
            if (phase == 3)
            {
                Assert.All(live, p => Assert.False(p.IsSelectable));
                Assert.Equal(1, routedCopies);
                Assert.Equal(0, backend.Writes);
                Assert.DoesNotContain(old, live);
                Assert.Equal(live.Length, (int)Property(policy, "SourceCount"));
                slot.Content = null;
            }
            if (phase == 4)
            {
                Assert.Equal(0, (int)Property(policy, "SourceCount"));
                stale!.Execute(old!);
                Assert.Equal(0, backend.Writes);
            }
        });
    }

    [Fact]
    public async Task Rich_assistant_card_drag_copies_one_canonical_range_across_paragraphs()
    {
        var policy = New("TextInteraction");
        var markdown = new MarkdownControl("first paragraph\n\n1. second item\n2. third item\n\n> quoted paragraph\n\n```text\ncode body\n```");
        var wrapper = (Visual)New("ParagraphSelection", markdown, policy);
        var root = new Padder(wrapper);
        Call(policy, "Bind", root);
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            var paragraphs = markdown.EnumerateVisualsDepthFirst().OfType<Paragraph>().Where(paragraph => paragraph.App == context.App).ToArray();
            if (phase == 1)
            {
                Assert.True(paragraphs.Length >= 4);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Down, paragraphs[0]);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Drag, paragraphs[^1], 15);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Up, paragraphs[^1], 15);
            }
            if (phase == 2)
            {
                Assert.True((bool)Call(policy, "CopySelection")!);
                Assert.Equal("first paragraph\n\nsecond item\nthird item\nquoted paragr", backend.Written);
            }
        }, last: 3);
    }

    [Fact]
    public async Task Rich_assistant_card_drag_to_same_row_trailing_body_space_copies_nearest_member()
    {
        var policy = New("TextInteraction");
        var markdown = new MarkdownControl("short body");
        var wrapper = (Visual)New("ParagraphSelection", markdown, policy);
        var root = new Padder(wrapper) { HorizontalAlignment = Align.Stretch };
        Call(policy, "Bind", root);
        Paragraph? paragraph = null;
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            if (phase == 1)
            {
                paragraph = markdown.EnumerateVisualsDepthFirst().OfType<Paragraph>()
                    .Single(source => source.App == context.App);
                paragraph.HorizontalAlignment = Align.Start;
            }
            if (phase == 2)
            {
                Assert.True(paragraph!.Bounds.Right < markdown.Bounds.Right);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Down, paragraph);
                backend.PushEvent(new TerminalMouseEvent
                {
                    Kind = TerminalMouseKind.Drag, Button = TerminalMouseButton.Left,
                    X = paragraph.Bounds.Right + 1, Y = paragraph.Bounds.Y,
                });
                backend.PushEvent(new TerminalMouseEvent
                {
                    Kind = TerminalMouseKind.Up, Button = TerminalMouseButton.Left,
                    X = paragraph.Bounds.Right + 1, Y = paragraph.Bounds.Y,
                });
            }
            if (phase == 3)
            {
                Assert.True((bool)Call(policy, "CopySelection")!);
                Assert.Equal("short body", backend.Written);
            }
        }, last: 4);
    }

    [Fact]
    public async Task Rich_assistant_card_trailing_fallback_prefers_the_rightmost_same_row_member()
    {
        var policy = New("TextInteraction");
        var markdown = new MarkdownControl("left\n\nright");
        var wrapper = (Visual)New("ParagraphSelection", markdown, policy);
        var root = new Padder(wrapper) { HorizontalAlignment = Align.Stretch };
        Call(policy, "Bind", root);
        await TerminalInteractionTestHost.Run(root, (context, phase, _) =>
        {
            if (phase != 1) return;
            var members = markdown.EnumerateVisualsDepthFirst().OfType<Paragraph>()
                .Where(paragraph => paragraph.App == context.App).ToArray();
            var left = members.Single(paragraph => paragraph.Text == "left");
            var right = members.Single(paragraph => paragraph.Text == "right");
            Assert.True((bool)Call(wrapper, "TryBegin", left, left.Bounds.X, left.Bounds.Y)!);
            left.Arrange(new Rectangle(markdown.Bounds.X, markdown.Bounds.Y, left.Text!.Length, 1));
            right.Arrange(new Rectangle(markdown.Bounds.X + 10, markdown.Bounds.Y, right.Text!.Length, 1));
            var x = right.Bounds.Right + 1;

            Assert.True(x < markdown.Bounds.Right);
            Assert.True((bool)Call(wrapper, "TryExtend", markdown, x, right.Bounds.Y)!);
            Assert.True(right.HasSelection);
            Assert.True(right.TryCopySelection(out var copied));
            Assert.Equal("right", copied);
        }, last: 2);
    }

    [Fact]
    public async Task Active_chat_rich_drag_rejects_card_padding()
    {
        using var session = new CancellationTokenSource();
        var (_, screen, policy, _, root) = ActiveChat(session);
        Show(screen, New("ParticipantCard", "Answerer", "body text", "mission", DateTimeOffset.UnixEpoch, false));
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            var body = root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Single(paragraph => paragraph.Text == "body text");
            var card = root.EnumerateVisualsDepthFirst().Single(visual => visual.GetType().Name == "ParagraphSelection");
            var x = card.Bounds.X - 1;
            if (phase == 1)
            {
                Assert.False((bool)Call(card, "Owns", root.HitTest(x, body.Bounds.Y))!);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Down, body);
                backend.PushEvent(new TerminalMouseEvent { Kind = TerminalMouseKind.Drag, Button = TerminalMouseButton.Left, X = x, Y = body.Bounds.Y });
                backend.PushEvent(new TerminalMouseEvent { Kind = TerminalMouseKind.Up, Button = TerminalMouseButton.Left, X = x, Y = body.Bounds.Y });
            }
            if (phase == 2) Assert.False((bool)Call(policy, "CopySelection")!);
        }, last: 3);
    }

    [Fact]
    public async Task Active_chat_rich_drag_rejects_inter_block_gap()
    {
        using var session = new CancellationTokenSource();
        var (_, screen, policy, _, root) = ActiveChat(session);
        Show(screen, New("ParticipantCard", "Answerer", "first body\n\nsecond body", "mission", DateTimeOffset.UnixEpoch, false));
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            var first = root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Single(paragraph => paragraph.Text == "first body");
            var second = root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Single(paragraph => paragraph.Text == "second body");
            var card = root.EnumerateVisualsDepthFirst().Single(visual => visual.GetType().Name == "ParagraphSelection");
            var y = first.Bounds.Bottom;
            if (phase == 1)
            {
                Assert.True(y < second.Bounds.Y);
                var target = root.HitTest(first.Bounds.X, y)!;
                Assert.True((bool)Call(card, "Owns", target)!);
                Assert.IsNotType<Paragraph>(target);
                Assert.True((bool)Call(card, "TryBegin", first, first.Bounds.X, first.Bounds.Y)!);
                Assert.False((bool)Call(card, "TryExtend", target, first.Bounds.X, y)!);
                Call(card, "Clear");
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Down, first);
                backend.PushEvent(new TerminalMouseEvent { Kind = TerminalMouseKind.Drag, Button = TerminalMouseButton.Left, X = first.Bounds.X, Y = y });
                backend.PushEvent(new TerminalMouseEvent { Kind = TerminalMouseKind.Up, Button = TerminalMouseButton.Left, X = first.Bounds.X, Y = y });
            }
            if (phase == 2) Assert.False((bool)Call(policy, "CopySelection")!);
        }, last: 3);
    }

    [Fact]
    public async Task Active_chat_rich_drag_rejects_another_assistant_card()
    {
        using var session = new CancellationTokenSource();
        var (_, screen, policy, _, root) = ActiveChat(session);
        Show(screen,
            New("ParticipantCard", "Answerer", "first card", "mission", DateTimeOffset.UnixEpoch, false),
            New("ParticipantCard", "Answerer", "second card", "mission", DateTimeOffset.UnixEpoch, false));
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            var first = root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Single(paragraph => paragraph.Text == "first card");
            var second = root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Single(paragraph => paragraph.Text == "second card");
            if (phase == 1)
            {
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Down, first);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Drag, second);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Up, second);
            }
            if (phase == 2) Assert.False((bool)Call(policy, "CopySelection")!);
        }, last: 3);
    }

    [Fact]
    public async Task Active_chat_assistant_card_drag_copies_real_code_body()
    {
        using var session = new CancellationTokenSource();
        var (_, screen, policy, _, root) = ActiveChat(session);
        Show(screen, New("ParticipantCard", "Answerer", "body text\n\n```text\ncode body\n```", "mission", DateTimeOffset.UnixEpoch, false));
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            var paragraphs = root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Where(paragraph => paragraph.App == context.App).ToArray();
            var body = paragraphs.Single(paragraph => paragraph.Text == "body text");
            var code = paragraphs.Single(paragraph => paragraph.Text == "code body");
            if (phase == 1)
            {
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Down, body);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Drag, code, 4);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Up, code, 4);
            }
            if (phase == 2)
            {
                Assert.True((bool)Call(policy, "CopySelection")!);
                Assert.Contains("body text", backend.Written);
                Assert.Contains("code", backend.Written);
            }
        }, last: 3);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task Active_chat_assistant_card_drag_copies_logical_heading_with_selection_overlay(string theme)
    {
        using var session = new CancellationTokenSource();
        var (_, screen, policy, _, root) = ActiveChat(session, theme);
        Show(screen, New("ParticipantCard", "Answerer", "## Heading text\n\nbody text", "mission", DateTimeOffset.UnixEpoch, false));
        var frame = new NativeFrame();
        await TerminalInteractionTestHost.Run(new ZStack(root, frame), (context, phase, backend) =>
        {
            var heading = root.EnumerateVisualsDepthFirst().Single(visual => visual.GetType().Name == "HeadingImage");
            var body = root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Single(paragraph => paragraph.Text == "body text");
            if (phase == 1)
            {
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Down, heading);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Drag, body, 4);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Up, body, 4);
            }
            if (phase == 2)
            {
                Assert.True((bool)Property(heading, "HasSelection"));
                Assert.True((bool)Call(policy, "CopySelection")!);
                Assert.Contains("Heading text\n\nbody", backend.Written);
                var selection = ForgeText.Get<Color>(ForgeText.Theme(theme), "Selection").ToHexString();
                Assert.Contains(selection, string.Join('\n', frame.Lines), StringComparison.OrdinalIgnoreCase);
            }
        }, last: 3);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task Active_chat_composer_and_body_share_the_theme_selection_token(string theme)
    {
        using var session = new CancellationTokenSource();
        var (_, screen, _, composer, root) = ActiveChat(session, theme);
        composer.TextDocument = new TextDocument("composer text");
        Show(screen, New("ParticipantCard", "Answerer", "body text", "mission", DateTimeOffset.UnixEpoch, false));
        var frame = new NativeFrame();
        await TerminalInteractionTestHost.Run(new ZStack(root, frame), (context, phase, backend) =>
        {
            var selection = ForgeText.Get<Color>(ForgeText.Theme(theme), "Selection").ToHexString();
            if (phase == 1)
            {
                context.App.Focus(composer);
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlA);
            }
            if (phase == 2)
            {
                Assert.True(composer.HasSelection);
                Assert.Contains(selection, string.Join('\n', frame.Lines), StringComparison.OrdinalIgnoreCase);
                ((ISelectionOwner)composer).ClearSelection();
                var body = root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Single(paragraph => paragraph.Text == "body text");
                TerminalInteractionTestHost.Drag(backend, body, 0, body.Text!.Length);
            }
            if (phase == 3)
            {
                var body = root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Single(paragraph => paragraph.Text == "body text");
                Assert.True(body.HasSelection);
                Assert.Contains(selection, frame.Lines[body.Bounds.Y], StringComparison.OrdinalIgnoreCase);
            }
        }, last: 4);
    }

    [Fact]
    public async Task Active_chat_wrapped_heading_maps_normalized_whitespace_and_ellipsis_to_source_text()
    {
        const string headingText = "first   extraordinarilylongword";
        using var session = new CancellationTokenSource();
        var (_, screen, policy, _, root) = ActiveChat(session);
        Show(screen, New("ParticipantCard", "Answerer", $"## {headingText}\n\nbody text", "mission", DateTimeOffset.UnixEpoch, false));
        var backend = new ClipboardBackend();
        await TerminalInteractionTestHost.Run(root, (context, phase, _) =>
        {
            if (phase == 0) backend.SetSize(new TerminalSize(20, 32), raiseEvent: true);
            if (phase == 2)
            {
                var heading = root.EnumerateVisualsDepthFirst().Single(visual => visual.GetType().Name == "HeadingImage");
                var body = root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Single(paragraph => paragraph.Text == "body text");
                Assert.True(heading.Bounds.Height >= 4);
                backend.PushEvent(new TerminalMouseEvent
                {
                    Kind = TerminalMouseKind.Down, Button = TerminalMouseButton.Left,
                    X = heading.Bounds.X, Y = heading.Bounds.Y + 2,
                });
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Drag, body, 4);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Up, body, 4);
            }
            if (phase == 3)
            {
                Assert.True((bool)Call(policy, "CopySelection")!);
                Assert.Equal("extraordinarilylongword\n\nbody", backend.Written);
            }
        }, last: 4, backend);
    }

    [Fact]
    public async Task Active_chat_rich_range_clears_at_invalid_boundary_replacement_and_detach()
    {
        using var session = new CancellationTokenSource();
        var (_, screen, policy, _, root) = ActiveChat(session);
        var sent = DateTimeOffset.UnixEpoch;
        Show(screen, New("ParticipantCard", "Answerer", "first body\n\nfirst tail\n\n```text\npayload\n```", "mission", sent, false));
        Paragraph[] old = [];
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            var paragraphs = root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Where(paragraph => paragraph.App == context.App).ToArray();
            if (phase == 1)
            {
                old = paragraphs.Where(paragraph => paragraph.Text is "first body" or "first tail").ToArray();
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Down, old[0]);
            }
            if (phase == 2)
            {
                Assert.False((bool)Call(policy, "CopySelection")!);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Drag,
                    root.EnumerateVisualsDepthFirst().Single(visual => visual.GetType().Name == "CodeCopyButton"));
            }
            if (phase == 3)
            {
                Assert.False((bool)Call(policy, "CopySelection")!);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Up,
                    root.EnumerateVisualsDepthFirst().Single(visual => visual.GetType().Name == "CodeCopyButton"));
            }
            if (phase == 4)
            {
                Assert.False((bool)Call(policy, "CopySelection")!);
                Assert.All(old, paragraph => Assert.False(paragraph.HasSelection));
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Down, old[0]);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Drag, old[1], 4);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Up, old[1], 4);
            }
            if (phase == 5)
            {
                Assert.True((bool)Call(policy, "CopySelection")!);
                Show(screen, New("ParticipantCard", "Answerer", "next body\n\nnext tail", "mission", sent, false));
            }
            if (phase == 7)
            {
                Assert.All(old, paragraph => Assert.False(paragraph.HasSelection));
                Assert.False((bool)Call(policy, "CopySelection")!);
                var next = paragraphs.Where(paragraph => paragraph.Text is "next body" or "next tail").ToArray();
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Down, next[0]);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Drag, next[1], 4);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Up, next[1], 4);
            }
            if (phase == 8)
            {
                Assert.True((bool)Call(policy, "CopySelection")!);
                Assert.Contains("next", backend.Written);
                Show(screen, []);
            }
            if (phase == 9)
            {
                Assert.All(old, paragraph => { Assert.False(paragraph.HasSelection); Assert.False(paragraph.IsEnabled); });
                Assert.False((bool)Call(policy, "CopySelection")!);
            }
        }, last: 10);
    }

    [Fact]
    public async Task Active_chat_rich_range_copy_failure_does_not_stop_turn()
    {
        using var session = new CancellationTokenSource();
        var (tui, screen, _, _, root) = ActiveChat(session);
        Show(screen, New("ParticipantCard", "Answerer", "first body\n\nfirst tail", "mission", DateTimeOffset.UnixEpoch, false));
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            var paragraphs = root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Where(paragraph => paragraph.App == context.App).ToArray();
            var body = paragraphs.Single(paragraph => paragraph.Text == "first body");
            var tail = paragraphs.Single(paragraph => paragraph.Text == "first tail");
            if (phase == 1)
            {
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Down, body);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Drag, tail, 4);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Up, tail, 4);
                backend.FailWrite = true;
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlC);
            }
            if (phase == 2)
            {
                Assert.Equal(1, backend.Writes);
                Assert.False(StopRequested(tui));
            }
        }, last: 3);
    }

    [Fact]
    public async Task Actual_screen_sources_and_chrome_remain_owned_through_outer_recycling()
    {
        var screen = Screen("Dark");
        Show(screen,
            New("YouBlock", "user alpha beta", DateTimeOffset.UnixEpoch),
            New("PendingYouBlock", Guid.NewGuid(), "pending alpha beta", DateTimeOffset.UnixEpoch),
            New("NoticeLine", "notice alpha beta"), New("ErrorLine", "error alpha beta"),
            New("ParticipantCard", "Answerer", "body alpha beta\n\n> quote\n\n- list\n\n| key | value |\n| --- | --- |\n| a | b |\n\n> [!NOTE]\n> alert text\n\n```go\nfunc main() {}\n```", "Chat", DateTimeOffset.UnixEpoch, false));
        var root = (Visual)Property(screen, "Root");
        var policy = Property(screen, "Interaction");
        var first = new HashSet<Paragraph>();
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            var live = root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Where(p => p.App == context.App).ToArray();
            if (phase == 1)
            {
                Assert.NotEmpty(live);
                Assert.All(live, p => { Assert.False(p.IsSelectable); Assert.NotNull(p.ContextMenuFactory); });
                Assert.All(root.EnumerateVisualsDepthFirst().OfType<TextBlock>(), text => Assert.False(text.IsSelectable));
                Assert.Equal(live.Length + 1, (int)Property(policy, "SourceCount"));
                first = live.ToHashSet();
                root.EnumerateVisualsDepthFirst().OfType<DocumentFlow>().First().Scroll!.SetOffset(0, 0);
            }
            if (phase == 2)
            {
                Assert.Equal(live.Length + 1, (int)Property(policy, "SourceCount"));
                var source = live.First(p => p.Text?.Contains("user alpha") == true);
                TerminalInteractionTestHost.Drag(backend, source, 5, 10);
            }
            if (phase == 3)
            {
                var source = live.First(p => p.Text?.Contains("user alpha") == true);
                Assert.True(source.HasSelection);
                source.ContextMenuFactory!(source).Single().Command!.Execute(source);
                Assert.Equal("alpha", backend.Written);
                Show(screen, Enumerable.Range(0, 60).Select(i => New("YouBlock", $"replacement {i} alpha beta", DateTimeOffset.UnixEpoch)).ToArray());
            }
            if (phase == 4)
            {
                Assert.DoesNotContain(live, first.Contains);
                Assert.Equal(live.Length + 1, (int)Property(policy, "SourceCount"));
                Assert.True(live.Length < 60, "outer DocumentFlow must realize a bounded viewport");
                root.EnumerateVisualsDepthFirst().OfType<DocumentFlow>().First().Scroll!.SetOffset(0, 0);
            }
            if (phase == 5)
            {
                Assert.Equal(live.Length + 1, (int)Property(policy, "SourceCount"));
                Call(screen, "ShowEditor", new Paragraph("unrelated test view") { IsSelectable = false }, true);
            }
            if (phase == 6) Assert.Equal(1, (int)Property(policy, "SourceCount"));
        }, last: 7);
    }

    [Theory]
    [InlineData(false, TerminalKey.Left, 10, false, "alpha beta", "a", 9)]
    [InlineData(true, TerminalKey.Left, 10, false, "alpha beta", "a", 9)]
    [InlineData(false, TerminalKey.Right, 0, false, "alpha beta", "a", 1)]
    [InlineData(true, TerminalKey.Right, 0, false, "alpha beta", "a", 1)]
    [InlineData(false, TerminalKey.Home, 10, false, "alpha beta", "alpha beta", 0)]
    [InlineData(true, TerminalKey.Home, 10, false, "alpha beta", "alpha beta", 0)]
    [InlineData(false, TerminalKey.End, 0, false, "alpha beta", "alpha beta", 10)]
    [InlineData(true, TerminalKey.End, 0, false, "alpha beta", "alpha beta", 10)]
    [InlineData(false, TerminalKey.Left, 10, true, "alpha beta", "beta", 6)]
    [InlineData(true, TerminalKey.Left, 10, true, "alpha beta", "beta", 6)]
    [InlineData(false, TerminalKey.Right, 0, true, "alpha beta", "alpha", 5)]
    [InlineData(true, TerminalKey.Right, 0, true, "alpha beta", "alpha", 5)]
    [InlineData(false, TerminalKey.Up, 17, false, "alpha beta\nsecond", "beta\nsecond", 6)]
    [InlineData(true, TerminalKey.Up, 17, false, "alpha beta\nsecond", "beta\nsecond", 6)]
    [InlineData(false, TerminalKey.Down, 6, false, "alpha beta\nsecond", "beta\nsecond", 17)]
    [InlineData(true, TerminalKey.Down, 6, false, "alpha beta\nsecond", "beta\nsecond", 17)]
    public async Task Actual_composer_and_file_editor_draw_no_click_native_ranges(bool file, TerminalKey key, int start,
        bool word, string document, string text, int caret)
    {
        using var session = new CancellationTokenSource();
        var (_, screen, policy, composer, root) = ActiveChat(session);
        var editor = file ? FileView(screen, policy) : composer;
        editor.TextDocument = new TextDocument(document);
        var frame = new NativeFrame();
        await TerminalInteractionTestHost.Run(new ZStack(root, frame), (context, phase, backend) =>
        {
            if (phase == 1)
            {
                context.App.Focus(editor);
                editor.CaretIndex = start;
                var modifiers = TerminalModifiers.Shift;
                if (word) modifiers |= OperatingSystem.IsMacOS() ? TerminalModifiers.Alt : TerminalModifiers.Ctrl;
                TerminalInteractionTestHost.Key(backend, key, modifiers);
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlC);
            }
            if (phase == 2)
            {
                Assert.True(editor.HasSelection);
                Assert.Equal(caret, editor.CaretIndex);
                Assert.Equal(text, backend.Written);
                var selection = ForgeText.Get<Color>(ForgeText.Theme("Dark"), "Selection").ToHexString();
                Assert.Contains(selection, string.Join('\n', frame.Lines), StringComparison.OrdinalIgnoreCase);
                Console.WriteLine($"Native {(file ? "CodeEditor" : "composer")} key={key} word={word} caret={editor.CaretIndex} highlighted=true");
            }
        }, last: 3);
    }

    [Theory]
    [InlineData(false, "drag")]
    [InlineData(true, "drag")]
    [InlineData(false, "double")]
    [InlineData(true, "double")]
    [InlineData(false, "shift")]
    [InlineData(true, "shift")]
    public async Task Actual_editor_mouse_gestures_keep_native_word_and_directional_ranges(bool file, string gesture)
    {
        using var session = new CancellationTokenSource();
        var (_, screen, policy, composer, root) = ActiveChat(session);
        var editor = file ? FileView(screen, policy) : composer;
        editor.TextDocument = new TextDocument("alpha beta");
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            if (phase == 1)
            {
                context.App.Focus(editor);
                editor.CaretIndex = 6;
                Assert.True(editor.TryGetCursorCell(out var x, out var y));
                if (gesture == "drag")
                {
                    foreach (var kind in new[] { TerminalMouseKind.Down, TerminalMouseKind.Drag, TerminalMouseKind.Up })
                        backend.PushEvent(new TerminalMouseEvent { Kind = kind, Button = TerminalMouseButton.Left, X = kind == TerminalMouseKind.Down ? x + 4 : x, Y = y });
                }
                else backend.PushEvent(new TerminalMouseEvent
                {
                    Kind = gesture == "double" ? TerminalMouseKind.DoubleClick : TerminalMouseKind.Down,
                    Button = TerminalMouseButton.Left, X = gesture == "double" ? x + 1 : x + 4, Y = y,
                    Modifiers = gesture == "shift" ? TerminalModifiers.Shift : TerminalModifiers.None,
                });
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlC);
            }
            if (phase == 2)
            {
                Assert.True(editor.HasSelection);
                Assert.Equal("beta", backend.Written);
                Assert.Equal(gesture == "drag" ? 6 : 10, editor.CaretIndex);
            }
        }, last: 3);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Actual_editor_keyboard_Paste_Undo_and_Cut_keep_native_editing(bool file, bool failedCut)
    {
        using var session = new CancellationTokenSource();
        var (_, screen, policy, composer, root) = ActiveChat(session);
        var editor = file ? FileView(screen, policy) : composer;
        editor.TextDocument = new TextDocument("alpha beta");
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            if (phase == 1)
            {
                context.App.Focus(editor);
                backend.TrySetClipboardText("replacement");
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlA);
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlV);
            }
            if (phase == 2)
            {
                Assert.Equal("replacement", EditorText(editor));
                Assert.Equal(1, backend.Reads);
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlZ);
            }
            if (phase == 3)
            {
                Assert.Equal("alpha beta", EditorText(editor));
                backend.FailWrite = failedCut;
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlA);
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlX);
            }
            if (phase == 4)
            {
                Assert.Equal("", EditorText(editor));
                Assert.Equal(failedCut ? null : "alpha beta", backend.Written);
                Assert.Equal(1, backend.Writes);
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlZ);
            }
            if (phase == 5) Assert.Equal("alpha beta", EditorText(editor));
        }, last: 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_clipboard_feedback_prefixes_native_progress_or_unsaved_status(bool file)
    {
        using var session = new CancellationTokenSource();
        var (_, screen, policy, composer, root) = ActiveChat(session);
        var editor = file ? FileView(screen, policy) : composer;
        if (!file)
        {
            editor.TextDocument = new TextDocument("alpha beta");
            Show(screen, New("ParticipantCard", "Answerer", "streaming", "Chat", DateTimeOffset.UnixEpoch, true));
        }
        var frame = new NativeFrame();
        await TerminalInteractionTestHost.Run(new ZStack(root, frame), (context, phase, backend) =>
        {
            if (phase == 1)
            {
                context.App.Focus(editor);
                if (file) { backend.PushEvent(new TerminalTextEvent { Text = "x" }); TerminalInteractionTestHost.Key(backend, TerminalKey.Escape); }
            }
            if (phase == 2)
            {
                if (file) Assert.Contains("Unsaved", string.Join('\n', frame.Lines), StringComparison.OrdinalIgnoreCase);
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlA);
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlC);
            }
            if (phase == 3)
            {
                Assert.Contains(file ? "Copied · Unsaved" : "Copied · Answerer is replying", string.Join('\n', frame.Lines), StringComparison.OrdinalIgnoreCase);
                backend.PushEvent(new TerminalTextEvent { Text = "fresh" });
            }
            if (phase == 4) { Assert.Equal("", Feedback(policy)); Assert.DoesNotContain("Copied", string.Join('\n', frame.Lines)); }
        }, last: 5);
    }

    [Theory]
    [InlineData("modal")]
    [InlineData("chrome")]
    public async Task Actual_root_modal_and_chrome_cannot_copy_underlay_selection(string action)
    {
        using var session = new CancellationTokenSource();
        var (tui, screen, _, _, root) = ActiveChat(session);
        Show(screen, New("YouBlock", "alpha beta", DateTimeOffset.UnixEpoch));
        var modalButton = new Button("modal");
        var popup = new Popup(modalButton);
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            if (phase == 1) TerminalInteractionTestHost.Drag(backend, root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Single(), 6, 10);
            if (phase == 2)
            {
                Assert.True(root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Single().HasSelection);
                if (action == "modal") { popup.Show(); context.App.Focus(modalButton); }
                else
                {
                    var chrome = root.EnumerateVisualsDepthFirst().OfType<TextBlock>().First(text => text.IsVisible && text.Bounds.Width >= 4 && text.Bounds.Height > 0);
                    Assert.False(chrome.IsSelectable);
                    TerminalInteractionTestHost.Drag(backend, chrome, 0, 3);
                }
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlC);
            }
            if (phase == 3)
            {
                Assert.Equal(0, backend.Writes);
                Assert.Equal(action == "chrome", StopRequested(tui));
                if (action == "modal") popup.Close();
            }
        }, last: 4);
    }

    [Fact]
    public async Task Direct_Paragraph_retains_unchanged_range_then_detaches_and_reattaches_cleanly()
    {
        var policy = New("TextInteraction");
        var paragraph = new Paragraph("alpha beta");
        var wrapper = (Visual)New("ParagraphSelection", paragraph, policy);
        var slot = new Padder(wrapper);
        await TerminalInteractionTestHost.Run(slot, (context, phase, backend) =>
        {
            if (phase == 1) TerminalInteractionTestHost.Drag(backend, paragraph, 6, 10);
            if (phase == 2)
            {
                wrapper.Arrange(wrapper.Bounds);
                Assert.True(paragraph.TryCopySelection(out var selected));
                Assert.Equal("beta", selected);
                Assert.Equal(1, (int)Property(policy, "SourceCount"));
                slot.Content = null;
            }
            if (phase == 3)
            {
                Assert.False(paragraph.HasSelection);
                Assert.Equal(0, (int)Property(policy, "SourceCount"));
                slot.Content = wrapper;
            }
            if (phase == 4) { Assert.Equal(1, (int)Property(policy, "SourceCount")); TerminalInteractionTestHost.Drag(backend, paragraph, 6, 10); }
            if (phase == 5) { paragraph.ContextMenuFactory!(paragraph).Single().Command!.Execute(paragraph); Assert.Equal("beta", backend.Written); }
        }, last: 6);
    }

    [Fact]
    public async Task Actual_screen_swap_invalidates_captured_menu_and_keeps_editor_isolation()
    {
        using var session = new CancellationTokenSource();
        var (tui, screen, policy, _, root) = ActiveChat(session);
        Show(screen, New("YouBlock", "alpha beta", DateTimeOffset.UnixEpoch));
        Paragraph? source = null;
        Command? copy = null;
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            if (phase == 1)
            {
                source = root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Single();
                TerminalInteractionTestHost.Drag(backend, source, 6, 10);
            }
            if (phase == 2)
            {
                copy = source!.ContextMenuFactory!(source).Single().Command!;
                Assert.True(copy.CanExecute!(source));
                FileView(screen, policy);
            }
            if (phase == 3)
            {
                Assert.False(copy!.CanExecute!(source!));
                copy.Execute(source!);
                Assert.Equal(0, backend.Writes);
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlC);
            }
            if (phase == 4) { Assert.False(StopRequested(tui)); Assert.Equal(0, backend.Writes); }
        }, last: 5);
    }

    [Fact]
    public void Never_realized_direct_Paragraph_is_configured_without_live_registration()
    {
        var policy = New("TextInteraction");
        var paragraph = new Paragraph("discarded before layout");
        var wrapper = (Visual)New("ParagraphSelection", paragraph, policy);
        Assert.False(paragraph.IsSelectable);
        Assert.NotNull(paragraph.ContextMenuFactory);
        var slot = new Padder(wrapper);
        slot.Content = null;
        Assert.Equal(0, (int)Property(policy, "SourceCount"));
    }

    private static (object Tui, object Screen, object Policy, TextEditorBase Composer, Visual Root) ActiveChat(CancellationTokenSource session, string theme = "Dark")
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = ForgeText.Type("ForgeMission.Cli.Tui.ChatTui");
        var serviceType = type.GetConstructors(fields).Single().GetParameters()[0].ParameterType;
        var service = DispatchProxy.Create(serviceType, typeof(ChatTranscriptTests.StreamingConversations));
        var styles = ForgeText.Styles(theme);
        var tui = New("ChatTui", service, Guid.NewGuid(), New("ChatHeader", "chat", "Chat", 1, "anthropic", "ameer"),
            ForgeText.Theme(theme), ForgeText.Fonts(), null, new System.Collections.Concurrent.ConcurrentQueue<string>(), session);
        var screen = type.GetField("_screen", fields)!.GetValue(tui)!;
        var cell = ForgeText.Cell(10, 20);
        var tiles = ForgeText.Type("ForgeMission.Cli.Tui.ScreenTiles").GetMethod("Create")!.Invoke(null, [styles, cell])!;
        Call(screen, "UseImages", tiles, ChatScreenTileTests.TextImagesFor(styles, cell));
        Call(screen, "ShowChat");
        type.GetField("_submitting", fields)!.SetValue(tui, true);
        return (tui, screen, Property(screen, "Interaction"), (TextEditorBase)Property(screen, "Composer"), (Visual)Property(screen, "Root"));
    }

    private static TextEditorBase FileView(object screen, object policy)
    {
        var file = New("EditFile", "scratch.cs", "/tmp/forge-controlled-scratch.cs", false, "alpha beta");
        var view = New("FileEditor", file, ForgeText.Styles("Dark"), (Action)(() => { }), policy);
        Call(screen, "ShowEditor", Property(view, "View"), true);
        return (TextEditorBase)Property(view, "Editor");
    }

    private static bool StopRequested(object tui) => (bool)tui.GetType()
        .GetField("_stopOwnTurn", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tui)!;

    private static string EditorText(TextEditorBase editor)
    {
        var snapshot = editor.TextDocument.CurrentSnapshot;
        return string.Create(snapshot.Length, snapshot, static (text, source) => source.CopyTo(0, text));
    }

    private static Command[] CommandsWithMetadata(TextEditorBase editor, Dictionary<string, int> calls)
    {
        var index = 0;
        var originals = editor.Commands.Where(command => command.Id != "TextEditor.Copy").ToArray();
        foreach (var native in originals)
        {
            calls[native.Id] = 0;
            var sequence = index++ % 2 == 0;
            editor.AddCommand(new Command
            {
                Id = native.Id, LabelMarkup = $"[bold]{native.Id}[/]", Name = $"named-{native.Id}",
                DescriptionMarkup = "[dim]description[/]", SearchText = "search terms",
                Gesture = sequence ? null : new KeyGesture(TerminalKey.F12),
                Sequence = sequence ? new KeySequence(new KeyGesture(TerminalKey.F11), new KeyGesture(TerminalKey.F12)) : null,
                Importance = CommandImportance.Primary, Presentation = CommandPresentation.Menu | CommandPresentation.CommandPalette,
                CanExecute = _ => true, IsVisible = _ => true, ConsumesGestureWhenUnavailable = false, RouteGesture = false,
                Execute = target => { calls[native.Id]++; native.Execute(target); },
            });
        }
        return editor.Commands.Where(command => command.Id != "TextEditor.Copy").ToArray();
    }

    private sealed class ExtractionFaultEditor(bool empty) : PromptEditor, ISelectionOwner
    {
        internal int Extractions { get; private set; }
        bool ISelectionOwner.TryCopySelection(out string text) { Extractions++; text = ""; return empty; }
    }

    internal static object Screen(string theme)
    {
        var styles = ForgeText.Styles(theme);
        var screen = New("ChatScreen", New("ChatHeader", "chat", "Chat", 1, "anthropic", "ameer"), styles);
        var cell = ForgeText.Cell(10, 20);
        var tiles = ForgeText.Type("ForgeMission.Cli.Tui.ScreenTiles").GetMethod("Create")!.Invoke(null, [styles, cell])!;
        Call(screen, "UseImages", tiles, ChatScreenTileTests.TextImagesFor(styles, cell));
        return screen;
    }

    internal static void Show(object screen, params object[] blocks)
    {
        var array = Array.CreateInstance(ForgeText.Type("ForgeMission.Cli.Tui.TranscriptBlock"), blocks.Length);
        for (var index = 0; index < blocks.Length; index++) array.SetValue(blocks[index], index);
        Call(screen, "Show", array);
    }

    internal static object New(string type, params object?[] args) => Activator.CreateInstance(
        ForgeText.Type($"ForgeMission.Cli.Tui.{type}"), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
        binder: null, args, culture: null)!;
    internal static object? Call(object target, string method, params object?[] args) => target.GetType()
        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Single(candidate => candidate.Name == method && candidate.GetParameters().Length == args.Length
            && candidate.GetParameters().Select((parameter, index) => args[index] is null || parameter.ParameterType.IsInstanceOfType(args[index])).All(match => match))
        .Invoke(target, args);
    internal static object Property(object target, string name) => target.GetType()
        .GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(target)!;
    internal static string Feedback(object policy) => ((State<string>)Property(policy, "Feedback")).Value;

    private static void AssertMetadata(Command a, Command b)
    {
        Assert.Equal(a.Id, b.Id); Assert.Equal(a.LabelMarkup, b.LabelMarkup); Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.DescriptionMarkup, b.DescriptionMarkup); Assert.Equal(a.SearchText, b.SearchText);
        Assert.Equal(a.Gesture, b.Gesture); Assert.Equal(a.Sequence, b.Sequence); Assert.Equal(a.Importance, b.Importance);
        Assert.Equal(a.Presentation, b.Presentation); Assert.Same(a.CanExecute, b.CanExecute); Assert.Same(a.IsVisible, b.IsVisible);
        Assert.Equal(a.ConsumesGestureWhenUnavailable, b.ConsumesGestureWhenUnavailable); Assert.Equal(a.RouteGesture, b.RouteGesture);
    }
}
