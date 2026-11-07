using System.Text.RegularExpressions;
using Tsuki.Data.Icons;

namespace Tsuki.Cli.Commands;

public sealed partial class ReleaseSumsCommand
{
    /// <summary>True for a Tsukihime Mac ZIP; routed here before the Windows updater ZIP rules.</summary>
    private static bool IsReleaseMacZipName(string name) => name.StartsWith("Tsukihime-", StringComparison.OrdinalIgnoreCase)
        && name.Contains("-macos", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates one ditto-made Tsukihime.app ZIP: its entries the way the macOS updater checks them (<see cref="ReleaseRules.CheckMacZipEntries"/>: one root, safe
    /// paths, Unix entry kinds and modes, ditto's code-signature metadata, the size limits), then the unpacked app (release-check's Mac checks, with its code
    /// signature, and both assemblies). It is deliberately not a Windows updater asset: the Windows launcher continues to ask only for Tsukihime-vX.Y.Z-win-x64.zip.
    /// </summary>
    public static IReadOnlyList<string> CheckReleaseMacZip(string zipPath, byte[] crescentIco, byte[]? crescentIcns = null, ReleaseLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(crescentIco);
        string name = Path.GetFileName(zipPath);
        var problems = new List<string>();
        Match match = ReleaseMacZipName().Match(name);
        if (!match.Success)
        {
            problems.Add($"{name}: a Mac release ZIP must be named Tsukihime-vX.Y.Z-macos-arm64.zip or Tsukihime-vX.Y.Z-macos-x64.zip");
            return problems;
        }

        string version = $"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}";
        string arch = match.Groups[4].Value;
        crescentIcns ??= ReleaseCheckCommand.DefaultMacIcns();
        string temporary = Path.Combine(Path.GetTempPath(), "tsuki-mac-release-" + Guid.NewGuid().ToString("N"));
        try
        {
            var found = new List<string>();
            string app = ReleaseRules.UnpackMacZip(zipPath, temporary, found, limits);
            if (found.Count == 0)
            {
                // never checked further when an entry was refused: nothing of such an archive is unpacked
                IReadOnlyList<IconFrame> crescent = FaceIcon.DecodeIco(crescentIco);
                ReleaseRules.CheckMacFolder(app, arch, crescentIcns, version, found, [], requireCodeSignature: true);
                ReleaseRules.CheckMacAssembly(app, version, crescent, found);
                ReleaseRules.CheckMacAssembly(app, version, crescent, found, ReleaseRules.MacLauncher);
            }

            problems.AddRange(found.Select(problem => $"{name}: {problem}"));
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            problems.Add($"{name}: not a readable Mac release ZIP ({e.Message})");
        }
        finally
        {
            DeleteQuietly(temporary);
        }

        return problems;
    }

    [GeneratedRegex(@"^Tsukihime-v(\d{1,5})\.(\d{1,5})\.(\d{1,5})-macos-(arm64|x64)\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseMacZipName();
}
