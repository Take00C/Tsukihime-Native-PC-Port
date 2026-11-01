// How a player's dump of the game is laid out on disk (package K25): the extracted app folder itself, a parent folder with the game inside, the base game and the
// update in two folders (CUSA47999 + CUSA47999-patch, CUSA47999-app + CUSA47999-patch, CUSA47999 + CUSA47999-UPDATE), a .pkg that was never extracted. The
// resolver only looks at directory listings and small files; GameFolderSource then reads through one view where the update's files override the base's.
using System.Globalization;

namespace Tsuki.Data.IO;

/// <summary>What a chosen path turned out to be.</summary>
public enum GameLayoutKind
{
    /// <summary>Nothing was chosen.</summary>
    NotSet,

    /// <summary>The path does not exist.</summary>
    Missing,

    /// <summary>A <c>.pkg</c> file: the game must be extracted first.</summary>
    PkgFile,

    /// <summary>A folder (or a folder with subfolders) that holds no game: no <c>eboot.bin</c> with an <c>image\</c> folder anywhere one or two levels down.</summary>
    NoGame,

    /// <summary>The update (patch) folder alone, without the base game beside it.</summary>
    UpdateOnly,

    /// <summary>Several different games or title ids were found inside; the player must choose one.</summary>
    Several,

    /// <summary>One game folder: <c>eboot.bin</c> and <c>image\</c>.</summary>
    Game,

    /// <summary>A base game folder with an update folder beside it; the update's files win.</summary>
    GameWithUpdate,
}

/// <summary>A resolved game layout.</summary>
/// <param name="Chosen">The path as the player gave it (trimmed), or null.</param>
/// <param name="Kind">What it is.</param>
/// <param name="BaseFolder">The folder that holds the full game (<c>image\</c> with the archives), when there is one.</param>
/// <param name="PatchFolder">The update folder whose files override the base's, when there is one.</param>
/// <param name="Candidates">For <see cref="GameLayoutKind.Several"/>: the game folders that were found.</param>
/// <param name="IgnoredPatchFolder">An update folder beside the game that is not layered, because the game folder already holds an update as new as it (a known eboot).</param>
public sealed record GameLayout(string? Chosen, GameLayoutKind Kind, string? BaseFolder = null, string? PatchFolder = null, IReadOnlyList<string>? Candidates = null, string? IgnoredPatchFolder = null)
{
    /// <summary>True when the game can be opened from this layout.</summary>
    public bool IsGame => Kind is GameLayoutKind.Game or GameLayoutKind.GameWithUpdate;

    /// <summary>The folder the game is remembered and started by: the base folder.</summary>
    public string? PrimaryFolder => BaseFolder;

    /// <summary>The game folders that were found (empty when none).</summary>
    public IReadOnlyList<string> CandidateList => Candidates ?? [];

    /// <summary>The read-only view of the files: the update over the base, or the base alone.</summary>
    /// <exception cref="InvalidOperationException">The layout holds no game.</exception>
    public IGameFiles OpenFiles()
    {
        if (BaseFolder is null)
        {
            throw new InvalidOperationException($"the layout {Kind} holds no game folder");
        }

        var baseFiles = new DirectoryGameFiles(BaseFolder);
        return PatchFolder is null ? baseFiles : new LayeredGameFiles(new DirectoryGameFiles(PatchFolder), baseFiles);
    }

    /// <summary>One line for the log: "D:\games\CUSA47999 + update folder D:\games\CUSA47999-patch".</summary>
    public string Describe() => Kind switch
    {
        GameLayoutKind.NotSet => "no game folder",
        GameLayoutKind.Missing => $"{Chosen}: does not exist",
        GameLayoutKind.PkgFile => $"{Chosen}: a .pkg file, not extracted",
        GameLayoutKind.NoGame => $"{Chosen}: no game inside",
        GameLayoutKind.UpdateOnly => $"{Chosen}: the update folder alone, no base game beside it",
        GameLayoutKind.Several => $"{Chosen}: {CandidateList.Count} game folders inside",
        GameLayoutKind.Game => IgnoredPatchFolder is null ? BaseFolder! : $"{BaseFolder} (the update folder {IgnoredPatchFolder} beside it is not needed: the game folder already holds an update as new)",
        _ => $"{BaseFolder} + update folder {PatchFolder}",
    };
}

