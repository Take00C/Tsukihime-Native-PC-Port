// The version numbers of the update check (package K40, GitHub issue #6): a release tag is "vX.Y.Z" (the tags of the port's GitHub releases), the running launcher's
// version is its informational version ("0.1.3+<commit>", "0.1.3-personal+<commit>" for a personal build, package K28). Only plain X.Y.Z numbers are compared; a tag
// with anything else ("v0.2.0-rc1", "nightly") is not a release the launcher installs.
using System.Globalization;

namespace Tsuki.Launcher.Core;

/// <summary>A release number X.Y.Z, ordered numerically (0.1.10 is newer than 0.1.9).</summary>
/// <param name="Major">X.</param>
/// <param name="Minor">Y.</param>
/// <param name="Patch">Z.</param>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    /// <summary>The tag of this version on GitHub: "v0.1.3".</summary>
    public string Tag => "v" + ToString();

    /// <inheritdoc/>
    public int CompareTo(ReleaseVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);

    /// <summary>Newer than.</summary>
    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;

    /// <summary>Older than.</summary>
    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;

    /// <summary>Newer or the same.</summary>
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;

    /// <summary>Older or the same.</summary>
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;

    /// <summary>"0.1.3".</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>Reads exactly "X.Y.Z" (each part 0..99999, digits only); false for anything else.</summary>
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        string[] parts = (text ?? string.Empty).Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (int i = 0; i < 3; i++)
        {
            string p = parts[i];
            if (p.Length is 0 or > 5 || !p.All(char.IsAsciiDigit) || !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new ReleaseVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>Reads a release tag: "v0.1.3" or "0.1.3" (surrounding blanks ignored); false for a tag with a suffix ("v0.2.0-rc1") or anything else.</summary>
    public static bool TryParseTag(string? tag, out ReleaseVersion version)
    {
        string t = (tag ?? string.Empty).Trim();
        if (t.Length > 0 && t[0] is 'v' or 'V')
        {
            t = t[1..];
        }

        return TryParse(t, out version);
    }

    /// <summary>
    /// Reads an assembly's informational version: the build metadata after '+' (the commit) and a pre-release label after '-' ("-personal" of a personal build) are
    /// dropped, then the X.Y.Z that is left is read. False when no X.Y.Z is left.
    /// </summary>
    public static bool TryParseInformational(string? informational, out ReleaseVersion version)
    {
        string t = (informational ?? string.Empty).Trim();
        int plus = t.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            t = t[..plus];
        }

        int dash = t.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            t = t[..dash];
        }

        return TryParse(t, out version);
    }
}
