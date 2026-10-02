using System.Buffers.Binary;

namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56 G9: pair kerning from a font's GPOS table, read by us because StbTrueType reads only the
// legacy kern table and Inter keeps its kerning in GPOS. Scope is exactly what forge's subset Inter
// uses: the lookups of every 'kern' feature, applied in lookup order; pair adjustment (type 2)
// directly or inside Extension lookups (type 9); PairPos formats 1 and 2; Coverage formats 1 and 2;
// ClassDef formats 1 and 2; the first glyph's x advance. As HarfBuzz does, within a lookup the first
// subtable that applies wins: format 1 applies when it lists the pair, format 2 whenever the first
// glyph is covered. Verified against HarfBuzz for every pair in the allowed set (GposKerningTests).
// Pure bytes: no StbTrueType here. A table it cannot read throws InvalidDataException at load.
internal sealed class GposKerning
{
    private const ushort PairAdjustment = 2, Extension = 9;
    private const ushort XPlacement = 0x1, YPlacement = 0x2, XAdvance = 0x4;

    private readonly IReadOnlyList<IReadOnlyList<PairSubtable>> _lookups;

    private GposKerning(IReadOnlyList<IReadOnlyList<PairSubtable>> lookups) => _lookups = lookups;

    /// <summary>Reads the kerning of <paramref name="font"/> (a whole TTF).</summary>
    public static GposKerning Read(byte[] font)
    {
        try
        {
            var gpos = new Reader(font, TableOffset(font, "GPOS"));
            var lookupList = gpos.At(gpos.U16(8));
            var lookups = KernLookupIndices(gpos.At(gpos.U16(6)))
                .Select(index => ReadLookup(lookupList.At(lookupList.U16(2 + 2 * index))))
                .ToList();
            if (lookups.Count == 0) throw new InvalidDataException("The font's GPOS table has no 'kern' feature.");
            return new GposKerning(lookups);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("The font's GPOS table points outside the font.");
        }
    }

    /// <summary>The x-advance adjustment, in font units, between glyph <paramref name="left"/> and
    /// the glyph <paramref name="right"/> that follows it.</summary>
    public int Adjust(int left, int right)
    {
        var total = 0;
        foreach (var lookup in _lookups)
        {
            foreach (var subtable in lookup)
            {
                if (subtable.TryAdjust(left, right, out var value))
                {
                    total += value;
                    break;
                }
            }
        }
        return total;
    }

    // ── Font structure ───────────────────────────────────────────────────────────────────────

    private static int TableOffset(byte[] font, string tag)
    {
        var directory = new Reader(font, 0);
        for (var i = 0; i < directory.U16(4); i++)
        {
            var record = 12 + 16 * i;
            if (directory.Tag(record) == tag) return checked((int)directory.U32(record + 8));
        }
        throw new InvalidDataException($"The font has no {tag} table.");
    }

    /// <summary>Every lookup index named by a 'kern' feature, once each, in lookup order.</summary>
    private static IEnumerable<int> KernLookupIndices(Reader featureList)
    {
        var indices = new SortedSet<int>();
        for (var i = 0; i < featureList.U16(0); i++)
        {
            var record = 2 + 6 * i;
            if (featureList.Tag(record) != "kern") continue;
            var feature = featureList.At(featureList.U16(record + 4));
            for (var k = 0; k < feature.U16(2); k++) indices.Add(feature.U16(4 + 2 * k));
        }
        return indices;
    }

    private static List<PairSubtable> ReadLookup(Reader lookup)
    {
        var type = lookup.U16(0);
        var subtables = new List<PairSubtable>();
        for (var i = 0; i < lookup.U16(4); i++)
        {
            var subtable = lookup.At(lookup.U16(6 + 2 * i));
            var (innerType, inner) = type == Extension
                ? (subtable.U16(2), subtable.At(checked((int)subtable.U32(4))))
                : (type, subtable);
            if (innerType != PairAdjustment)
                throw new InvalidDataException($"A 'kern' lookup has type {innerType}; only pair adjustment is read.");
            subtables.Add(ReadPairPos(inner));
        }
        return subtables;
    }

    private static PairSubtable ReadPairPos(Reader table)
    {
        var coverage = ReadCoverage(table.At(table.U16(2)));
        var value1 = table.U16(4);
        var value2 = table.U16(6);
        var size = ValueRecordSize(value1) + ValueRecordSize(value2);
        var advanceAt = AdvanceOffset(value1);
        return table.U16(0) switch
        {
            1 => ReadFormat1(table, coverage, size, advanceAt),
            2 => ReadFormat2(table, coverage, size, advanceAt),
            var format => throw new InvalidDataException($"PairPos format {format} is not supported."),
        };
    }