/// <summary>The chosen path holds no game that can be opened: a .pkg, an update alone, a folder without the game, several games (package K25).</summary>
public sealed class GameLayoutException : IOException
{
    /// <summary>Creates the exception for a layout that is not a game.</summary>
    public GameLayoutException(GameLayout layout)
        : base(Compose(layout))
    {
        Layout = layout;
    }

    /// <summary>What the path turned out to be.</summary>
    public GameLayout Layout { get; }

    private static string Compose(GameLayout layout) => layout.Kind switch
    {
        GameLayoutKind.PkgFile => $"{layout.Chosen} is a PS4 package (.pkg). It must be extracted first (shadPS4, a PKG extractor); then choose the extracted {GameLayoutResolver.TitleId} folder.",
        GameLayoutKind.UpdateOnly => $"{layout.Chosen} holds only the game's update. The base game (the folder with image\\allpac0.mrg and the other archives) must be beside it.",
        GameLayoutKind.Several => $"{layout.Chosen} holds several game folders ({string.Join(", ", layout.CandidateList.Select(Path.GetFileName))}); choose one of them.",
        GameLayoutKind.Missing => $"{layout.Chosen} does not exist.",
        GameLayoutKind.NotSet => "no game folder was chosen.",
        _ => $"{layout.Chosen} holds no game: no folder with eboot.bin and image\\ inside it.",
    };
}

/// <summary>Turns the path a player chose into a <see cref="GameLayout"/>. Reads directory listings and <c>sce_sys/param.sfo</c> only; writes nothing.</summary>
public static class GameLayoutResolver
{
    /// <summary>The title id of TSUKIHIME -A piece of blue glass moon- (Asia); the folder name every extractor gives the game.</summary>
    public const string TitleId = "CUSA47999";

    /// <summary>The folder name suffixes extractors give the update: <c>CUSA47999-patch</c> (some PKG extractors), <c>CUSA47999-UPDATE</c> (shadPS4), <c>_patch</c>, <c>-update</c>.</summary>
    public static IReadOnlyList<string> PatchSuffixes { get; } = ["-patch", "_patch", "-update", "_update"];

    /// <summary>The folder name suffixes extractors give the base game beside a patch: <c>CUSA47999-app</c> (PS4 pkg tools).</summary>
    public static IReadOnlyList<string> BaseSuffixes { get; } = ["-app", "_app"];

    /// <summary>The folders inside a game folder a player may pick by mistake; the game is their parent.</summary>
    private static readonly string[] InsideFolders = ["image", "data", "sce_sys", "sce_module"];

