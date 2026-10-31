using System.Text;
using Tsuki.Engine.Vm;

namespace Tsuki.Engine.Sound;

/// <summary>
/// The keyword tag scan of the audio handlers (docs/formats/sound_runtime.md 3.1; eboot VA 0x87276..0x87308) and the argument
/// helpers they share. Port of <c>parse_tags</c>, <c>find_tag</c>, <c>_arg_text</c> and <c>_count_args</c>. All functions work on the
/// raw statement bytes: <c>sep</c> is the index of the separator byte after the four name bytes (<c>Statement.Args[0]</c>).
/// </summary>
internal sealed class SoundTags
{
    /// <summary>The tag of the fade time in milliseconds (<c>`001</c>).</summary>
    public const int Fade = 1;

    /// <summary>The fade-out time of the old track (<c>`011</c>).</summary>
    public const int Wait = 11;

    /// <summary>The volume (<c>`017</c>).</summary>
    public const int Volume = 17;

    /// <summary>The pan (<c>`018</c>).</summary>
    public const int Pan = 18;

    /// <summary>The loop argument (<c>`049</c>).</summary>
    public const int Loop = 49;

    /// <summary>The delay of a deferred command (<c>`068</c>).</summary>
    public const int Delay = 68;

    /// <summary>The start sample (<c>`069</c>).</summary>
    public const int Start = 69;

    /// <summary>The character of a voice-like SE (<c>`070</c>).</summary>
    public const int Character = 70;

    /// <summary>Tag byte per argument position 0..63 (0xFF = untagged).</summary>
    public byte[] Tags { get; } = new byte[64];

    /// <summary>1 + the position of the last tagged argument (0 if none); the handlers search positions 0..N-1 only.</summary>
    public int Count { get; private set; }

    private static byte At(ReadOnlySpan<byte> buf, int i) => (uint)i < (uint)buf.Length ? buf[i] : (byte)0;

    /// <summary>
    /// The engine tag scan: an argument whose first byte is a backtick gets the tag byte
    /// <c>((d1 * 10 + d2) * 10 + d3) mod 256</c> of its three digit characters, any other argument 0xFF.
    /// </summary>
    public static SoundTags Parse(ReadOnlySpan<byte> buf, int sep)
    {
        var t = new SoundTags();
        int ntags = 0;
        int p = sep + 1;
        for (int i = 0; i < 64; i++)
        {
            t.Tags[i] = 0xFF;
            if ((uint)p < (uint)buf.Length && buf[p] == 0x60)
            {
                int b0 = At(buf, p + 1);
                int b1 = At(buf, p + 2);
                int b2 = At(buf, p + 3);
                int v = ((b0 * 10) & 0xFF) + b1 & 0xFF;
                v = (((v * 10) & 0xFF) + b2 + 0x30) & 0xFF;
                t.Tags[i] = (byte)v;
                p += 5;
                ntags = i + 1;
            }

            int start = p;
            int q = p + 1;
            bool end = false;
            bool broke = false;
            for (int n = 0; n < 500; n++)
            {
                byte c = At(buf, q - 1);
                if (c is 0x29 or 0x3B)
                {
                    end = true;
                    broke = true;
                    break;
                }

                if (c == 0x2C)
                {
                    broke = true;
                    break;
                }

                q++;
            }

            if (!broke)
            {
                q = start + 500;
            }

            if (end)
            {
                break;
            }

            p = q;
        }

        t.Count = ntags;
        return t;
    }

    /// <summary>The position of the first argument tagged <paramref name="tag"/> among the first <see cref="Count"/>, or -1.</summary>
    public int Find(int tag)
    {
        for (int j = 0; j < Count; j++)
        {
            if (Tags[j] == tag)
            {
                return j;
            }
        }

        return -1;
    }

    /// <summary>
    /// Argument <paramref name="index"/> copied the way the SE and voice handlers copy a name: skip a 5-byte backtick tag, stop at
    /// NUL ) , ;.
    /// </summary>
    public static string ArgText(ReadOnlySpan<byte> b, int sep, int index)
    {
        int p = sep + 1;
        for (int i = 0; i < index; i++)
        {
            bool found = false;
            for (int k = 0; k < 500; k++)
            {
                byte c = At(b, p + k);
                if (c is 0x29 or 0x3B)
                {
                    return "";
                }

                if (c == 0x2C)
                {
                    p += k + 1;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                p += 500;
            }
        }

        if ((uint)p < (uint)b.Length && b[p] == 0x60)
        {
            p += 5;
        }

        var sb = new StringBuilder();
        while ((uint)p < (uint)b.Length && b[p] is not (0 or 0x29 or 0x2C or 0x3B))
        {
            sb.Append((char)b[p]);
            p++;
        }

        return sb.ToString();
    }

    /// <summary>VA 0x99F12..0x99F5A: 1 + the number of commas before ) or ; (at most 0x1F3 bytes).</summary>
    public static int CountArgs(ReadOnlySpan<byte> b, int sep)
    {
        int n = 1;
        int k = sep + 1;
        while (k < b.Length && k - sep < 0x1F9)
        {
            byte c = b[k];
            if (c is 0x29 or 0x3B)
            {
                break;
            }

            if (c == 0x2C)
            {
                n++;
            }

            k++;
        }

        return n;
    }

    /// <summary>The inline integer reader (VA 0x76690 / 0x76780) at argument <paramref name="index"/>.</summary>
    public static int Int(ReadOnlySpan<byte> b, int sep, int index, int bits = 16) => InlineReader.ReadInt(b, sep + 1, index, bits);

    /// <summary>The inline "present" test at argument <paramref name="index"/>.</summary>
    public static bool Present(ReadOnlySpan<byte> b, int sep, int index) => InlineReader.IsPresent(b, sep + 1, index);
}
