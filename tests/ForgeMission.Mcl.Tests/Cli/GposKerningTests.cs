using System.Security.Cryptography;
using static ForgeMission.Tests.Cli.ForgeText;

namespace ForgeMission.Tests.Cli;

// forge chat TUI (Phase 56 Task 4, G9): forge's own GPOS pair-kerning reader against HarfBuzz. The
// golden tables (Fixtures/Kerning) hold, for every pair in the image-text allowed set, the kerning
// hb-shape 12.1.0 applied to the committed subset fonts (eng/fonts/inter-subset.sh makes them), so
// these tests need no HarfBuzz. Each table names the SHA-256 of the font it was made from; a font
// changed without regenerating its table fails here.
[Collection(CpuBoundCollection.Name)]
public sealed class GposKerningTests
{
    [Theory]
    [InlineData("Inter-SemiBold")]
    [InlineData("Inter-Bold")]
    public void Every_allowed_pair_matches_HarfBuzz(string font)
    {
        var golden = Golden.Read(font);
        var glyphs = Glyphs($"{font}.ttf");
        var kern = glyphs.GetType().GetMethod("KernUnits")!;
        var mismatches = new List<string>();

        for (var i = 0; i < golden.Codepoints.Length; i++)
        for (var j = 0; j < golden.Codepoints.Length; j++)
        {
            var actual = (int)kern.Invoke(glyphs, [golden.Codepoints[i], golden.Codepoints[j]])!;
            if (actual != golden.Kerning[i, j])
                mismatches.Add($"U+{golden.Codepoints[i]:X4} U+{golden.Codepoints[j]:X4}: HarfBuzz {golden.Kerning[i, j]}, forge {actual}");
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} of {golden.Kerning.Length} pairs differ:\n{string.Join('\n', mismatches.Take(20))}");
    }

    [Theory]
    [InlineData("Inter-SemiBold")]
    [InlineData("Inter-Bold")]
    public void The_golden_table_was_made_from_the_embedded_font_and_covers_the_allowed_set(string font)
    {
        var golden = Golden.Read(font);

        Assert.Equal(golden.FontSha256, Convert.ToHexStringLower(SHA256.HashData(Resource($"{font}.ttf"))));
        Assert.Equal(AllowedSet.Order(), golden.Codepoints.Order());
        Assert.Contains(golden.Kerning.Cast<int>(), value => value != 0);
    }

    [Fact]
    public void A_font_without_a_GPOS_table_is_refused()
    {
        var font = Resource("Inter-Bold.ttf");
        RenameTable(font, "GPOS", "XPOS");

        var thrown = Assert.Throws<InvalidDataException>(() => Load(font));
        Assert.Contains("no GPOS table", thrown.Message);
    }

    [Fact]
    public void A_GPOS_table_pointing_outside_the_font_is_refused()
    {
        var font = Resource("Inter-Bold.ttf");
        var record = TableRecord(font, "GPOS");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(record + 8), (uint)font.Length - 4);

        Assert.Throws<InvalidDataException>(() => Load(font));
    }

    private static object Load(byte[] font)
    {
        var reader = Type("ForgeMission.Cli.Tui.Graphics.GposKerning").GetMethod("Read")!;
        try { return reader.Invoke(null, [font])!; }
        catch (System.Reflection.TargetInvocationException wrapped) when (wrapped.InnerException is { } inner) { throw inner; }
    }

    private static void RenameTable(byte[] font, string tag, string renamed) =>
        System.Text.Encoding.ASCII.GetBytes(renamed).CopyTo(font, TableRecord(font, tag));

    private static int TableRecord(byte[] font, string tag)
    {
        var count = font[4] << 8 | font[5];
        for (var i = 0; i < count; i++)
        {
            var record = 12 + 16 * i;
            if (System.Text.Encoding.ASCII.GetString(font, record, 4) == tag) return record;
        }
        throw new InvalidOperationException($"No {tag} table.");
    }

    /// <summary>A golden table: the font's SHA-256, the allowed set's codepoints, and the kerning
    /// matrix (row = left character, column = right), in font units.</summary>
    private sealed record Golden(string FontSha256, int[] Codepoints, int[,] Kerning)
    {
        public static Golden Read(string font)
        {
            var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Kerning", $"{font}.kern.txt"));
            var sha = lines.Single(l => l.StartsWith("# font-sha256 ", StringComparison.Ordinal))["# font-sha256 ".Length..];
            var codepoints = lines.Single(l => l.StartsWith("# codepoints ", StringComparison.Ordinal))["# codepoints ".Length..]
                .Split(' ').Select(hex => Convert.ToInt32(hex, 16)).ToArray();
            var rows = lines.Where(l => !l.StartsWith('#')).ToArray();
            var kerning = new int[codepoints.Length, codepoints.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                var values = rows[i].Split(' ');
                for (var j = 0; j < values.Length; j++) kerning[i, j] = int.Parse(values[j], System.Globalization.CultureInfo.InvariantCulture);
            }
            Assert.Equal(codepoints.Length, rows.Length);
            return new Golden(sha, codepoints, kerning);
        }
    }
}
