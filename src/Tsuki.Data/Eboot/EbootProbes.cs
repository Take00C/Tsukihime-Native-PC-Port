using System.Globalization;
using Tsuki.Data.Catalog;
using Tsuki.Data.Verification;

namespace Tsuki.Data.Eboot;

/// <summary>
/// The structural probes of RR 3.3 item 2 that <see cref="BuildCheck"/> does not run itself: the SELF segment table, the
/// image catalog, the picture-name list, the movie table, the scene-id table and <c>ui_text</c>. They are a self-test when
/// the SHA-256 matches and a diagnosis when it does not, so a damaged or foreign eboot.bin fails with a message instead of
/// an exception. (The eighth probe of the list, <c>EbootFontTables.verify()</c>, belongs to the font reader.)
/// </summary>
public static class EbootProbes
{
    /// <summary>Runs every probe on the bytes of eboot.bin. Never throws for malformed content.</summary>
    public static IReadOnlyList<CheckResult> Run(ReadOnlyMemory<byte> eboot)
    {
        var results = new List<CheckResult>();
        EbootReader reader;
        try
        {
            reader = EbootReader.Parse(eboot);
        }
        catch (Exception e) when (IsFormatProblem(e))
        {
            results.Add(Fail("segments", "SELF segment table", "10 plain segments", "unreadable", $"eboot.bin cannot be read as a PS4 SELF: {e.Message}"));
            return results;
        }

        Probe(results, "segments", "SELF segment table", () => Segments(reader.Image));
        Probe(results, "layout", "segment layout (text 0x2280, RELRO 0x32C210, data 0x365FA0)", () => Layout(reader.Image));
        Probe(results, "catalog", "image catalog: BLACK.mzp, BLUE.mzp, 14,744 records + 31 empty", () => Catalog(reader, eboot));
        Probe(results, "names", "picture-name list: BLACK first, 14,744 names + 1 empty, 0xFF", () => Names(reader));
        Probe(results, "movies", "movie table: titlebg1.mp4 first, 134 records, terminator -1", () => Movies(reader));
        Probe(results, "scenes", "scene-id table at text 0x216130 starts 110, 240, 260, 270", () => Scenes(reader));
        Probe(results, "uitext", "ui_text[ja][0] is \"NULL\"", () => UiTextFirst(reader));
        Probe(results, "uitext.table", "ui_text: 5 x 1,499 pointers equal their relocations", () => UiTextAll(reader));
        return results;
    }

    private static bool IsFormatProblem(Exception e) =>
        e is Containers.ContainerFormatException or ArgumentException or IndexOutOfRangeException or InvalidOperationException
            or OverflowException or KeyNotFoundException or InvalidCastException;

    private static void Probe(List<CheckResult> results, string id, string title, Func<(string Expected, string Actual, bool Ok)> check)
    {
        try
        {
            var (expected, actual, ok) = check();
            results.Add(ok
                ? new CheckResult("eboot.probe." + id, title, CheckStatus.Pass, BuildFailure.None, expected, actual, string.Empty)
                : Fail(id, title, expected, actual, $"{title}: expected {expected}, found {actual}. The tables are not where the analysed build has them."));
        }
        catch (Exception e) when (IsFormatProblem(e))
        {
            results.Add(Fail(id, title, "readable", "unreadable", $"{title}: {e.Message}"));
        }
    }

    private static CheckResult Fail(string id, string title, string expected, string actual, string message) =>
        new("eboot.probe." + id, title, CheckStatus.Fail, BuildFailure.EbootProbeFailed, expected, actual, message);

    private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    private static (string, string, bool) Segments(SelfImage self)
    {
        int count = self.SelfSegments.Count;
        bool plain = self.SelfSegments.All(s => !(s.Deflated || s.Encrypted));
        string expected = "10 segments, all plain";
        string actual = $"{count} segments, {(plain ? "all plain" : "some deflated or encrypted")}";
        return (expected, actual, count == 10 && plain);
    }

