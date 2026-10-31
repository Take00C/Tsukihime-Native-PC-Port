using System.Collections.Concurrent;
using System.Text;
using Tsuki.Data.IO;
using Tsuki.Data.Text;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Data.Text;

/// <summary>The opened <c>script_text.mrg</c> of the user's folder, shared by the tests of one process (the folder is only read).</summary>
internal static class SharedText
{
    private static readonly Lazy<ScriptText> Lazy = new(() => ScriptText.Open(new DirectoryGameFiles(GameFolder.Require())));

    public static ScriptText Value => Lazy.Value;

    public static IGameFiles Files { get; } = new DirectoryGameFiles(GameFolder.Path ?? ".");
}

/// <summary>
/// The per-line hashes of the C# side, computed once per (language, category) and compared with the oracle's. Categories: <c>engine</c>,
/// <c>tok</c>, <c>tokscript</c>, <c>runs</c>, <c>plain</c>.
/// </summary>
internal static class TextHashes
{
    private static readonly ConcurrentDictionary<(Language, string), Lazy<string[]>> Cache = new();

    public static string[] Get(Language lang, string category) =>
        Cache.GetOrAdd((lang, category), key => new Lazy<string[]>(() => Compute(key.Item1, key.Item2))).Value;

    private static string[] Compute(Language lang, string category)
    {
        TextTable table = SharedText.Value.Table(lang);
        string[] hashes = new string[table.Count];
        Parallel.For(0, table.Count, i =>
        {
            ReadOnlySpan<byte> line = table.LineBytes(i).Span;
            hashes[i] = category switch
            {
                "engine" => OracleCanon.H16(OracleCanon.Hex(table.EngineLine(i).Span)),
                "tok" => OracleCanon.H16(OracleCanon.Tokens(Markup.Tokenize(line, lang))),
                "tokscript" => OracleCanon.H16(OracleCanon.Tokens(Markup.Tokenize(line, lang, script: true))),
                "runs" => OracleCanon.H16(OracleCanon.Runs(Markup.ToRuns(Markup.Tokenize(line, lang), lang))),
                "plain" => OracleCanon.H16(OracleCanon.Plain(Markup.Tokenize(line, lang))),
                _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
            };
        });
        return hashes;
    }

    /// <summary>Fails with the first line whose hash differs from the oracle's concatenated 16-digit hashes.</summary>
    public static void AssertEqual(string what, string oracle, string[] ours)
    {
        Assert.Equal(ours.Length * 16, oracle.Length);
        var mismatches = new List<int>();
        for (int i = 0; i < ours.Length; i++)
        {
            if (!oracle.AsSpan(16 * i, 16).SequenceEqual(ours[i]))
            {
                mismatches.Add(i);
            }
        }

        if (mismatches.Count > 0)
        {
            int first = mismatches[0];
            Assert.Fail($"{what}: {mismatches.Count} of {ours.Length} lines differ, first id {first}: oracle {oracle.Substring(16 * first, 16)}, C# {ours[first]}");
        }
    }
}

/// <summary>Builds small mrgd00 containers (containers.md 4) for tests that run without game data.</summary>
internal static class MiniMzp
{
    /// <summary>Packs sections the way the packer does: 1..8 bytes of 0xFF after each, sector and byte offset, size in sectors plus the low 16 bits.</summary>
    public static byte[] Build(IReadOnlyList<byte[]> sections)
    {
        int dataStart = 8 + (8 * sections.Count);
        var body = new List<byte>();
        var records = new byte[8 * sections.Count];
        long pos = dataStart;
        for (int i = 0; i < sections.Count; i++)
        {
            long rel = pos - dataStart;
            int sectorOffset = (int)(rel >> 11);
            int inSector = (int)(rel & 0x7FF);
            int size = sections[i].Length;
            int sectors = (int)((inSector + size + 0x7FF) / 0x800);
            BitConverter.TryWriteBytes(records.AsSpan(8 * i), (ushort)sectorOffset);
            BitConverter.TryWriteBytes(records.AsSpan((8 * i) + 2), (ushort)inSector);
            BitConverter.TryWriteBytes(records.AsSpan((8 * i) + 4), (ushort)sectors);
            BitConverter.TryWriteBytes(records.AsSpan((8 * i) + 6), (ushort)(size & 0xFFFF));
            body.AddRange(sections[i]);
            long end = pos + size;
            int pad = 8 - (int)(end % 8);
            body.AddRange(Enumerable.Repeat((byte)0xFF, pad));
            pos = end + pad;
        }

        var file = new List<byte>();
        file.AddRange("mrgd00"u8.ToArray());
        file.AddRange(BitConverter.GetBytes((ushort)sections.Count));
        file.AddRange(records);
        file.AddRange(body);
        return [.. file];
    }

    /// <summary>A script_text.mrg-shaped container from five (offset table, blob) pairs built from line lists (each line gets its CR LF).</summary>
    public static byte[] ScriptText(IReadOnlyList<IReadOnlyList<string>> linesByLanguage, int endMarks = 1)
    {
        var sections = new List<byte[]>();
        foreach (IReadOnlyList<string> lines in linesByLanguage)
        {
            var blob = new List<byte>();
            var table = new List<byte>();
            foreach (string line in lines)
            {
                table.AddRange(Be(blob.Count));
                blob.AddRange(Encoding.UTF8.GetBytes(line + "\r\n"));
            }

            table.AddRange(Be(blob.Count));
            table.AddRange(Be(blob.Count));
            for (int i = 0; i < endMarks; i++)
            {
                table.AddRange(Be(-1));
            }

            sections.Add([.. table]);
            sections.Add([.. blob]);
        }

        return Build(sections);
    }

    private static byte[] Be(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
}
