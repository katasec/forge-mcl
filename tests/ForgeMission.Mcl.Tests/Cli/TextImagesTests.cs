using static ForgeMission.Tests.Cli.ForgeText;

namespace ForgeMission.Tests.Cli;

// forge chat TUI (Phase 56 Task 4): the session's text images. Each distinct image is drawn and
// sent once; asking again only names its id. Text ids have bit 23 set, so they never meet a tile
// id (22 bits); the rest is a hash that is the same in every run, and a clash probes the next id.
public sealed class TextImagesTests
{
    private const uint TextBit = 1u << 23;
    private static readonly Type ImagesType = Type("ForgeMission.Cli.Tui.Graphics.TextImages");

    [Fact]
    public void An_image_is_sent_once_and_then_named_by_its_id()
    {
        var sent = new List<(uint Id, int Cols, int Rows)>();
        var images = Images(sent);

        var first = Call(images, "Get", Request("Name", "Answerer"))!;
        var again = Call(images, "Get", Request("Name", "Answerer"))!;
        var other = Call(images, "Get", Request("Heading2", "Answerer"))!;

        Assert.Equal(2, sent.Count);
        Assert.Equal(Get<uint>(first, "Id"), Get<uint>(again, "Id"));
        Assert.NotEqual(Get<uint>(first, "Id"), Get<uint>(other, "Id"));
        Assert.Equal((Get<uint>(first, "Id"), Get<int>(first, "Cols"), 1), sent[0]);
    }

    [Fact]
    public void Text_ids_have_bit_23_and_stay_inside_the_24_bits_a_placeholder_carries()
    {
        var sent = new List<(uint Id, int Cols, int Rows)>();
        var images = Images(sent);
        foreach (var word in new[] { "a", "b", "Answerer", "Assistant", "forge", "chat / Chat" })
            Call(images, "Get", Request("Name", word));

        Assert.All(sent, s => Assert.InRange(s.Id, TextBit, (1u << 24) - 1));
    }

    [Fact]
    public void A_hash_clash_takes_the_next_free_id_and_wraps_inside_23_bits()
    {
        var assign = ImagesType.GetMethod("AssignId", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        uint Assign(uint hash, Func<uint, bool> taken) => (uint)assign.Invoke(null, [hash, taken])!;

        Assert.Equal(TextBit | 5, Assign(5, _ => false));
        Assert.Equal(TextBit | 7, Assign(5, id => id is (TextBit | 5) or (TextBit | 6)));
        Assert.Equal(TextBit, Assign(TextBit - 1, id => id == (TextBit | (TextBit - 1))));
    }

    [Fact]
    public void The_hash_is_the_same_in_every_run_and_tells_inputs_apart()
    {
        var hash = ImagesType.GetMethod("Hash", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        uint Hash(int theme, int w, int h, string kind, string text) => (uint)hash.Invoke(null, [theme, Cell(w, h), Request(kind, text)])!;

        // FNV-1a is fixed: this value only changes if the hash or its inputs change.
        Assert.Equal(Hash(0, 10, 21, "Name", "Answerer"), Hash(0, 10, 21, "Name", "Answerer"));
        Assert.Equal(0x3428E0u, Hash(0, 10, 21, "Name", "Answerer"));
        Assert.NotEqual(Hash(0, 10, 21, "Name", "Answerer"), Hash(1, 10, 21, "Name", "Answerer"));
        Assert.NotEqual(Hash(0, 10, 21, "Name", "Answerer"), Hash(0, 19, 42, "Name", "Answerer"));
        Assert.NotEqual(Hash(0, 10, 21, "Name", "Answerer"), Hash(0, 10, 21, "Heading2", "Answerer"));
        Assert.All(new[] { Hash(0, 10, 21, "Name", "x"), Hash(1, 60, 60, "Send", "↵") }, h => Assert.True(h < TextBit));
    }

    private static object Images(List<(uint, int, int)> sent)
    {
        Action<uint, byte[], int, int> send = (id, _, cols, rows) => sent.Add((id, cols, rows));
        return Activator.CreateInstance(ImagesType, Art("Light", 10, 21), 0, Cell(10, 21), send)!;
    }
}
