using ForgeMission.Tests.TerminalInteraction;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Rendering;
using XenoAtom.Terminal.UI.Styling;

namespace ForgeMission.Tests.Cli;

[Collection(XenoAtomUiCollection.Name)]
public sealed class CodeCopyControlTests
{
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task Actual_native_button_paints_combined_hover_focus_press_and_result_states(string theme)
    {
        var policy = TextInteractionTests.New("TextInteraction");
        var header = (Visual)TextInteractionTests.New("CodeCopyHeader", "payload", ForgeText.Styles(theme), policy);
        var button = (Button)TextInteractionTests.Property(header, "Button");
        var editor = new PromptEditor();
        TextInteractionTests.Call(policy, "Configure", editor);
        var frame = new NativeFrame();
        await TerminalInteractionTestHost.Run(new ZStack(new VStack(header, editor), frame), (context, phase, backend) =>
        {
            var line = frame.Lines.Length == 0 ? "" : frame.Lines[button.Bounds.Y];
            if (phase > 1) Console.WriteLine($"Native button {theme} phase={phase} focused={button.HasFocus} hovered={button.IsHovered} pressed={button.IsPressed} enabled={button.IsEnabled} tooltip={TextInteractionTests.Property(button, "TooltipText")}: {line}");
            switch (phase)
            {
                case 1:
                    context.App.Focus(editor);
                    TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Move, button, 1);
                    break;
                case 2:
                    Assert.True(button.IsHovered);
                    Assert.False(button.HasFocus);
                    Assert.Contains("bold", line);
                    Assert.DoesNotContain("underline", line);
                    context.App.Focus(button);
                    break;
                case 3:
                    Assert.True(button.HasFocus && button.IsHovered);
                    Assert.Contains("underline", line);
                    TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Down, button, 1);
                    break;
                case 4:
                    Assert.True(button.IsPressed);
                    Assert.Contains("underline", line);
                    Assert.Contains(ForgeText.Get<Color>(ForgeText.Theme(theme), "Selection").ToHexString(), line, StringComparison.OrdinalIgnoreCase);
                    TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Up, button, 1);
                    break;
                case 5:
                    Assert.Contains("Copied", line);
                    button.IsEnabled = false;
                    break;
                case 6:
                    Assert.Contains("bold", line);
                    Assert.DoesNotContain("underline", line);
                    Assert.Contains(ForgeText.Get<Color>(ForgeText.Theme(theme), "TextMuted").ToHexString(), line, StringComparison.OrdinalIgnoreCase);
                    button.IsEnabled = true;
                    backend.FailWrite = true;
                    context.App.Focus(button);
                    TerminalInteractionTestHost.Key(backend, TerminalKey.Enter);
                    break;
                case 7:
                    Assert.Contains("Copy failed", line);
                    Assert.Equal("Copy failed", TextInteractionTests.Property(button, "TooltipText"));
                    Assert.Contains(ForgeText.Get<Color>(ForgeText.Theme(theme), "Error").ToHexString(), line, StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }, last: 8);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task Current_product_snippet_survives_all_viewport_corners_and_continuous_resize(string theme)
    {
        const string code = "var greeting = \"hello\";\nConsole.WriteLine(\"a sample with a long literal\");\n";
        var screen = TextInteractionTests.Screen(theme);
        TextInteractionTests.Show(screen, TextInteractionTests.New("ParticipantCard", "Answerer", $"```csharp\n{code}\n```", "Chat", DateTimeOffset.UnixEpoch, false));
        var root = (Visual)TextInteractionTests.Property(screen, "Root");
        var frame = new NativeFrame();
        var sizes = new[] { new TerminalSize(100, 32), new TerminalSize(60, 24), new TerminalSize(60, 32), new TerminalSize(100, 24) }
            .Concat(Enumerable.Range(60, 41).Select(width => new TerminalSize(width, 24))).ToArray();
        await TerminalInteractionTestHost.Run(new ZStack(root, frame), (context, phase, backend) =>
        {
            if (phase == 0) return;
            var size = sizes[phase - 1];
            var button = root.EnumerateVisualsDepthFirst().OfType<Button>().Single();
            var body = root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Single(p => p.Text?.Contains("greeting") == true);
            Assert.Equal(size.Rows, frame.Lines.Length);
            Assert.Equal(15, button.Bounds.Width);
            Assert.Equal(button.Bounds.Y + 1, body.Bounds.Y);
            Assert.InRange(button.Bounds.Right, 0, size.Columns);
            Assert.InRange(body.Bounds.Right, 0, size.Columns);
            Assert.Contains("Copy code", frame.Lines[button.Bounds.Y]);
            Console.WriteLine($"Native viewport {theme} {size.Columns}x{size.Rows}: button={button.Bounds} code={body.Bounds} {frame.Lines[button.Bounds.Y]}");
            if (phase < sizes.Length) backend.SetSize(sizes[phase], raiseEvent: true);
        }, last: sizes.Length);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task Same_native_header_resizes_through_zero_icon_and_full_label(string theme)
    {
        var policy = TextInteractionTests.New("TextInteraction");
        var header = (Visual)TextInteractionTests.New("CodeCopyHeader", "payload", ForgeText.Styles(theme), policy);
        var button = (Button)TextInteractionTests.Property(header, "Button");
        var frame = new NativeFrame();
        var widths = new[] { 0, 1, 2, 3, 4, 12, 14, 15, 16, 60, 100, 2, 0, 15 };
        header.MaxWidth = widths[0];
        await TerminalInteractionTestHost.Run(new ZStack(header, frame), (context, phase, backend) =>
        {
            if (phase == 0) return;
            var width = widths[phase - 1];
            Assert.Equal(width > 0, button.IsVisible);
            Assert.Equal(width > 0, button.IsTabStop);
            Assert.Equal(Math.Min(width, 15), button.Bounds.Width);
            Assert.Equal(1, header.Bounds.Height);
            if (width >= 15) Assert.Contains("Copy code", frame.Lines[button.Bounds.Y]);
            Console.WriteLine($"Native header {theme} available={width} bounds={button.Bounds} visible={button.IsVisible} tab={button.IsTabStop}: {frame.Lines[0]}");
            if (phase < widths.Length) header.MaxWidth = widths[phase];
        }, last: widths.Length);
    }

    [Theory]
    [InlineData("Light", "csharp", "var greeting = \"hello\";")]
    [InlineData("Dark", "csharp", "var greeting = \"hello\";")]
    [InlineData("Light", "go", "func main() { println(\"hello\") }")]
    [InlineData("Dark", "go", "func main() { println(\"hello\") }")]
    [InlineData("Light", "json", "{ \"greeting\": \"hello\", \"count\": 1 }")]
    [InlineData("Dark", "json", "{ \"greeting\": \"hello\", \"count\": 1 }")]
    public async Task Selected_product_syntax_keeps_native_foregrounds_on_theme_selection(string theme, string language, string code)
    {
        var screen = TextInteractionTests.Screen(theme);
        TextInteractionTests.Show(screen, TextInteractionTests.New("ParticipantCard", "Answerer", $"```{language}\n{code}\n```", "Chat", DateTimeOffset.UnixEpoch, false));
        var root = (Visual)TextInteractionTests.Property(screen, "Root");
        var frame = new NativeFrame();
        Paragraph? body = null;
        await TerminalInteractionTestHost.Run(new ZStack(root, frame), (context, phase, backend) =>
        {
            if (phase == 1)
            {
                body = root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Single(p => p.Text == code);
                Assert.NotEmpty(body.Runs);
                TerminalInteractionTestHost.Drag(backend, body, 0, code.Length);
            }
            if (phase == 2)
            {
                Assert.True(body!.HasSelection);
                var line = frame.Lines[body.Bounds.Y];
                var selection = ForgeText.Get<Color>(ForgeText.Theme(theme), "Selection").ToHexString();
                Assert.Contains(selection, line, StringComparison.OrdinalIgnoreCase);
                var foregrounds = body.Runs.Select(run => { Assert.True(run.Style.TryGetForeground(out var color)); return color.ToHexString(); }).Distinct().ToArray();
                Assert.All(foregrounds, color => Assert.Contains(color, line, StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"Native selected {theme}/{language} selection={selection} foregrounds={string.Join(',', foregrounds)}: {line}");
            }
        }, last: 3);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("pipeline")]
    [InlineData("unchanged")]
    public async Task Actual_host_retires_before_pending_release_and_replacement_accepts_fresh_input(string change)
    {
        var screen = TextInteractionTests.Screen("Dark");
        void Show(string code, bool streaming) => TextInteractionTests.Show(screen,
            TextInteractionTests.New("ParticipantCard", "Answerer", $"```\n{code}\n```", "Chat", DateTimeOffset.UnixEpoch, streaming));
        Show("old immutable payload", false);
        var root = (Visual)TextInteractionTests.Property(screen, "Root");
        var policy = TextInteractionTests.Property(screen, "Interaction");
        Button? old = null;
        var changed = change != "unchanged";
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            if (phase == 1)
            {
                old = root.EnumerateVisualsDepthFirst().OfType<Button>().Single();
                context.App.Focus(old);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Down, old);
            }
            if (phase == 2)
            {
                Assert.True(old!.IsPressed);
                context.App.Post(() =>
                {
                    Show(change == "content" ? "new immutable payload" : "old immutable payload", change == "pipeline");
                    Assert.Equal(!changed, old.IsPressed);
                    Assert.Equal(!changed, old.IsEnabled);
                    Assert.Equal(0, backend.Writes);
                    Assert.Equal("", TextInteractionTests.Feedback(policy));
                });
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Up, old);
                Thread.Sleep(50);
            }
            if (phase == 3)
            {
                Assert.Equal(changed ? 0 : 1, backend.Writes);
                var current = root.EnumerateVisualsDepthFirst().OfType<Button>().Single();
                Assert.True(current.IsEnabled);
                Assert.False(current.IsPressed);
                context.App.Focus(current);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Down, current);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Up, current);
                TerminalInteractionTestHost.Key(backend, TerminalKey.Enter);
                TerminalInteractionTestHost.Key(backend, TerminalKey.Space);
            }
            if (phase == 4)
            {
                Assert.Equal(changed ? 3 : 4, backend.Writes);
                Assert.Equal(change == "content" ? "new immutable payload" : "old immutable payload", backend.Written);
            }
        }, last: 5);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Wrapper_retained_visual_resume_preserves_original_enabled_state(bool enabled)
    {
        var screen = TextInteractionTests.Screen("Dark");
        TextInteractionTests.Show(screen, TextInteractionTests.New("ParticipantCard", "Answerer", "```\npayload\n```", "Chat", DateTimeOffset.UnixEpoch, false));
        var root = (Visual)TextInteractionTests.Property(screen, "Root");
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            if (phase != 1) return;
            var button = root.EnumerateVisualsDepthFirst().OfType<Button>().Single();
            var wrapper = root.EnumerateVisualsDepthFirst().Single(visual => visual.GetType().Name == "ParagraphSelection"
                && visual is Padder { Content: MarkdownControl });
            button.IsEnabled = enabled;
            TextInteractionTests.Call(wrapper, "Retire");
            TextInteractionTests.Call(wrapper, "Retire");
            Assert.False(button.IsEnabled);
            Assert.False(button.IsPressed);
            var bounds = wrapper.Bounds;
            wrapper.Arrange(new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height));
            wrapper.Arrange(bounds);
            Assert.Equal(enabled, button.IsEnabled);
            Assert.Same(button, root.EnumerateVisualsDepthFirst().OfType<Button>().Single());
            TextInteractionTests.Call(button, "Resume");
            Assert.Equal(enabled, button.IsEnabled);
            Assert.Equal(0, backend.Writes);
        }, last: 2);
    }

    [Theory]
    [InlineData("retire")]
    [InlineData("ancestor-disabled")]
    [InlineData("ancestor-hidden")]
    public async Task Transport_continuation_rejects_retirement_or_ineligible_ancestor(string mutation)
    {
        var policy = TextInteractionTests.New("TextInteraction");
        var header = (Visual)TextInteractionTests.New("CodeCopyHeader", "payload", ForgeText.Styles("Dark"), policy);
        var button = (Button)TextInteractionTests.Property(header, "Button");
        var slot = new Padder(header);
        await TerminalInteractionTestHost.Run(slot, (context, phase, backend) =>
        {
            if (phase == 1)
            {
                context.App.Focus(button);
                backend.DuringWrite = () =>
                {
                    if (mutation == "retire") TextInteractionTests.Call(button, "Retire");
                    if (mutation == "ancestor-disabled") slot.IsEnabled = false;
                    if (mutation == "ancestor-hidden") slot.IsVisible = false;
                };
                TerminalInteractionTestHost.Key(backend, TerminalKey.Enter);
            }
            if (phase == 2)
            {
                Assert.Equal(1, backend.Writes);
                Assert.Equal("", TextInteractionTests.Feedback(policy));
                backend.DuringWrite = null;
                TextInteractionTests.Call(button, "Resume");
                slot.IsEnabled = slot.IsVisible = true;
                context.App.Focus(button);
                TerminalInteractionTestHost.Key(backend, TerminalKey.Enter);
            }
            if (phase == 3) { Assert.Equal(2, backend.Writes); Assert.Equal("Copied", TextInteractionTests.Feedback(policy)); }
        }, last: 4);
    }

    [Theory]
    [InlineData("detach", false)]
    [InlineData("hide", false)]
    [InlineData("detach", true)]
    [InlineData("hide", true)]
    public async Task Native_press_lifetime_cancels_old_release_and_reports_exact_payload(string mutation, bool failed)
    {
        const string payload = "  alpha\n\n\tbeta\n";
        var policy = TextInteractionTests.New("TextInteraction");
        var header = (Visual)TextInteractionTests.New("CodeCopyHeader", payload, ForgeText.Styles("Dark"), policy);
        var button = (Button)TextInteractionTests.Property(header, "Button");
        var slot = new Padder(header) { MaxWidth = 20 };
        await TerminalInteractionTestHost.Run(slot, (context, phase, backend) =>
        {
            if (phase == 1) { context.App.Focus(button); TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Down, button); }
            if (phase == 2)
            {
                Assert.True(button.IsPressed);
                if (mutation == "detach") slot.Content = null; else slot.MaxWidth = 0;
            }
            if (phase == 3)
            {
                Assert.False(button.IsPressed);
                Assert.Equal("", TextInteractionTests.Feedback(policy));
                if (mutation == "detach") slot.Content = header; else slot.MaxWidth = 20;
            }
            if (phase == 4)
            {
                Assert.True(button.Bounds.Width > 0);
                context.App.Focus(button);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Up, button);
            }
            if (phase == 5)
            {
                Assert.Equal(0, backend.Writes);
                backend.FailWrite = failed;
                TerminalInteractionTestHost.Key(backend, TerminalKey.Enter);
            }
            if (phase == 6)
            {
                Assert.Equal(1, backend.Writes);
                Assert.Equal(failed ? "Copy failed" : "Copied", TextInteractionTests.Feedback(policy));
                if (!failed) Assert.Equal(payload, backend.Written);
                backend.FailWrite = false;
                TerminalInteractionTestHost.Key(backend, TerminalKey.Space);
            }
            if (phase == 7) { Assert.Equal(2, backend.Writes); Assert.Equal(payload, backend.Written); }
        }, last: 8);
    }

    [Fact]
    public async Task Focused_button_at_zero_width_rejects_keys_and_repairs_native_Tab_focus()
    {
        var policy = TextInteractionTests.New("TextInteraction");
        var header = (Visual)TextInteractionTests.New("CodeCopyHeader", "exact payload", ForgeText.Styles("Dark"), policy);
        var button = (Button)TextInteractionTests.Property(header, "Button");
        var other = new Button("next");
        await TerminalInteractionTestHost.Run(new VStack(header, other), (context, phase, backend) =>
        {
            if (phase == 1) { context.App.Focus(button); header.MaxWidth = 0; }
            if (phase == 2)
            {
                Assert.False(button.HasFocus);
                Assert.False(button.IsTabStop);
                Assert.False(button.IsVisible);
                Assert.Same(other, context.App.FocusedElement);
                TerminalInteractionTestHost.Key(backend, TerminalKey.Enter);
                TerminalInteractionTestHost.Key(backend, TerminalKey.Space);
            }
            if (phase == 3) { Assert.Equal(0, backend.Writes); header.MaxWidth = 15; }
            if (phase == 4) { Assert.True(button.IsTabStop); TerminalInteractionTestHost.Key(backend, TerminalKey.Tab); }
            if (phase == 5)
            {
                Assert.True(button.HasFocus);
                TerminalInteractionTestHost.Key(backend, TerminalKey.Enter);
                TerminalInteractionTestHost.Key(backend, TerminalKey.Space);
            }
            if (phase == 6) { Assert.Equal(2, backend.Writes); Assert.Equal("exact payload", backend.Written); }
        }, last: 7);
    }

    [Fact]
    public async Task Outer_DocumentFlow_recycles_snippet_cancels_press_and_copies_after_return()
    {
        var screen = TextInteractionTests.Screen("Dark");
        TextInteractionTests.Show(screen, Enumerable.Range(0, 60).Select(index => TextInteractionTests.New("ParticipantCard",
            $"Answerer {index}", $"```\nexact payload {index}\n```", "Chat", DateTimeOffset.UnixEpoch, false)).ToArray());
        var root = (Visual)TextInteractionTests.Property(screen, "Root");
        var policy = TextInteractionTests.Property(screen, "Interaction");
        Button? old = null;
        Button? current = null;
        var payload = "";
        var offset = 0;
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            var flow = root.EnumerateVisualsDepthFirst().OfType<DocumentFlow>().First();
            if (phase == 1)
            {
                offset = flow.Scroll.OffsetY;
                old = root.EnumerateVisualsDepthFirst().OfType<Button>().Last(button => button.Bounds.Y >= 0 && button.Bounds.Y < 29);
                Visual? content = old.Parent;
                while (content is not VStack) content = content!.Parent;
                payload = content.EnumerateVisualsDepthFirst().OfType<Paragraph>().Single().Text!;
                context.App.Focus(old);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Down, old);
            }
            if (phase == 2) { Assert.True(old!.IsPressed); flow.FollowTail = false; flow.Scroll.SetOffset(0, 0); }
            if (phase == 3)
            {
                Assert.Null(old!.App);
                Assert.False(old.IsPressed);
                Assert.Equal("", TextInteractionTests.Feedback(policy));
                Assert.Equal(0, backend.Writes);
                flow.ScrollToItem(int.Parse(payload.Split(' ').Last()));
            }
            if (phase == 4)
            {
                var body = root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Single(paragraph => paragraph.Text == payload);
                current = body.Parent!.EnumerateVisualsDepthFirst().OfType<Button>().Single();
                Assert.Same(context.App, current.App);
                Assert.True(current.IsEnabled);
                context.App.Focus(current);
                TerminalInteractionTestHost.Mouse(backend, TerminalMouseKind.Up, current);
            }
            if (phase == 5) { Assert.Equal(0, backend.Writes); TerminalInteractionTestHost.Key(backend, TerminalKey.Enter); }
            if (phase == 6)
            {
                Assert.Equal(1, backend.Writes);
                Assert.Equal(payload, backend.Written);
                Assert.Equal("Copied", TextInteractionTests.Feedback(policy));
                Console.WriteLine($"Native DocumentFlow snippet offset={offset} detached, returnedSameButton={ReferenceEquals(old, current)}, old release=0, fresh exact write=1");
            }
        }, last: 7);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Actual_native_header_widths_and_state_styles_match_semantic_reference(string theme)
    {
        var styles = ForgeText.Styles(theme);
        var policy = TextInteractionTests.New("TextInteraction");
        var header = (Visual)TextInteractionTests.New("CodeCopyHeader", "", styles, policy);
        var button = (Button)TextInteractionTests.Property(header, "Button");
        foreach (var width in new[] { 0, 1, 2, 3, 14, 15, 16, 60, 100, 2, 0, 15 })
        {
            header = (Visual)TextInteractionTests.New("CodeCopyHeader", "", styles, policy);
            button = (Button)TextInteractionTests.Property(header, "Button");
            header.MaxWidth = width;
            var lines = VisualSnapshotRenderer.Render(header, Math.Max(width, 1), 1).ToMarkupLines();
            Assert.Equal(width > 0, button.IsVisible);
            Assert.Equal(width > 0, button.IsTabStop);
            Assert.Equal(Math.Min(width, 15), button.Bounds.Width);
            Assert.All(header.EnumerateVisualsDepthFirst().OfType<TextBlock>(), text => Assert.False(text.IsSelectable));
            if (width >= 15) Assert.Contains("Copy code", string.Join("", lines));
        }
        foreach (var result in new[] { "", "Copied", "Copy failed" })
        {
            var style = (ButtonStyle)TextInteractionTests.Call(styles, "CodeCopy", result, 15, true)!;
            var nativeTheme = ForgeText.Get<Theme>(styles, "Screen");
            var focused = style.Resolve(nativeTheme, true, true, true, false, ControlTone.Default);
            Assert.True(focused.TextStyle.HasFlag(TextStyle.Bold));
            Assert.True(focused.TextStyle.HasFlag(TextStyle.Underline));
            var pressed = style.Resolve(nativeTheme, true, true, true, true, ControlTone.Default);
            Assert.True(pressed.TextStyle.HasFlag(TextStyle.Underline));
            var disabled = style.Resolve(nativeTheme, false, true, true, true, ControlTone.Default);
            Assert.False(disabled.TextStyle.HasFlag(TextStyle.Underline));
        }
    }

    [Fact]
    public async Task Actual_renderer_copies_complete_code_and_keeps_native_runs_in_both_themes()
    {
        const string code = "  var greeting = \"hello\";\n\n\tConsole.WriteLine(greeting);\n";
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var language in new[] { "csharp", "go", "json", "unknown-language" })
        {
            var screen = TextInteractionTests.Screen(theme);
            TextInteractionTests.Show(screen, TextInteractionTests.New("ParticipantCard", "Answerer", $"```{language}\n{code}\n```\n", "Chat", DateTimeOffset.UnixEpoch, false));
            var root = (Visual)TextInteractionTests.Property(screen, "Root");
            await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
            {
                if (phase == 1)
                {
                    var button = root.EnumerateVisualsDepthFirst().OfType<Button>().Single();
                    var body = root.EnumerateVisualsDepthFirst().OfType<Paragraph>().Single(p => p.Text?.Contains("greeting") == true);
                    Assert.Single(root.EnumerateVisualsDepthFirst().OfType<Padder>(), visual => visual.GetType().Name == "ParagraphSelection" && visual.Content is MarkdownControl);
                    Assert.DoesNotContain(root.EnumerateVisualsDepthFirst().OfType<Padder>(), visual => visual.GetType().Name == "ParagraphSelection" && visual.Content == body);
                    Assert.NotEmpty(body.Runs);
                    Assert.False(body.IsSelectable);
                    Assert.Equal(15, button.Bounds.Width);
                    Assert.Equal(button.Bounds.Y + 1, body.Bounds.Y);
                    context.App.Focus(button);
                    TerminalInteractionTestHost.Key(backend, TerminalKey.Enter);
                }
                if (phase == 2) { Assert.Equal(1, backend.Writes); Assert.Equal(code, backend.Written); }
            }, last: 3);
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task Rendered_feedback_updates_without_resize_and_moves_between_buttons(string theme)
    {
        var styles = ForgeText.Styles(theme);
        var policy = TextInteractionTests.New("TextInteraction");
        var first = (Visual)TextInteractionTests.New("CodeCopyHeader", "first", styles, policy);
        var second = (Visual)TextInteractionTests.New("CodeCopyHeader", "second", styles, policy);
        var buttons = new[] { (Button)TextInteractionTests.Property(first, "Button"), (Button)TextInteractionTests.Property(second, "Button") };
        var frame = new NativeFrame();
        var root = new ZStack(new VStack(first, second), frame);
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            if (phase == 1) { context.App.Focus(buttons[0]); TerminalInteractionTestHost.Key(backend, TerminalKey.Enter); }
            if (phase == 2)
            {
                AssertLabel(frame, buttons[0], "Copied");
                Assert.Equal("Copied", TextInteractionTests.Property(buttons[0], "TooltipText"));
                Assert.Equal(ForgeText.Get<Color>(ForgeText.Theme(theme), "Success"), Foreground(buttons[0]));
                context.App.Focus(buttons[1]);
                TerminalInteractionTestHost.Key(backend, TerminalKey.Enter);
            }
            if (phase == 3)
            {
                AssertLabel(frame, buttons[0], "Copy code");
                AssertLabel(frame, buttons[1], "Copied");
                backend.FailWrite = true;
                TerminalInteractionTestHost.Key(backend, TerminalKey.Enter);
            }
            if (phase == 4)
            {
                AssertLabel(frame, buttons[1], "Copy failed");
                Assert.Equal("Copy failed", TextInteractionTests.Property(buttons[1], "TooltipText"));
                Assert.Equal(ForgeText.Get<Color>(ForgeText.Theme(theme), "Error"), Foreground(buttons[1]));
                TextInteractionTests.Call(policy, "Reset");
            }
            if (phase == 5) AssertLabel(frame, buttons[1], "Copy code");
        }, last: 6);
    }

    [Fact]
    public async Task Actual_empty_fence_keeps_native_header_and_copies_empty_payload()
    {
        var screen = TextInteractionTests.Screen("Dark");
        TextInteractionTests.Show(screen, TextInteractionTests.New("ParticipantCard", "Answerer", "```\n```", "Chat", DateTimeOffset.UnixEpoch, false));
        var root = (Visual)TextInteractionTests.Property(screen, "Root");
        await TerminalInteractionTestHost.Run(root, (context, phase, backend) =>
        {
            if (phase == 1)
            {
                var button = root.EnumerateVisualsDepthFirst().OfType<Button>().Single();
                Assert.Equal(1, button.Bounds.Height);
                context.App.Focus(button);
                TerminalInteractionTestHost.Key(backend, TerminalKey.Enter);
            }
            if (phase == 2) { Assert.Equal(1, backend.Writes); Assert.Equal("", backend.Written); }
        }, last: 3);
    }

    [Fact]
    public async Task Detach_during_transport_cannot_revive_feedback()
    {
        var policy = TextInteractionTests.New("TextInteraction");
        var header = (Visual)TextInteractionTests.New("CodeCopyHeader", "", ForgeText.Styles("Dark"), policy);
        var button = (Button)TextInteractionTests.Property(header, "Button");
        var slot = new Padder(header);
        await TerminalInteractionTestHost.Run(slot, (context, phase, backend) =>
        {
            if (phase == 1)
            {
                context.App.Focus(button);
                backend.DuringWrite = () => slot.Content = null;
                TerminalInteractionTestHost.Key(backend, TerminalKey.Enter);
            }
            if (phase == 2) { Assert.Equal(1, backend.Writes); Assert.Equal("", TextInteractionTests.Feedback(policy)); }
        }, last: 3);
    }

    private static void AssertLabel(NativeFrame frame, Button button, string label) =>
        Assert.Contains(label, frame.Lines[button.Bounds.Y]);

    private static Color Foreground(Button button)
    {
        var style = button.GetStyle<ButtonStyle>().Resolve(button.GetTheme(), true, button.HasFocus, false, false, ControlTone.Default);
        Assert.True(style.TryGetForeground(out var color));
        return color;
    }
}