    /// <summary>Resolves <paramref name="path"/> (a folder, a file, quotes allowed).</summary>
    public static GameLayout Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new GameLayout(null, GameLayoutKind.NotSet);
        }

        string chosen = path.Trim().Trim('"');
        bool unixRoot = !OperatingSystem.IsWindows() && chosen == "/";
        if (chosen.Length == 0 || (!unixRoot && chosen.All(c => c is '\\' or '/')))
        {
            return new GameLayout(null, GameLayoutKind.NotSet);
        }

        if (chosen.Length == 2 && chosen[1] == ':' && char.IsAsciiLetter(chosen[0]))
        {
            // a bare drive letter means its root, not the process's current directory on that drive (which GetFullPath would give)
            chosen += Path.DirectorySeparatorChar;
        }

        try
        {
            if (!OperatingSystem.IsWindows() && !Directory.Exists(chosen) && !File.Exists(chosen))
            {
                chosen = chosen.TrimEnd('\\');
            }

            // the full path first, then the trailing separator off: a drive root ("D:\") keeps its separator, "D:\games\" becomes "D:\games" (K27: trimming first turned
            // the root into "D:", which GetFullPath resolves to the current directory on that drive)
            chosen = Path.TrimEndingDirectorySeparator(Path.GetFullPath(chosen));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new GameLayout(chosen, GameLayoutKind.Missing);
        }

        if (File.Exists(chosen))
        {
            if (chosen.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
            {
                return new GameLayout(chosen, GameLayoutKind.PkgFile);
            }

            // eboot.bin or any other file inside the game: its folder
            string? dir = Path.GetDirectoryName(chosen);
            return dir is null ? new GameLayout(chosen, GameLayoutKind.NoGame) : Resolve(dir);
        }

        if (!Directory.Exists(chosen))
        {
            return new GameLayout(chosen, GameLayoutKind.Missing);
        }

        // a folder inside the game (image\, data\, sce_sys\): its parent
        string leaf = Path.GetFileName(chosen);
        string? parent = Path.GetDirectoryName(chosen);
        if (parent is not null && InsideFolders.Contains(leaf, StringComparer.OrdinalIgnoreCase) && HasEboot(parent))
        {
            chosen = parent;
            leaf = Path.GetFileName(chosen);
            parent = Path.GetDirectoryName(chosen);
        }

        if (HasEboot(chosen))
        {
            return Classify(chosen, chosen);
        }

        if (OperatingSystem.IsLinux() && chosen == Path.GetPathRoot(chosen))
        {
            return new GameLayout(chosen, GameLayoutKind.NoGame);
        }

        // the folder above the game (one or two levels): every game folder below, patch folders paired with their base
        List<string> inside = [.. Directory.EnumerateDirectories(chosen).Where(HasEboot)];
        if (inside.Count == 0)
        {
            foreach (string sub in Directory.EnumerateDirectories(chosen))
            {
                try
                {
                    inside.AddRange(Directory.EnumerateDirectories(sub).Where(HasEboot));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // an unreadable folder holds no game we can use
                }
            }
        }

        List<string> bases = [.. inside.Where(f => !IsPatchFolder(f))];
        List<string> patches = [.. inside.Where(IsPatchFolder)];
        if (bases.Count == 0 && patches.Count == 1)
        {
            return Classify(chosen, patches[0]);
        }

        if (bases.Count == 1)
        {
            GameLayout one = Classify(chosen, bases[0]);
            return one with { Chosen = chosen };
        }

        if (bases.Count > 1)
        {
            // several: the one named by the title id wins when it is the only one
            List<string> titled = [.. bases.Where(b => Path.GetFileName(b).StartsWith(TitleId, StringComparison.OrdinalIgnoreCase))];
            if (titled.Count == 1)
            {
                return Classify(chosen, titled[0]) with { Chosen = chosen };
            }

            return new GameLayout(chosen, GameLayoutKind.Several, Candidates: bases);
        }

        if (patches.Count > 0)
        {
            return new GameLayout(chosen, GameLayoutKind.UpdateOnly, PatchFolder: patches[0]);
        }

        return new GameLayout(chosen, GameLayoutKind.NoGame);
    }

    /// <summary>A folder with <c>eboot.bin</c> (the base game, a merged dump, or an update).</summary>
    private static bool HasEboot(string folder) => ResolveFile(folder, "eboot.bin") is not null;

    /// <summary>A folder that holds the full game: <c>eboot.bin</c> and the first picture archive.</summary>
    private static bool IsFullGame(string folder) => HasEboot(folder) && ResolveFile(folder, "image/allpac0.hed") is not null;

    private static string? ResolveFile(string folder, string relativePath)
    {
        try
        {
            return new DirectoryGameFiles(folder).ResolveFile(relativePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ParamSfo? ReadParamSfo(string folder) =>
        ResolveFile(folder, "sce_sys/param.sfo") is { } path ? ParamSfo.TryRead(path) : null;

    /// <summary>
    /// True for an update folder: its <c>param.sfo</c> says <c>CATEGORY gp</c> (a patch package; <c>gd</c> is the game), else its name ends in a patch suffix. A folder with
    /// an eboot but no archives and neither mark is an incomplete game, not an update (the build check then names the missing files).
    /// </summary>
    public static bool IsPatchFolder(string folder)
    {
        if (ReadParamSfo(folder) is { } sfo && sfo["CATEGORY"] is { } category)
        {
            return string.Equals(category, "gp", StringComparison.OrdinalIgnoreCase);
        }

        string name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        return PatchSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The folder with eboot.bin: a game (with a patch beside it when there is one), or an update alone (with its base beside it when there is one).</summary>
    private static GameLayout Classify(string chosen, string folder)
    {
        if (IsPatchFolder(folder))
        {
            string? baseBeside = FindBaseBeside(folder);
            return baseBeside is null
                ? new GameLayout(chosen, GameLayoutKind.UpdateOnly, PatchFolder: folder)
                : Pair(chosen, baseBeside, folder);
        }

        if (!IsFullGame(folder))
        {
            // an eboot without archives and not marked as a patch: the folder is incomplete; report it as the game so the build check names the missing files
            return new GameLayout(chosen, GameLayoutKind.Game, folder);
        }

        string? patch = FindPatchBeside(folder);
        return patch is null ? new GameLayout(chosen, GameLayoutKind.Game, folder) : Pair(chosen, folder, patch);
    }

    /// <summary>
    /// The update takes precedence when its metadata is explicitly newer than the base's. Otherwise a known base eboot at least as new as the update keeps a
    /// merged dump's stale sibling update from being layered again.
    /// </summary>
    private static GameLayout Pair(string chosen, string baseFolder, string patchFolder)
    {
        string? baseVersion = BaseIsCurrent(baseFolder, patchFolder);
        return baseVersion is null
            ? new GameLayout(chosen, GameLayoutKind.GameWithUpdate, baseFolder, patchFolder)
            : new GameLayout(chosen, GameLayoutKind.Game, baseFolder, IgnoredPatchFolder: patchFolder);
    }

    /// <summary>The known base eboot's version when the sibling update is unnecessary and metadata does not show a newer update; null otherwise.</summary>
    private static string? BaseIsCurrent(string baseFolder, string patchFolder)
    {
        try
        {
            var files = new DirectoryGameFiles(baseFolder);
            long size = files.Length("eboot.bin");
            if (size > 64 * 1024 * 1024)
            {
                return null;
            }

            string sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(files.ReadAll("eboot.bin")));
            if (Verification.KnownBuilds.Find(size, sha) is not { } known)
            {
                return null;
            }

            return CurrentBaseVersion(baseFolder, patchFolder, known);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? CurrentBaseVersion(string baseFolder, string patchFolder, Verification.KnownBuild known)
    {
        string? baseVersion = ReadParamSfo(baseFolder)?["APP_VER"];
        string? patchVersion = ReadParamSfo(patchFolder)?["APP_VER"];
        if (IsAppVersion(baseVersion) && IsAppVersion(patchVersion) && string.CompareOrdinal(patchVersion, baseVersion) > 0)
        {
            return null;
        }

        return patchVersion is null || string.CompareOrdinal(patchVersion, known.AppVersion) <= 0 ? known.AppVersion : null;
    }

    // Fixed-width NN.NN versions sort numerically. Other metadata keeps the existing fallback.
    private static bool IsAppVersion(string? version) => version is { Length: 5 } && version[2] == '.'
        && char.IsAsciiDigit(version[0]) && char.IsAsciiDigit(version[1])
        && char.IsAsciiDigit(version[3]) && char.IsAsciiDigit(version[4]);

    /// <summary>The update folder beside a base folder: <c>&lt;name&gt;-patch</c>, <c>&lt;name&gt;-UPDATE</c>, ... or the same with the base's <c>-app</c> suffix removed.</summary>
    public static string? FindPatchBeside(string baseFolder)
    {
        string? parent = Path.GetDirectoryName(baseFolder.TrimEnd('\\', '/'));
        if (parent is null)
        {
            return null;
        }

        string name = Path.GetFileName(baseFolder.TrimEnd('\\', '/'));
        var stems = new List<string> { name };
        foreach (string s in BaseSuffixes)
        {
            if (name.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            {
                stems.Add(name[..^s.Length]);
            }
        }

        // the siblings as the disk spells them (the comparison ignores case): first the exact suffixes, then any sibling named after the stem that is a patch
        List<string> siblings = [.. Directory.EnumerateDirectories(parent).Where(s => !string.Equals(s, baseFolder, StringComparison.OrdinalIgnoreCase))];
        foreach (string stem in stems)
        {
            foreach (string suffix in PatchSuffixes)
            {
                string? candidate = siblings.FirstOrDefault(s => string.Equals(Path.GetFileName(s), stem + suffix, StringComparison.OrdinalIgnoreCase));
                if (candidate is not null && HasEboot(candidate) && IsPatchFolder(candidate))
                {
                    return candidate;
                }
            }
        }

        foreach (string sibling in siblings)
        {
            string sib = Path.GetFileName(sibling);
            if (stems.Any(st => sib.StartsWith(st, StringComparison.OrdinalIgnoreCase) && sib.Length > st.Length) && HasEboot(sibling) && IsPatchFolder(sibling))
            {
                return sibling;
            }
        }

        return null;
    }

    /// <summary>The base game beside an update folder: the name without its patch suffix, or that plus <c>-app</c>.</summary>
    public static string? FindBaseBeside(string patchFolder)
    {
        string? parent = Path.GetDirectoryName(patchFolder.TrimEnd('\\', '/'));
        if (parent is null)
        {
            return null;
        }

        string name = Path.GetFileName(patchFolder.TrimEnd('\\', '/'));
        var stems = new List<string>();
        foreach (string s in PatchSuffixes)
        {
            if (name.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            {
                stems.Add(name[..^s.Length]);
            }
        }

        List<string> siblings = [.. Directory.EnumerateDirectories(parent).Where(s => !string.Equals(s, patchFolder, StringComparison.OrdinalIgnoreCase))];
        foreach (string stem in stems)
        {
            foreach (string candidate in new[] { stem }.Concat(BaseSuffixes.Select(s => stem + s)))
            {
                string? dir = siblings.FirstOrDefault(s => string.Equals(Path.GetFileName(s), candidate, StringComparison.OrdinalIgnoreCase));
                if (dir is not null && IsFullGame(dir))
                {
                    return dir;
                }
            }
        }

        // any full game beside it that shares the title id
        string? titleId = ReadParamSfo(patchFolder)?["TITLE_ID"];
        foreach (string sibling in siblings)
        {
            if (IsFullGame(sibling) && !IsPatchFolder(sibling))
            {
                string? sibId = ReadParamSfo(sibling)?["TITLE_ID"];
                if (titleId is null || sibId is null || string.Equals(sibId, titleId, StringComparison.OrdinalIgnoreCase))
                {
                    return sibling;
                }
            }
        }

        return null;
    }

    /// <summary>A short note for a layout that is a game: "update folder CUSA47999-patch beside it; its files take precedence", or why a sibling update was left alone, or null.</summary>
    public static string? Note(GameLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return layout.Kind == GameLayoutKind.GameWithUpdate && layout.PatchFolder is { } p
            ? string.Create(CultureInfo.InvariantCulture, $"update folder {Path.GetFileName(p)} beside it; its files take precedence")
            : layout.IgnoredPatchFolder is { } ignored
                ? string.Create(CultureInfo.InvariantCulture, $"update folder {Path.GetFileName(ignored)} beside it is not needed: the game folder already holds an update as new")
                : null;
    }
}
