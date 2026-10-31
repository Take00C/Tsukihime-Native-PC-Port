using System.Text;
using Tsuki.Data.Containers;

namespace Tsuki.Tests.Data;

/// <summary>Paths inside the user's game folder (read-only).</summary>
public static class GameImage
{
    /// <summary>The 12 triplet archives (FORMATS 2).</summary>
    public static readonly string[] Triplets =
        ["allpac0", "allpac1", "allpac1_2", "allpac2", "allpac3", "allpac4", "parts", "bgm", "vse", "vag", "voice", "voice2"];

    /// <summary>The triplets whose entries are mrgd00 image containers.</summary>
    public static readonly string[] ImageTriplets = ["allpac0", "allpac1", "allpac1_2", "allpac2", "allpac3", "allpac4", "parts"];

    /// <summary>The 8 standalone mrgd00 files (FORMATS 2).</summary>
    public static readonly string[] Standalone =
        ["allscr.mrg", "script_text.mrg", "ft_en.mrg", "ft_ja.mrg", "ft_ko.mrg", "ft_zc.mrg", "ft_zt.mrg", "se_nam.mrg"];

    /// <summary>Full path of <c>image/&lt;name&gt;</c>.</summary>
    public static string Path(string name) => System.IO.Path.Combine(GameFolder.Require(), "image", name);

    /// <summary>Opens a triplet by its base name.</summary>
    public static MrgArchive Open(string name) => MrgArchive.Open(Path(name));
}

/// <summary>Collects mismatches so that a failing test names the first few instead of stopping at one.</summary>
public sealed class Mismatches
{
    private readonly List<string> _list = [];
    private int _total;

    /// <summary>Number of mismatches recorded.</summary>
    public int Total => _total;

    /// <summary>Compares two values and records a mismatch.</summary>
    public void Equal<T>(string what, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Add($"{what}: expected {expected}, got {actual}");
        }
    }

    /// <summary>Records a mismatch.</summary>
    public void Add(string message)
    {
        lock (_list)
        {
            _total++;
            if (_list.Count < 12)
            {
                _list.Add(message);
            }
        }
    }

    /// <summary>Fails the test when anything was recorded.</summary>
    public void AssertNone(string context)
    {
        if (_total != 0)
        {
            var sb = new StringBuilder();
            sb.Append(context).Append(": ").Append(_total).AppendLine(" mismatch(es); first ones:");
            foreach (string s in _list)
            {
                sb.Append("  ").AppendLine(s);
            }

            Assert.Fail(sb.ToString());
        }
    }
}
