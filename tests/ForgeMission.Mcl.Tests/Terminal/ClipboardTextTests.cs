using Katasec.Forge.Terminal.Extensions;
using ForgeMission.Tests.Cli;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace ForgeMission.Tests.TerminalInteraction;

[Collection(XenoAtomUiCollection.Name)]
public sealed class ClipboardTextTests
{
    [Theory]
    [InlineData(false, true, "alpha", false, ClipboardResult.NoSelection, 0)]
    [InlineData(true, false, "alpha", false, ClipboardResult.CopyFailed, 0)]
    [InlineData(true, true, "", false, ClipboardResult.CopyFailed, 0)]
    [InlineData(true, true, "  alpha\n\n\tbeta\n", false, ClipboardResult.Copied, 1)]
    [InlineData(true, true, "alpha", true, ClipboardResult.CopyFailed, 1)]
    public async Task Selection_extracts_once_and_never_falls_back(bool selected, bool extracted, string text,
        bool failure, ClipboardResult result, int writes)
    {
        var source = new Selection(selected, extracted, text);
        await TerminalInteractionTestHost.Run(new Paragraph("fixture"), (context, phase, backend) =>
        {
            if (phase != 1) return;
            backend.FailWrite = failure;
            Assert.Equal(result, ClipboardText.CopySelection(source, context.App.Terminal));
            Assert.Equal(writes, backend.Writes);
            Assert.Equal(selected ? 1 : 0, source.Extractions);
            Assert.Equal(0, source.Clears);
            if (result == ClipboardResult.Copied) Assert.Equal(text, backend.Written);
        }, last: 2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  a\n\n\tb\n")]
    public async Task Whole_text_keeps_exact_whitespace_and_explicit_retry(string text)
    {
        await TerminalInteractionTestHost.Run(new Paragraph("fixture"), (context, phase, backend) =>
        {
            if (phase != 1) return;
            backend.FailWrite = true;
            Assert.Equal(ClipboardResult.CopyFailed, ClipboardText.CopyText(text, context.App.Terminal));
            backend.FailWrite = false;
            Assert.Equal(ClipboardResult.Copied, ClipboardText.CopyText(text, context.App.Terminal));
            Assert.Equal(2, backend.Writes);
            Assert.Equal(text, backend.Written);
            Assert.Throws<ArgumentNullException>(() => ClipboardText.CopyText(null!, context.App.Terminal));
            backend.DuringWrite = () => throw new IOException("controlled transport failure");
            Assert.Throws<IOException>(() => ClipboardText.CopyText(text, context.App.Terminal));
        }, last: 2);
    }

    private sealed class Selection(bool selected, bool extracted, string text) : ISelectionOwner
    {
        public bool HasSelection => selected;
        public bool IsSelectable => false;
        public int Extractions { get; private set; }
        public int Clears { get; private set; }
        public bool TryCopySelection(out string value) { Extractions++; value = text; return extracted; }
        public void ClearSelection() => Clears++;
    }
}
