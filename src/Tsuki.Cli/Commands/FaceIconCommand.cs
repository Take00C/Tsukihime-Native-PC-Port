using Tsuki.Data;
using Tsuki.Data.Eboot;
using Tsuki.Data.Icons;
using Tsuki.Data.IO;

namespace Tsuki.Cli.Commands;

/// <summary>
/// <c>Tsuki.Cli face-icon --game DIR --out DIR [--force]</c> (package K28): builds the Arcueid icon of package K23 (<see cref="FaceIconCache"/>, the same file the game
/// and the launcher build into the profile folder) from the game folder into <c>DIR\icons\tsukihime.ico</c> with its stamp beside it, or leaves a current one alone.
/// <c>src/Tsuki.PersonalIcon.targets</c> runs it before the compile of a PERSONAL build, with the project's obj/ folder as <c>--out</c>. Exit 1 when the folder has no
/// usable picture (the build must not fall back to another icon by itself).
/// </summary>
public sealed class FaceIconCommand : ICliCommand
{
    /// <inheritdoc/>
    public string Name => "face-icon";

    /// <inheritdoc/>
    public string Summary => "build the Arcueid icon (K23) from the game folder into DIR\\icons\\tsukihime.ico; face-icon --game DIR --out DIR [--force]";

    /// <inheritdoc/>
    public int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? game = null;
        string? outDir = null;
        bool force = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--game" when i + 1 < args.Length:
                    game = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    outDir = args[++i];
                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    error.WriteLine($"face-icon: unexpected argument '{args[i]}'");
                    return ExitCode.Usage;
            }
        }

        if (string.IsNullOrWhiteSpace(outDir))
        {
            error.WriteLine("face-icon: --out DIR is required (the icon goes to DIR\\icons\\tsukihime.ico)");
            return ExitCode.Usage;
        }

        string? folder = GameFolderLocator.Resolve(game);
        if (folder is null || !(Directory.Exists(folder) || File.Exists(folder)))
        {
            error.WriteLine("face-icon: no game folder; pass --game <dir> or set TSUKI_GAME_DIR to the CUSA47999 folder");
            return ExitCode.Usage;
        }

        if (force)
        {
            File.Delete(FaceIconCache.StampPath(outDir));
        }

        bool built = false;
        string? path;
        try
        {
            path = FaceIconCache.Ensure(outDir, folder, () => GameFolderSource.Open(folder), line =>
            {
                built |= line.StartsWith("face icon: built", StringComparison.Ordinal);
                output.WriteLine(line);
            });
        }
        catch (Exception e) when (e is GameLayoutException or EbootFormatException)
        {
            error.WriteLine($"face-icon: {folder} cannot be used: {e.Message}");
            return ExitCode.Failed;
        }

        if (path is null)
        {
            error.WriteLine($"face-icon: the Arcueid icon could not be built from {folder}");
            return ExitCode.Failed;
        }

        output.WriteLine($"face-icon: {path}{(built ? string.Empty : " (current)")}");
        return ExitCode.Ok;
    }
}
