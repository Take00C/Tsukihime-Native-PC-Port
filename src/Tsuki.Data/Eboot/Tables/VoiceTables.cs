using System.Buffers.Binary;
using System.Text;

namespace Tsuki.Data.Eboot.Tables;

/// <summary>
/// The voice speaker tables (knife <c>game.rs</c> <c>VOICE_SPEAKERS</c>, <c>SPEAKER_ROW_BIT</c>, <c>VOICE_ROW_WORDS</c>). A voice file name starts with a
/// three-letter prefix (<c>A00_0001</c>); the speaker number picks the character's config voice row. Port of
/// <c>tsuki.engine_tables.read_voice_speakers</c>, <c>read_speaker_row_bits</c> and <c>read_voice_row_words</c>.
/// </summary>
public sealed class VoiceSpeakerTable
{
    /// <summary>Text VA of the speaker records: 36 x 16 bytes {char key[8]; i32 speaker; i32 0}, the key reversed (<c>00A</c> for <c>A00</c>).</summary>
    public const long Va = 0x215E90;

    /// <summary>Number of speaker records.</summary>
    public const int RecordCount = 36;

    /// <summary>Data VA of <c>i32[24]</c>: speaker to config voice row bit (row = log2).</summary>
    public const long RowBitVa = 0x86A850;

    /// <summary>Number of speakers.</summary>
    public const int SpeakerCount = 24;

    /// <summary>Text VA of <c>i32[19]</c>: config voice row to its progress F word.</summary>
    public const long RowWordVa = 0x1A6850;

    /// <summary>Number of config voice rows.</summary>
    public const int RowCount = 19;

    private readonly Dictionary<string, int> _prefixes;

    private VoiceSpeakerTable(Dictionary<string, int> prefixes, int[] rowBits, int[] rowWords)
    {
        _prefixes = prefixes;
        RowBits = rowBits;
        RowWords = rowWords;
    }

    /// <summary>Speaker to config voice row bit, 24 entries.</summary>
    public IReadOnlyList<int> RowBits { get; }

    /// <summary>Config voice row to progress word, 19 entries.</summary>
    public IReadOnlyList<int> RowWords { get; }

    /// <summary>The voice file name prefixes (file-name order, upper case) with their speaker; unlisted prefixes are speaker 0.</summary>
    public IReadOnlyDictionary<string, int> Prefixes => _prefixes;

    /// <summary>Reads the three tables.</summary>
    /// <exception cref="EbootFormatException">A table is not file-backed.</exception>
    public static VoiceSpeakerTable Read(EbootReader eboot)
    {
        ArgumentNullException.ThrowIfNull(eboot);
        Dictionary<string, int> prefixes = [];
        for (int k = 0; k < eboot.Layout.VoiceSpeakerRecords; k++)
        {
            ReadOnlySpan<byte> rec = eboot.Read(eboot.Layout.VoiceSpeakers + (16L * k), 16);
            int nul = rec[..8].IndexOf((byte)0);
            char[] key = [.. Encoding.ASCII.GetString(rec[..(nul < 0 ? 8 : nul)])];
            Array.Reverse(key);
            prefixes[new string(key)] = BinaryPrimitives.ReadInt32LittleEndian(rec[8..]);
        }

        return new VoiceSpeakerTable(prefixes, ReadInts(eboot, eboot.Layout.VoiceRowBits, SpeakerCount), ReadInts(eboot, eboot.Layout.VoiceRowWords, RowCount));
    }

    /// <summary>The speaker of a voice file name (first three letters, case-insensitive); 0 when unlisted.</summary>
    public int SpeakerOf(string voiceName)
    {
        ArgumentNullException.ThrowIfNull(voiceName);
        return voiceName.Length >= 3 && _prefixes.TryGetValue(voiceName[..3].ToUpperInvariant(), out int s) ? s : 0;
    }

    internal static int[] ReadInts(EbootReader eboot, long va, int count)
    {
        ReadOnlySpan<byte> raw = eboot.Read(va, 4 * count);
        int[] values = new int[count];
        for (int k = 0; k < count; k++)
        {
            values[k] = BinaryPrimitives.ReadInt32LittleEndian(raw[(4 * k)..]);
        }

        return values;
    }
}
