namespace Katasec.Forge.Terminal.Extensions;

/// <summary>A synchronous clipboard fact, without presentation or editing policy.</summary>
public enum ClipboardResult
{
    NoSelection,
    Copied,
    CopyFailed,
    PasteReadSucceeded,
    PasteReadFailed,
}
