// Is this folder the game? (package K17). The launcher never changes the folder: it asks the build check of Tsuki.Data (eboot.bin by size and SHA-256, the archives, the counts),
// reads sce_sys/param.sfo for the version, and adds up the file sizes. Package K25: the path may be any layout GameLayoutResolver understands (the game, its parent, the
// base game with the update beside it), the build is identified (verified / other version of a known release / unknown) and a .pkg or an update alone gets its own state.
using Tsuki.Data;
using Tsuki.Data.IO;
using Tsuki.Data.Verification;
using Tsuki.Engine.Contracts;

namespace Tsuki.Launcher.Core;

/// <summary>How far a folder is from being the game.</summary>
public enum FolderState
{
    /// <summary>No folder has been chosen.</summary>
    NotSet,

    /// <summary>The path does not exist.</summary>
    Missing,

    /// <summary>The folder has no eboot.bin.</summary>
    NotTheGame,

    /// <summary>It looks like the game, but a file is missing, or it is a build whose tables could not be found.</summary>
    Incomplete,

    /// <summary>The game is complete and can be played: the verified build, or another build whose tables were all located (see <see cref="GameFolderInfo.Build"/>).</summary>
    Ready,

    /// <summary>A .pkg file: the game must be extracted first (package K25).</summary>
    PkgFile,

    /// <summary>The update folder alone, without the base game beside it (package K25).</summary>
    UpdateOnly,

    /// <summary>Several game folders inside the chosen one (package K25).</summary>
    Several,
}

/// <summary>What the launcher knows about a game folder.</summary>
/// <param name="Path">The folder as given; null when none.</param>
/// <param name="State">How far it is from being the game.</param>
/// <param name="TitleId">The title id from param.sfo (CUSA47999).</param>
/// <param name="AppVersion">The game version: <see cref="GameFolderCheck.AnalysedVersion"/> when the build check accepted the folder (it identifies the build by the eboot hash), else param.sfo's APP_VER.</param>
/// <param name="Title">The title from param.sfo.</param>
/// <param name="EbootBytes">Size of eboot.bin, 0 when missing.</param>
/// <param name="TotalBytes">Size of every file in the folder (0 when not measured).</param>
/// <param name="ChecksPassed">Build checks that passed.</param>
/// <param name="ChecksTotal">Build checks that ran.</param>
/// <param name="Problems">The messages of the failed checks.</param>
/// <param name="Archives">The archives and data files the check found.</param>
/// <param name="Movies">The movie files the check found.</param>
/// <param name="SfoVersion">APP_VER of param.sfo (01.00 in the base package).</param>
/// <param name="Layout">How the dump is laid out (package K25): the base folder, the update folder beside it, or why the path is not a game.</param>
/// <param name="Build">Verified build, other version of a known release, or unknown build (package K25).</param>
/// <param name="BuildName">The identity in words: "known build: CUSA47999 01.01 (...)" or "unknown build: CUSAxxxxx 01.00, eboot ..." (package K25).</param>
/// <param name="TablesNote">How the eboot's tables were found ("71 tables located by content in 0.4 s"), or null for the verified build (package K25).</param>
/// <param name="Languages">The selectable languages the data carries (package K25).</param>
public sealed record GameFolderInfo(
    string? Path,
    FolderState State,
    string? TitleId = null,
    string? AppVersion = null,
    string? Title = null,
    long EbootBytes = 0,
    long TotalBytes = 0,
    int ChecksPassed = 0,
    int ChecksTotal = 0,
    IReadOnlyList<string>? Problems = null,
    int Archives = 0,
    int Movies = 0,
    string? SfoVersion = null,
    GameLayout? Layout = null,
    BuildMatch Build = BuildMatch.Verified,
    string? BuildName = null,
    string? TablesNote = null,
    IReadOnlyList<Language>? Languages = null)
{
    /// <summary>True when the game can be started from this folder.</summary>
    public bool IsReady => State == FolderState.Ready;

    /// <summary>True when the folder holds the verified build (the one the port was played on).</summary>
    public bool IsVerifiedBuild => IsReady && Build == BuildMatch.Verified && (BuildName is null || BuildName.StartsWith("known build: " + KnownBuilds.Verified.Name, StringComparison.Ordinal));

    /// <summary>The failed checks' messages (never null).</summary>
    public IReadOnlyList<string> ProblemList => Problems ?? [];

    /// <summary>The selectable languages (every one of the four when unknown).</summary>
    public IReadOnlyList<Language> LanguageList => Languages ?? [Language.Ja, Language.En, Language.Zc, Language.Zt];

    /// <summary>The update folder beside the game whose files take precedence, or null.</summary>
    public string? PatchFolder => Layout?.PatchFolder;

    /// <summary>An update folder beside the game that is not needed (the game folder already holds an update as new), or null.</summary>
    public string? IgnoredPatchFolder => Layout?.IgnoredPatchFolder;

    /// <summary>"verified build", "other version of a known release (CUSA47999 01.00): tables found by content" or "unknown build (CUSAxxxxx 01.00): tables found by content".</summary>
    public string BuildText => State != FolderState.Ready ? string.Empty : IsVerifiedBuild ? "verified build" : Build switch
    {
        BuildMatch.Verified => BuildName ?? "known build",      // another known eboot: its tables are byte-identical to the verified one
        BuildMatch.KnownRelease => $"other version of a known release ({TitleId} {AppVersion}): tables found by content",
        _ => $"unknown build ({TitleId ?? "no title id"} {AppVersion ?? "?"}): tables found by content",
    };

    /// <summary>A one-line reason for a folder that is not ready.</summary>
    public string Summary => State switch
    {
        FolderState.NotSet => "No game folder chosen yet.",
        FolderState.Missing => "This folder does not exist.",
        FolderState.NotTheGame => "No eboot.bin in this folder. Choose the CUSA47999 folder (it holds eboot.bin and image\\).",
        FolderState.PkgFile => "This is a PS4 package (.pkg). Extract it first (shadPS4, or a PKG extractor), then choose the extracted CUSA47999 folder.",
        FolderState.UpdateOnly => "This folder holds only the game's update. The base game folder (with image\\allpac0.mrg and the other archives) must be beside it.",
        FolderState.Several => $"Several game folders are inside this one{(Layout is { CandidateList.Count: > 0 } l ? " (" + string.Join(", ", l.CandidateList.Select(System.IO.Path.GetFileName)) + ")" : string.Empty)}. Choose one of them.",
        FolderState.Incomplete => ProblemList.Count > 0 ? ProblemList[0] : "The game files are incomplete.",
        _ => "Ready.",
    };
}

