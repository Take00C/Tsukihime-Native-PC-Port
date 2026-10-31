using System.Buffers.Binary;
using Tsuki.Engine.Text;

namespace Tsuki.Tests.Synthetic.Text;

/// <summary>
/// The canonical serialisation of a layout block (a copy of the one in Tsuki.Tests.Data): byte for byte what <c>tools/oracle/textbox.py</c>
/// <c>canonical</c> writes (version 1), so a SHA-1 match is a bit-exact match of every field.
/// </summary>
internal static class CanonicalBlock
{
    public const int Version = 1;

    public static byte[] Serialize(TextBlock b)
    {
        int n = b.N;
        int size = 60 + 64 + 128 + 128 + 4 + 60 * b.Records.Length + n + 2 * n;
        var buf = new byte[size];
        int o = 0;
        void I32(int v)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o), v);
            o += 4;
        }

        void U32(uint v)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), v);
            o += 4;
        }

        void F32(float v) => U32(BitConverter.SingleToUInt32Bits(v));

        U32(Version);
        I32(b.N);
        I32(b.NLines);
        I32(b.BoxWidth);
        I32(b.WidthArg);
        I32(b.Height);
        I32(b.Flags);
        I32(b.Spacing);
        I32(b.Leading);
        I32(b.LastGlyphLine);
        I32(b.EndCtr);
        I32(b.YField);
        I32(b.Empty ? 1 : 0);
        F32(b.Sx);
        F32(b.Sy);
        for (int i = 0; i < TextBlock.LineSlots; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), b.LineGlyphs[i]);
            o += 2;
        }

        for (int i = 0; i < TextBlock.LineSlots; i++)
        {
            F32(b.LineWidth[i]);
        }

        for (int i = 0; i < TextBlock.LineSlots; i++)
        {
            F32(b.LineHeight[i]);
        }

        I32(b.Records.Length);
        foreach (var r in b.Records)
        {
            I32(r.Index);
            I32(r.Flags);
            I32(r.Color);
            I32(r.Ext);
            I32(r.Line);
            I32(r.Tex);
            I32(r.X);
            I32(r.Y);
            I32(r.X2);
            I32(r.Y2);
            I32(r.Ctr);
            F32(r.Dx);
            F32(r.Dy);
            F32(r.Sx);
            F32(r.Sy);
        }

        b.Classes.AsSpan(0, n).CopyTo(buf.AsSpan(o));
        o += n;
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), b.WordLen[i]);
            o += 2;
        }

        if (o != size)
        {
            throw new InvalidOperationException($"canonical size {o} != {size}");
        }

        return buf;
    }

    /// <summary>The readable dump of <c>python -m oracle.textbox dump</c>, for comparing a failing line by eye.</summary>
    public static string Describe(TextBlock b)
    {
        var sb = new System.Text.StringBuilder();
        static string H(float f) => "0x" + BitConverter.SingleToUInt32Bits(f).ToString("x8");
        sb.AppendLine($"n={b.N} nlines={b.NLines} box_width={b.BoxWidth} last_glyph_line={b.LastGlyphLine} end_ctr={b.EndCtr} y_field={b.YField} empty={(b.Empty ? "True" : "False")}");
        sb.AppendLine($"sx={H(b.Sx)} sy={H(b.Sy)}");
        for (int k = 0; k < b.NLines; k++)
        {
            sb.AppendLine($"line {k}: glyphs={b.LineGlyphs[k]} width={H(b.LineWidth[k])} height={H(b.LineHeight[k])}");
        }

        foreach (var r in b.Records)
        {
            sb.AppendLine($"rec {r.Index} fl=0x{r.Flags:x} col=0x{r.Color:x} ext={r.Ext} line={r.Line} tex=0x{r.Tex:x} rect=({r.X},{r.Y},{r.X2},{r.Y2}) ctr={r.Ctr} dx={H(r.Dx)} dy={H(r.Dy)} sx={H(r.Sx)} sy={H(r.Sy)}");
        }

        return sb.ToString();
    }
}