    /// <summary>Format 1: per covered first glyph, a list of (second glyph, values).</summary>
    private static PairSubtable ReadFormat1(Reader table, Dictionary<int, int> coverage, int size, int? advanceAt)
    {
        var pairs = new Dictionary<(int, int), int>();
        foreach (var (glyph, index) in coverage)
        {
            var set = table.At(table.U16(10 + 2 * index));
            for (var k = 0; k < set.U16(0); k++)
            {
                var record = 2 + k * (2 + size);
                pairs[(glyph, set.U16(record))] = Advance(set, record + 2, advanceAt);
            }
        }
        return new PairSubtable(coverage, (left, right) => pairs.TryGetValue((left, right), out var v) ? v : null);
    }

    /// <summary>Format 2: a class matrix over two class definitions; applies to every covered first glyph.</summary>
    private static PairSubtable ReadFormat2(Reader table, Dictionary<int, int> coverage, int size, int? advanceAt)
    {
        var first = ReadClassDef(table.At(table.U16(8)));
        var second = ReadClassDef(table.At(table.U16(10)));
        int rows = table.U16(12), cols = table.U16(14);
        var matrix = new int[rows * cols];
        for (var i = 0; i < matrix.Length; i++) matrix[i] = Advance(table, 16 + i * size, advanceAt);
        return new PairSubtable(coverage, (left, right) =>
        {
            int c1 = first.GetValueOrDefault(left), c2 = second.GetValueOrDefault(right);
            return c1 < rows && c2 < cols ? matrix[c1 * cols + c2] : null;
        });
    }

    /// <summary>Glyph → coverage index.</summary>
    private static Dictionary<int, int> ReadCoverage(Reader table)
    {
        var map = new Dictionary<int, int>();
        switch (table.U16(0))
        {
            case 1:
                for (var i = 0; i < table.U16(2); i++) map[table.U16(4 + 2 * i)] = i;
                return map;
            case 2:
                for (var i = 0; i < table.U16(2); i++)
                {
                    var range = 4 + 6 * i;
                    int start = table.U16(range), end = table.U16(range + 2), index = table.U16(range + 4);
                    for (var glyph = start; glyph <= end; glyph++) map[glyph] = index + glyph - start;
                }
                return map;
            default:
                throw new InvalidDataException($"Coverage format {table.U16(0)} is not supported.");
        }
    }

    /// <summary>Glyph → class; a glyph not listed is class 0.</summary>
    private static Dictionary<int, int> ReadClassDef(Reader table)
    {
        var map = new Dictionary<int, int>();
        switch (table.U16(0))
        {
            case 1:
                for (var i = 0; i < table.U16(4); i++) map[table.U16(2) + i] = table.U16(6 + 2 * i);
                return map;
            case 2:
                for (var i = 0; i < table.U16(2); i++)
                {
                    var range = 4 + 6 * i;
                    for (var glyph = table.U16(range); glyph <= table.U16(range + 2); glyph++) map[glyph] = table.U16(range + 4);
                }
                return map;
            default:
                throw new InvalidDataException($"ClassDef format {table.U16(0)} is not supported.");
        }
    }

    /// <summary>Bytes in a value record: two per field present (device-table offsets included).</summary>
    private static int ValueRecordSize(ushort format) => 2 * System.Numerics.BitOperations.PopCount((uint)(format & 0xFF));

    /// <summary>Where the x advance sits in the first value record, or null when it has none.</summary>
    private static int? AdvanceOffset(ushort format) => (format & XAdvance) == 0
        ? null
        : 2 * System.Numerics.BitOperations.PopCount((uint)(format & (XPlacement | YPlacement)));

    private static int Advance(Reader table, int record, int? advanceAt) => advanceAt is { } at ? table.S16(record + at) : 0;

    /// <summary>One pair-adjustment subtable: its coverage and how it finds a covered pair's value
    /// (null when it does not apply).</summary>
    private sealed class PairSubtable(Dictionary<int, int> coverage, Func<int, int, int?> find)
    {
        public bool TryAdjust(int left, int right, out int value)
        {
            value = 0;
            if (!coverage.ContainsKey(left) || find(left, right) is not { } found) return false;
            value = found;
            return true;
        }
    }

    /// <summary>Big-endian reads relative to a table's start; out-of-range reads throw.</summary>
    private readonly struct Reader(byte[] data, int start)
    {
        public Reader At(int offset) => new(data, checked(start + offset));
        public ushort U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(start + offset, 2));
        public short S16(int offset) => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(start + offset, 2));
        public uint U32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start + offset, 4));
        public string Tag(int offset) => System.Text.Encoding.ASCII.GetString(data, start + offset, 4);
    }
}
