using Tsuki.Data.Icons;

namespace Tsuki.Cli.Commands;

public static partial class ReleaseCheck
{
    public const string PersonalNote = "PERSONAL_BUILD.txt";

    public static string? IconProblem(string exe, IReadOnlyList<IconFrame> crescent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exe);
        ArgumentNullException.ThrowIfNull(crescent);
        if (crescent.Count == 0)
        {
            return "cannot be compared: the crescent icon file could not be read";
        }

        if (PeIcons.IconGroup(exe) is not { } group)
        {
            return $"carries no application icon group (id {PeIcons.ApplicationIconGroup})";
        }

        IReadOnlyList<IconFrame> frames = FaceIcon.DecodeIco(group);
        return SameFrames(crescent, frames)
            ? null
            : $"carries an icon that is not the port's crescent ({frames.Count} frames, {string.Join(", ", frames.Select(f => f.Size))} px): a personal build, or a folder whose launcher wrote Arcueid into its programs (package K43)";
    }

    private static bool SameFrames(IReadOnlyList<IconFrame> a, IReadOnlyList<IconFrame> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Size != b[i].Size || !a[i].Rgba.AsSpan().SequenceEqual(b[i].Rgba))
            {
                return false;
            }
        }

        return true;
    }
}
