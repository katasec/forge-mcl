namespace ForgeMission.Tests.Cli;

// XenoAtom.Terminal.UI keeps process-wide UI state: a running TerminalApp binds the one Dispatcher
// to its UI thread, and every bindable write (building a visual) on another thread then throws
// "Invalid thread access". Tests that build visuals or run a TerminalApp share this collection, so
// they never run at the same time as each other.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class XenoAtomUiCollection
{
    public const string Name = "XenoAtom UI";
}