    private static (string, string, bool) Layout(SelfImage self)
    {
        var text = self.Text;
        var relro = self.Relro;
        var data = self.DataSegment;
        string Describe(ProgramHeader? p) => p is null
            ? "missing"
            : $"VA 0x{p.VAddr:X} file 0x{self.PhdrFileOffset(p) ?? -1:X} size 0x{p.FileSize:X}";
        string expected = $"text VA 0x0 file 0x{EbootAddresses.TextFileOffset:X} size 0x{EbootAddresses.TextSize:X}; "
            + $"RELRO VA 0x{EbootAddresses.RelroVa:X} file 0x{EbootAddresses.RelroFileOffset:X} size 0x{EbootAddresses.RelroSize:X}; "
            + $"data VA 0x{EbootAddresses.DataVa:X} file 0x{EbootAddresses.DataFileOffset:X} size 0x{EbootAddresses.DataSize:X}";
        string actual = $"text {Describe(text)}; RELRO {Describe(relro)}; data {Describe(data)}";
        bool ok = text.VAddr == 0 && self.PhdrFileOffset(text) == EbootAddresses.TextFileOffset && text.FileSize == EbootAddresses.TextSize
            && relro is not null && relro.VAddr == EbootAddresses.RelroVa && self.PhdrFileOffset(relro) == EbootAddresses.RelroFileOffset
            && relro.FileSize == EbootAddresses.RelroSize
            && data.VAddr == EbootAddresses.DataVa && self.PhdrFileOffset(data) == EbootAddresses.DataFileOffset
            && data.FileSize == EbootAddresses.DataSize;
        return (expected, actual, ok);
    }

    private static (string, string, bool) Catalog(EbootReader reader, ReadOnlyMemory<byte> file)
    {
        var catalog = ImageCatalog.Read(reader);
        var (offset, used, empty) = ImageCatalog.Locate(file.Span);
        string first = $"{catalog[0].Name0}, {catalog[1].Name0}";
        bool namesOk = string.Equals(catalog[0].Name0, "BLACK.mzp", StringComparison.OrdinalIgnoreCase)
            && string.Equals(catalog[1].Name0, "BLUE.mzp", StringComparison.OrdinalIgnoreCase);
        bool ok = namesOk && catalog.Count == EbootAddresses.ImageTableRecords && catalog.EmptySlots == 31
            && offset == catalog.FileOffset && used == catalog.Count && empty == catalog.EmptySlots;
        return (
            "BLACK.mzp, BLUE.mzp; 14,744 records + 31 empty; found by content at the same place",
            $"{first}; {N(catalog.Count)} records + {catalog.EmptySlots} empty; by content: 0x{offset:X}, {N(used)} + {empty}",
            ok);
    }

    private static (string, string, bool) Names(EbootReader reader)
    {
        var names = PictureNameLists.Read(reader);
        byte end = reader.Read(EbootAddresses.ImageNames + ((long)EbootAddresses.NameRecordSize * names.Count), 1)[0];
        bool ok = names.Count == PictureNameLists.Records && names[0] == "BLACK" && names[^1].Length == 0 && end == 0xFF;
        return (
            "BLACK first, 14,745 records (14,744 names + 1 empty), then 0xFF",
            $"{names[0]} first, {N(names.Count)} records, last {(names[^1].Length == 0 ? "empty" : "\"" + names[^1] + "\"")}, then 0x{end:X2}",
            ok);
    }

    private static (string, string, bool) Movies(EbootReader reader)
    {
        var movies = MovieTable.Read(reader);
        int terminator = reader.I32(EbootAddresses.MovieTerminator);
        bool ok = movies.Count == 134 && movies[0].MovieId == 0 && movies[0].Path == "/app0/image/movie/titlebg1.mp4" && terminator == -1;
        return (
            "134 records, record 0 id 0 /app0/image/movie/titlebg1.mp4, terminator -1 at 0xDCA590",
            $"{N(movies.Count)} records, record 0 id {movies[0].MovieId} {movies[0].Path}, terminator {terminator}",
            ok);
    }

    private static (string, string, bool) Scenes(EbootReader reader)
    {
        short[] ids = [reader.I16(EbootAddresses.SceneIds), reader.I16(EbootAddresses.SceneIds + 2),
            reader.I16(EbootAddresses.SceneIds + 4), reader.I16(EbootAddresses.SceneIds + 6)];
        bool ok = ids.AsSpan().SequenceEqual([(short)110, (short)240, (short)260, (short)270]);
        return ("110, 240, 260, 270", string.Join(", ", ids), ok);
    }

    private static (string, string, bool) UiTextFirst(EbootReader reader)
    {
        ulong ptr = reader.U64(EbootAddresses.UiText);
        string text = reader.CString((long)ptr, 16);
        return ("\"NULL\"", "\"" + text + "\"", text == "NULL");
    }

    private static (string, string, bool) UiTextAll(EbootReader reader)
    {
        var table = UiTextTable.Read(reader.Image);
        int n = UiTextTable.Languages.Count * UiTextTable.Count;
        int read = 0;
        for (int l = 0; l < UiTextTable.Languages.Count; l++)
        {
            read += table.Rows(l).Count;
        }

        return ($"{N(n)} entries", $"{N(read)} entries", read == n);
    }
}
