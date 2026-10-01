using System.Text;
using XenoAtom.Terminal.UI;

namespace ForgeMission.Cli.Tui.Graphics;

// Kitty graphics with Unicode placeholders (docs/design/tui-graphics.md): each image is
// transmitted once as a virtual placement; a cell shows part of it by holding U+10EEEE plus row
// and column diacritics, with the image id as its 24-bit foreground colour. This class is the
// only writer of image bytes to stdout. Its one RGB colour carries an image id, not a visual colour
// (the one exception in TuiColourLiteralTests).
internal static class KittyImages
{
    private const int Placeholder = 0x10EEEE;
    private const int ChunkChars = 4096;

    /// <summary>Sends <paramref name="png"/> as image <paramref name="id"/>, shown over
    /// cols x rows cells. Must run after the TUI has entered the alternate screen.</summary>
    public static void Transmit(uint id, byte[] png, int cols, int rows) =>
        WriteStdout(TransmitEscape(png, $"a=T,U=1,f=100,i={id},c={cols},r={rows},q=2"));

    /// <summary>One placeholder cell: image cell (row, col). Every cell carries both diacritics:
    /// bare placeholders count columns up, which blanks repeated one-column tiles.</summary>
    public static string Cell(int row, int col) =>
        char.ConvertFromUtf32(Placeholder) + char.ConvertFromUtf32(PlaceholderDiacritics.Values[row]) +
        char.ConvertFromUtf32(PlaceholderDiacritics.Values[col]);

    /// <summary>The foreground colour that names image <paramref name="id"/>.</summary>
    public static Color IdColor(uint id) => Color.Rgb((byte)(id >> 16), (byte)(id >> 8), (byte)id);

    /// <summary>Writes straight to the stdout stream: XenoAtom's terminal writer does not reach the
    /// terminal while the app runs (tui-graphics.md).</summary>
    private static void WriteStdout(string escape)
    {
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(Encoding.UTF8.GetBytes(escape));
        stdout.Flush();
    }

    private static string TransmitEscape(byte[] png, string keys)
    {
        var base64 = Convert.ToBase64String(png);
        var escape = new StringBuilder();
        for (var offset = 0; offset < base64.Length; offset += ChunkChars)
        {
            var part = base64.AsSpan(offset, Math.Min(ChunkChars, base64.Length - offset));
            var more = offset + ChunkChars < base64.Length ? 1 : 0;
            escape.Append("\u001b_G").Append(offset == 0 ? keys + "," : "").Append("m=").Append(more).Append(';').Append(part).Append("\u001b\\");
        }
        return escape.ToString();
    }
}
