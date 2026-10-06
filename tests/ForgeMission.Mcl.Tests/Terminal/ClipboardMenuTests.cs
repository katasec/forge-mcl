using Katasec.Forge.Terminal.Extensions;
using ForgeMission.Tests.Cli;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Text;

namespace ForgeMission.Tests.TerminalInteraction;

[Collection(XenoAtomUiCollection.Name)]
public sealed class ClipboardMenuTests
{
    [Theory]
    [InlineData("escape")]
    [InlineData("tab")]
    [InlineData("outside")]
    [InlineData("copy")]
    [InlineData("mouse-copy")]
    public async Task Actual_right_click_menu_keeps_backwards_range_at_native_close_milestone(string action)
    {
        var editor = new PromptEditor { Text = "alpha beta" };
        var reports = new List<ClipboardResult>();
        editor.ConfigureClipboard(reports.Add);
        var closed = false;
        await TerminalInteractionTestHost.Run(editor, (context, phase, backend) =>
        {
            if (phase == 1)
            {
                context.App.Focus(editor);
                TerminalInteractionTestHost.Key(backend, TerminalKey.End);
                var word = OperatingSystem.IsMacOS() ? TerminalModifiers.Alt : TerminalModifiers.Ctrl;
                TerminalInteractionTestHost.Key(backend, TerminalKey.Left, word | TerminalModifiers.Shift);
            }
            if (phase == 2)
            {
                Assert.True(editor.TryCopySelection(out var selected));
                Assert.Equal("beta", selected);
                backend.PushEvent(new TerminalMouseEvent { Kind = TerminalMouseKind.Down, Button = TerminalMouseButton.Right, X = 18, Y = 12 });
            }
            if (phase == 3)
            {
                var popup = context.App.Root.EnumerateVisualsDepthFirst().OfType<Popup>().Single();
                popup.ClosedRouted += (_, _) =>
                {
                    closed = true;
                    Assert.True(editor.HasFocus);
                    Assert.True(editor.TryCopySelection(out var selected));
                    Assert.Equal("beta", selected);
                    Assert.Equal(6, editor.CaretIndex);
                };
                Assert.Equal(17, popup.Content!.Bounds.Width);
                Assert.Equal(6, popup.Content.Bounds.Height);
                if (action == "mouse-copy") backend.PushEvent(new TerminalMouseEvent { Kind = TerminalMouseKind.Down, Button = TerminalMouseButton.Left, X = popup.Content.Bounds.X + 2, Y = popup.Content.Bounds.Y + 2 });
                else if (action == "outside") backend.PushEvent(new TerminalMouseEvent { Kind = TerminalMouseKind.Down, Button = TerminalMouseButton.Left, X = 90, Y = 30 });
                else TerminalInteractionTestHost.Key(backend, action switch { "escape" => TerminalKey.Escape, "tab" => TerminalKey.Tab, _ => TerminalKey.Enter });
            }
            if (phase == 4)
            {
                Assert.True(closed);
                Assert.Equal(action.EndsWith("copy") ? 1 : 0, backend.Writes);
                if (action.EndsWith("copy")) { Assert.Equal("beta", backend.Written); Assert.Equal([ClipboardResult.Copied], reports); }
                else Assert.Empty(reports);
            }
        });
    }

