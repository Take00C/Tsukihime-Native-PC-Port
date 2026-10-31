using Tsuki.Data.Containers;

namespace Tsuki.Tests.Synthetic.Data;

/// <summary>
/// Package K11: the archives are mapped whole and every page a decode reads stays in the process's working set (3.8 GB after 200 s of a fast soak, nothing leaked).
/// <see cref="MappedFile.TrimResident"/> hands the whole mapped range to the shell's trimming function; Tsuki.Data makes no P/Invoke, so the function is the caller's.
/// </summary>
public sealed class PageTrimTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-pagetrim-" + Guid.NewGuid().ToString("N"));

    public PageTrimTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void TrimmingHandsOutTheWholeMappingAndLeavesItReadable()
    {
        string path = Path.Combine(_dir, "pages.bin");
        byte[] content = new byte[(5 * 1024 * 1024) + 123];
        new Random(5).NextBytes(content);
        File.WriteAllBytes(path, content);
        using var f = MappedFile.Open(path);
        Assert.True(content.AsSpan(4096, 1000).SequenceEqual(f.Span(4096, 1000)));

        var pieces = new List<(nint Address, nuint Length)>();
        f.TrimResident((address, length) => pieces.Add((address, length)));

        Assert.Single(pieces);                                          // one piece: the file is far under 1 GiB
        Assert.Equal((nuint)content.Length, pieces[0].Length);
        Assert.NotEqual((nint)0, pieces[0].Address);
        Assert.True(content.AsSpan(4096, 1000).SequenceEqual(f.Span(4096, 1000)), "the data is the same after the trim (a soft fault, not a loss)");
        Assert.True(content.AsSpan(content.Length - 100).SequenceEqual(f.Span(content.Length - 100, 100)));
    }

    [Fact]
    public void AnEmptyMappingHandsOutNothingAndADisposedOneRefuses()
    {
        string empty = Path.Combine(_dir, "empty.bin");
        File.WriteAllBytes(empty, []);
        using (var e = MappedFile.Open(empty))
        {
            int calls = 0;
            e.TrimResident((_, _) => calls++);
            Assert.Equal(0, calls);
        }

        string path = Path.Combine(_dir, "x.bin");
        File.WriteAllBytes(path, new byte[100]);
        var f = MappedFile.Open(path);
        f.Dispose();
        Assert.Throws<ObjectDisposedException>(() => f.TrimResident((_, _) => { }));
        using var g = MappedFile.Open(path);
        Assert.Throws<ArgumentNullException>(() => g.TrimResident(null!));
    }
}
