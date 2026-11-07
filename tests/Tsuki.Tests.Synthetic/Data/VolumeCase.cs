namespace Tsuki.Tests.Synthetic.Data;

/// <summary>
/// Whether the volume of a test folder compares names without case: NTFS, macOS's default APFS, a WSL folder on a Windows drive. There a path the resolver finds
/// keeps the caller's spelling (the file exists under it), so the tests compare paths the way the volume does, not the way the operating system usually does.
/// </summary>
internal static class VolumeCase
{
    /// <summary>True when a file made in <paramref name="dir"/> can be found under its upper-case name.</summary>
    public static bool IgnoresCase(string dir)
    {
        string probe = Path.Combine(dir, "case-probe-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(probe, []);
        try
        {
            return File.Exists(Path.Combine(dir, Path.GetFileName(probe).ToUpperInvariant()));
        }
        finally
        {
            File.Delete(probe);
        }
    }

    /// <summary>The comparer for paths on the volume of <paramref name="dir"/>.</summary>
    public static StringComparer PathComparer(string dir) => IgnoresCase(dir) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
