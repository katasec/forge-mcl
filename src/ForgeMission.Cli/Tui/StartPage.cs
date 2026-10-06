using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace ForgeMission.Cli.Tui;

// forge chat start page (Phase 60): the TUI's first view, like the Desktop's start page. ChatTui
// shows it where the transcript is (ChatScreen.ShowEditor) and Chat with a mission (selected when
// it opens) swaps in the chat (CloseEditor). Create a mission is shown and does nothing.
internal sealed class StartPage
{
    private const int ChatIndex = 1;

    public StartPage(ForgeStyles styles, Action openChat)
    {
        List = new OptionList<OptionListItem>(
            [
                Row(styles, "Create a mission", "Write and evaluate a reusable mission version, then approve it for use."),
                Row(styles, "Chat with a mission", "Start a durable chat on an approved mission version. It stays pinned to that version."),
            ],
            ChatIndex)
            .ActivateOnClick(true)
            .ItemActivated((_, e) =>
            {
                if (e.Index == ChatIndex) openChat();
            });
        View = new VStack(
                new TextBlock("Where do you want to start?") { IsSelectable = false }.Style(styles.FallbackStrong),
                List.Margin(new Thickness(0, 1, 0, 0)))
            .Margin(new Thickness(styles.TranscriptGutterCols, 1, 1, 0));
    }

    /// <summary>The page, shown where the transcript is.</summary>
    public Visual View { get; }

    /// <summary>The two rows; focused when the TUI starts.</summary>
    public OptionList<OptionListItem> List { get; }

    private static OptionListItem Row(ForgeStyles styles, string name, string description) => new()
    {
        Content = new TextBlock(name) { IsSelectable = false },
        Description = new TextBlock(description) { IsSelectable = false }.Style(styles.Label),
    };
}