/// <summary>Inspects a game folder, read-only.</summary>
public static class GameFolderCheck
{
    /// <summary>
    /// The version of the build the port was made for ("app 01.01" in the documents). The folder's param.sfo says 01.00 (the base package; docs/RUNTIME_REQUIREMENTS.md 3.3 item 4),
    /// so the version shown for an accepted folder is this one: the build is identified by the eboot hash, not by a version string.
    /// </summary>
    public const string AnalysedVersion = "01.01";

    /// <summary>The info for "no folder".</summary>
    public static GameFolderInfo NotSet { get; } = new(null, FolderState.NotSet);

    /// <summary>
    /// The folder to use for a path the user chose (package K25: <see cref="GameLayoutResolver"/>): the base game folder it leads to (the path itself, the game inside it,
    /// the parent of a folder inside the game, the base beside a chosen update folder). A path that leads to no game comes back unchanged, so that
    /// <see cref="Inspect"/> can say why.
    /// </summary>
    public static string Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        GameLayout layout = GameLayoutResolver.Resolve(path);
        return layout.IsGame && layout.PrimaryFolder is { } primary ? primary : path.Trim().Trim('"');
    }

    /// <summary>Inspects <paramref name="folder"/>. Never throws for an unreadable folder: the result says what is wrong.</summary>
    /// <param name="folder">The folder to inspect, or null / empty for none.</param>
    /// <param name="measureSize">Add up the file sizes (a few tens of milliseconds).</param>
    public static GameFolderInfo Inspect(string? folder, bool measureSize = true)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return NotSet;
        }

        GameLayout layout = GameLayoutResolver.Resolve(folder);
        string path = layout.PrimaryFolder ?? layout.Chosen ?? folder.Trim().Trim('"');
        switch (layout.Kind)
        {
            case GameLayoutKind.NotSet:
                return NotSet;
            case GameLayoutKind.Missing:
                return new GameFolderInfo(path, FolderState.Missing, Layout: layout);
            case GameLayoutKind.PkgFile:
                return new GameFolderInfo(path, FolderState.PkgFile, Layout: layout);
            case GameLayoutKind.UpdateOnly:
                return new GameFolderInfo(path, FolderState.UpdateOnly, Layout: layout);
            case GameLayoutKind.Several:
                return new GameFolderInfo(path, FolderState.Several, Layout: layout);
            case GameLayoutKind.NoGame:
                return new GameFolderInfo(path, FolderState.NotTheGame, Layout: layout);
        }

        string eboot = System.IO.Path.Combine(path, "eboot.bin");
        long ebootBytes = SafeLength(eboot);
        long total = measureSize ? FolderSize(path) + (layout.PatchFolder is { } pf ? FolderSize(pf) : 0) : 0;
        try
        {
            BuildReport report = BuildCheck.Run(layout);
            BuildCounts counts = report.Counts;
            BuildIdentity? identity = report.Identity;
            string? sfoVersion = identity?.AppVersion;
            bool verified = report.Accepted;
            List<string> problems = report.Failures.Select(f => f.Message).Where(m => m.Length > 0).Take(8).ToList();
            return new GameFolderInfo(
                path,
                report.Usable ? FolderState.Ready : FolderState.Incomplete,
                identity?.TitleId,
                verified ? AnalysedVersion : sfoVersion,
                identity?.Title,
                ebootBytes,
                total,
                report.Checks.Count(c => c.Passed),
                report.Checks.Count,
                problems,
                counts.TripletEntries.Count + counts.StandaloneSections.Count,
                counts.MovieFiles,
                sfoVersion,
                layout,
                identity?.Match ?? BuildMatch.Unknown,
                identity?.Describe(),
                report.Locations?.Summary(),
                identity?.AvailableLanguages);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            ParamSfo? sfo = ParamSfo.TryRead(System.IO.Path.Combine(layout.PatchFolder ?? path, "sce_sys", "param.sfo"));
            return new GameFolderInfo(path, FolderState.Incomplete, sfo?["TITLE_ID"] ?? BuildIdentity.TitleIdFromContent(sfo?["CONTENT_ID"]), sfo?["APP_VER"] ?? sfo?["VERSION"], sfo?["TITLE"], ebootBytes, total,
                Problems: [$"The folder could not be read: {e.Message}"], SfoVersion: sfo?["APP_VER"], Layout: layout);
        }
    }

    /// <summary>The size of every file under <paramref name="path"/> (unreadable entries count as 0).</summary>
    public static long FolderSize(string path)
    {
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                total += SafeLength(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the part that could be read is the answer
        }

        return total;
    }

    private static long SafeLength(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Formats a byte count: <c>46.3 GB</c>, <c>12.7 MB</c>.</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GB"),
        >= 1L << 20 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.0} MB"),
        >= 1L << 10 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 10):0.0} KB"),
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes} B"),
    };
}
