using System.Buffers.Binary;
using System.Text;

namespace Tsuki.Data.Eboot.Tables;

/// <summary>
/// The confirm-dialog and help message ids (knife <c>ui.rs</c> <c>DIALOG_SYSMES</c>, dword_3C9340): <c>i32[55]</c> at text VA 0x2D2D40, -1 = no message. The first
/// 26 are the confirm dialogs (knife's 24 plus two the PS4 inserts after dialog 20); the rest are the ending-list prompts (docs/formats/gallery.md 1, "help messages").
/// The ids are ui_text ids; knife's SYSMES numbers are the Japanese ones and differ from 217 on by one (<see cref="Ps4SysmesId"/>).
/// Port of <c>tsuki.engine_tables.read_dialog_messages</c>.
/// </summary>
public sealed class DialogMessageTable
{
    /// <summary>Text VA.</summary>
    public const long Va = 0x2D2D40;

    /// <summary>Number of entries.</summary>
    public const int Count = 55;

    /// <summary>The overseas SYSMES table has one message more from this number on.</summary>
    public const int SysmesOverseasInsert = 217;

    private readonly int[] _ids;

    private DialogMessageTable(int[] ids)
    {
        _ids = ids;
    }

    /// <summary>The ui_text id of each entry, -1 = none.</summary>
    public ReadOnlySpan<int> Ids => _ids;

    /// <summary>Reads the table.</summary>
    /// <exception cref="EbootFormatException">The table is not file-backed.</exception>
    public static DialogMessageTable Read(EbootReader eboot)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        return new DialogMessageTable(VoiceSpeakerTable.ReadInts(eboot, eboot.Layout.DialogMessages, Count));
    }

    /// <summary>knife's (Japanese) SYSMES message number as the PS4 ui_text id.</summary>
    public static int Ps4SysmesId(int knifeId) => knifeId >= SysmesOverseasInsert ? knifeId + 1 : knifeId;
}

/// <summary>
/// The config SE test list (knife <c>game.rs</c> <c>TEST_SES</c>): 14 inline 13-byte sound effect names at text VA 0x1A6774 (<c>moon_se02009</c> ...).
/// Port of <c>tsuki.engine_tables.read_test_se_names</c>.
/// </summary>
public static class TestSeNames
{
    /// <summary>Text VA.</summary>
    public const long Va = 0x1A6774;

    /// <summary>Number of names.</summary>
    public const int Count = 14;

    /// <summary>Reads the names.</summary>
    /// <exception cref="EbootFormatException">The table is not file-backed.</exception>
    public static IReadOnlyList<string> Read(EbootReader eboot)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        string[] names = new string[Count];
        for (int k = 0; k < Count; k++)
        {
            ReadOnlySpan<byte> rec = eboot.Read(eboot.Layout.TestSeNames + (13L * k), 13);
            int nul = rec.IndexOf((byte)0);
            names[k] = Encoding.ASCII.GetString(rec[..(nul < 0 ? 13 : nul)]);
        }

        return names;
    }
}

/// <summary>
/// The message colours of <c>@c1</c>..<c>@c5</c> (knife <c>text.rs</c> <c>fill_colour</c>; docs/formats/text.md 6): <c>u16[6]</c> RGB565 at text VA 0x2D34C0, word 0
/// the default colour, words 1..5 the five fill colours. Port of <c>tsuki.engine_tables.read_text_colours</c>.
/// </summary>
public static class TextColourTable
{
    /// <summary>Text VA.</summary>
    public const long Va = 0x2D34C0;

    /// <summary>Reads the five fill colours as 8-bit (R, G, B), the 5 and 6 bit channels expanded by bit replication.</summary>
    /// <exception cref="EbootFormatException">The table is not file-backed.</exception>
    public static IReadOnlyList<(byte R, byte G, byte B)> Read(EbootReader eboot)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        ReadOnlySpan<byte> raw = eboot.Read(eboot.Layout.TextColours, 12);
        (byte, byte, byte)[] colours = new (byte, byte, byte)[5];
        for (int k = 0; k < 5; k++)
        {
            int w = BinaryPrimitives.ReadUInt16LittleEndian(raw[(2 * (k + 1))..]);
            int r5 = w >> 11, g6 = (w >> 5) & 63, b5 = w & 31;
            colours[k] = ((byte)((r5 << 3) | (r5 >> 2)), (byte)((g6 << 2) | (g6 >> 4)), (byte)((b5 << 3) | (b5 >> 2)));
        }

        return colours;
    }
}
