using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace Katasec.Forge.Terminal.Extensions;

internal static class ClipboardMenus
{
    internal static Func<bool> CaptureEligibility(Visual source, Func<bool> contentUnchanged)
    {
        var app = source.App;
        var parent = source.Parent;
        return () => app is not null && source.App == app && source.Parent == parent
            && IsEligible(source) && contentUnchanged();
    }

    internal static MenuItem Item(string name, Visual source, Func<bool> available, Action execute) =>
        new(name, new Command
        {
            Id = $"Forge.Clipboard.{name}", LabelMarkup = name,
            Gesture = new KeyGesture(name == "Copy" ? XenoAtom.Terminal.TerminalChar.CtrlC : XenoAtom.Terminal.TerminalChar.CtrlV,
                XenoAtom.Terminal.TerminalModifiers.Ctrl),
            CanExecute = _ => available(),
            Execute = _ => { if (available()) execute(); },
        }) { CommandTarget = source };

    private static bool IsEligible(Visual source)
    {
        for (Visual? current = source; current is not null; current = current.Parent)
            if (!current.IsEnabled || !current.IsVisible) return false;
        return true;
    }
}
