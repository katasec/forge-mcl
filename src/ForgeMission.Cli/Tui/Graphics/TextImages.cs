using System.Text;

namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56 Task 4: the session's text images. An image is drawn and sent the first time something
// asks for it, on the UI thread (KittyImages asserts it), and named by its id from then on: a
// re-parse of a reply only redraws cells, and nothing is deleted within a session. Text ids have bit
// 23 set (tile ids use 22 bits, TileSet.ImageIds); the other 23 bits are an FNV-1a hash of (theme,
// cell size, kind, text), which also fix the image's size, so a later run in the same window gives
// the same image the same id. A clash with different content probes the next free id.
internal sealed class TextImages
{
    internal const uint TextBit = 1u << 23;
    private const uint HashMask = TextBit - 1;

    private readonly TextArt _art;
    private readonly int _themeSlot;
    private readonly CellSize _cell;
    private readonly Action<uint, byte[], int, int> _send;
    private readonly Dictionary<TextImageRequest, TextImage> _images = [];
    private readonly HashSet<uint> _taken = [];

    /// <summary>Images are sent through <paramref name="send"/> (id, PNG, cols, rows):
    /// KittyImages.Transmit in forge chat.</summary>
    public TextImages(TextArt art, int themeSlot, CellSize cell, Action<uint, byte[], int, int> send)
    {
        _art = art;
        _themeSlot = themeSlot;
        _cell = cell;
        _send = send;
    }

    /// <summary>The image for <paramref name="request"/>: drawn and sent the first time, then from
    /// the cache. Only text TextArt.Allows may be asked for.</summary>
    public TextImage Get(TextImageRequest request)
    {
        if (_images.TryGetValue(request, out var known)) return known;
        var art = _art.Render(request);
        var id = AssignId(Hash(_themeSlot, _cell, request), _taken.Contains);
        _taken.Add(id);
        _send(id, Png.Encode(art.Image), art.Cols, art.Rows);
        return _images[request] = new TextImage(id, art.Cols, art.Rows);
    }

    /// <summary>A heading's lines at <paramref name="maxCols"/> (TextArt.Wrap), never wider than a
    /// placeholder row can address.</summary>
    public IReadOnlyList<string> WrapHeading(TextKind kind, string text, int maxCols) =>
        _art.Wrap(kind, text, Math.Clamp(maxCols, 1, PlaceholderDiacritics.Values.Length));

    /// <summary>The text id for <paramref name="hash"/>: bit 23 plus its low 23 bits, or the next id
    /// up (wrapping inside the 23 bits) while <paramref name="taken"/> says it is in use.</summary>
    internal static uint AssignId(uint hash, Func<uint, bool> taken)
    {
        var id = TextBit | (hash & HashMask);
        while (taken(id)) id = TextBit | ((id + 1) & HashMask);
        return id;
    }

    /// <summary>FNV-1a (32 bits, stable across runs) of the image's inputs, folded to 23 bits.</summary>
    internal static uint Hash(int themeSlot, CellSize cell, TextImageRequest request)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes($"{themeSlot}|{cell.Width}x{cell.Height}|{request.Kind}|{request.Split}|{request.Text}"))
            hash = (hash ^ b) * 16777619u;
        return (hash >> 23) ^ (hash & HashMask);
    }
}

/// <summary>A sent text image: its id and the cells it covers.</summary>
internal sealed record TextImage(uint Id, int Cols, int Rows);