    [Theory]
    [InlineData(false, "", ClipboardResult.PasteReadSucceeded)]
    [InlineData(true, "ignored", ClipboardResult.PasteReadFailed)]
    [InlineData(false, "replacement", ClipboardResult.PasteReadSucceeded)]
    public async Task Native_menu_Paste_retains_range_or_native_undo(bool failed, string payload, ClipboardResult expected)
    {
        var editor = new CodeEditor { Text = "alpha\nbeta" };
        var results = new List<ClipboardResult>();
        editor.ConfigureClipboard(results.Add);
        Popup? popup = null;
        var caret = 0;
        var inserts = !failed && payload.Length > 0;
        await TerminalInteractionTestHost.Run(editor, (context, phase, backend) =>
        {
            if (phase == 1)
            {
                context.App.Focus(editor);
                backend.PushEvent(new TerminalTextEvent { Text = "!" });
                TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlA);
            }
            if (phase == 2)
            {
                Assert.True(editor.HasSelection);
                caret = editor.CaretIndex;
                backend.TrySetClipboardText(payload);
                backend.FailRead = failed;
                var menu = editor.ContextMenuFactory!(editor).ToArray();
                Assert.Equal(2, menu.Length);
                Assert.All(menu, item => Assert.Same(editor, item.CommandTarget));
                popup = ContextMenuService.Show(editor, menu, 2, 2);
                TerminalInteractionTestHost.Key(backend, TerminalKey.Down);
                TerminalInteractionTestHost.Key(backend, TerminalKey.Enter);
            }
            if (phase == 3)
            {
                Assert.NotNull(popup);
                Assert.Equal(expected, results.Single());
                Assert.Equal(1, backend.Reads);
                Assert.Equal(inserts ? payload : "!alpha\nbeta", editor.Text);
                if (!inserts)
                {
                    Assert.True(editor.TryCopySelection(out var selection));
                    Assert.Equal("!alpha\nbeta", selection);
                    Assert.Equal(caret, editor.CaretIndex);
                }
                editor.Commands.Single(command => command.Id == "TextEditor.Undo").Execute(editor);
            }
            if (phase == 4)
            {
                Assert.Equal(inserts ? "!alpha\nbeta" : "alpha\nbeta", editor.Text);
                if (inserts) editor.Commands.Single(command => command.Id == "TextEditor.Undo").Execute(editor);
            }
            if (phase == 5) Assert.Equal("alpha\nbeta", editor.Text);
        }, last: 6);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("document")]
    [InlineData("disabled")]
    [InlineData("hidden")]
    [InlineData("detached")]
    [InlineData("ancestor-disabled")]
    [InlineData("ancestor-hidden")]
    [InlineData("screen-swap")]
    public async Task Editor_menu_rejects_stale_availability_and_execution(string mutation)
    {
        var editor = new PromptEditor { Text = "alpha beta" };
        var reports = new List<ClipboardResult>();
        editor.ConfigureClipboard(reports.Add);
        var slot = new Padder(editor);
        Command[] captured = [];
        await TerminalInteractionTestHost.Run(slot, (context, phase, backend) =>
        {
            if (phase == 1) { context.App.Focus(editor); TerminalInteractionTestHost.Ctrl(backend, TerminalChar.CtrlA); }
            if (phase == 2)
            {
                captured = editor.ContextMenuFactory!(editor).Select(item => item.Command!).ToArray();
                Assert.True(captured[0].CanExecute!(editor));
                switch (mutation)
                {
                    case "version": editor.Text = "different"; break;
                    case "document": editor.TextDocument = new TextDocument("alpha beta"); break;
                    case "disabled": editor.IsEnabled = false; break;
                    case "hidden": editor.IsVisible = false; break;
                    case "detached": slot.Content = null; break;
                    case "ancestor-disabled": slot.IsEnabled = false; break;
                    case "ancestor-hidden": slot.IsVisible = false; break;
                    case "screen-swap": slot.Content = new PromptEditor("new screen"); break;
                }
                foreach (var command in captured)
                {
                    Assert.False(command.CanExecute!(editor));
                    command.Execute(editor);
                }
                Assert.Empty(reports);
                Assert.Equal(0, backend.Writes);
                Assert.Equal(0, backend.Reads);
            }
        }, last: 3);
    }

    [Fact]
    public async Task Paragraph_menu_retains_native_range_and_rejects_content_change()
    {
        var paragraph = new Paragraph("alpha beta") { HorizontalAlignment = Align.Stretch };
        var results = new List<ClipboardResult>();
        paragraph.ConfigureClipboard(results.Add);
        Command? copy = null;
        await TerminalInteractionTestHost.Run(paragraph, (context, phase, backend) =>
        {
            if (phase == 1) TerminalInteractionTestHost.Drag(backend, paragraph, 10, 6);
            if (phase == 2)
            {
                Assert.True(paragraph.HasSelection);
                copy = Assert.Single(paragraph.ContextMenuFactory!(paragraph)).Command!;
                backend.PushEvent(new TerminalMouseEvent { Kind = TerminalMouseKind.Down, Button = TerminalMouseButton.Right, X = paragraph.Bounds.X + 7, Y = paragraph.Bounds.Y });
            }
            if (phase == 3)
            {
                var popup = context.App.Root.EnumerateVisualsDepthFirst().OfType<Popup>().Single();
                Assert.Equal(16, popup.Content!.Bounds.Width);
                Assert.Equal(5, popup.Content.Bounds.Height);
                backend.PushEvent(new TerminalMouseEvent { Kind = TerminalMouseKind.Down, Button = TerminalMouseButton.Left, X = popup.Content.Bounds.X + 2, Y = popup.Content.Bounds.Y + 2 });
            }
            if (phase == 4)
            {
                Assert.Equal("beta", backend.Written);
                Assert.True(paragraph.HasSelection);
                paragraph.Text = "new text";
                Assert.False(copy!.CanExecute!(paragraph));
                copy.Execute(paragraph);
                Assert.Equal(1, backend.Writes);
                Assert.Equal([ClipboardResult.Copied], results);
            }
        }, last: 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paragraph_menu_rejects_ineligible_ancestor_before_availability_and_execution(bool hidden)
    {
        var paragraph = new Paragraph("alpha beta");
        var reports = new List<ClipboardResult>();
        paragraph.ConfigureClipboard(reports.Add);
        var slot = new Padder(paragraph);
        await TerminalInteractionTestHost.Run(slot, (context, phase, backend) =>
        {
            if (phase == 1) TerminalInteractionTestHost.Drag(backend, paragraph, 6, 10);
            if (phase == 2)
            {
                var command = paragraph.ContextMenuFactory!(paragraph).Single().Command!;
                Assert.True(command.CanExecute!(paragraph));
                if (hidden) slot.IsVisible = false; else slot.IsEnabled = false;
                Assert.False(command.CanExecute(paragraph));
                command.Execute(paragraph);
                Assert.Equal(0, backend.Writes);
                Assert.Empty(reports);
            }
        }, last: 3);
    }
}
