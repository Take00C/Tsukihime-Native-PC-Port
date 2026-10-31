using Tsuki.Engine.Contracts;

namespace Tsuki.Data.Verification;

/// <summary>Outcome of one check.</summary>
public enum CheckStatus
{
    /// <summary>The check passed.</summary>
    Pass,

    /// <summary>The check failed; the copy is not the analysed build.</summary>
    Fail,

    /// <summary>Not the verified build's value, but nothing is wrong with it (package K25: another build's eboot or data counts); the copy stays usable.</summary>
    Note,
}

/// <summary>Why a check failed. Every kind has its own message, so a wrong eboot differs from a missing archive (ROADMAP M0 acceptance 2).</summary>
public enum BuildFailure
{
    /// <summary>The check passed.</summary>
    None,

    /// <summary>eboot.bin is not in the folder.</summary>
    EbootMissing,

    /// <summary>eboot.bin has another size than the analysed build.</summary>
    EbootSizeMismatch,

    /// <summary>eboot.bin has another SHA-256 than the analysed build (RR 3.3 item 1).</summary>
    EbootHashMismatch,

    /// <summary>A structural probe of RR 3.3 item 2 failed.</summary>
    EbootProbeFailed,

    /// <summary>A data file the runtime needs is missing (RR 3.1).</summary>
    FileMissing,

    /// <summary>A data file is there but breaks its documented layout.</summary>
    LayoutInvalid,

    /// <summary>An entry, section, line or file count differs from FORMATS section 2 (RR 3.3 item 3).</summary>
    CountMismatch,
}

/// <summary>One line of a <see cref="BuildReport"/>.</summary>
/// <param name="Id">Stable identifier such as <c>eboot.sha256</c> or <c>data.allpac0.count</c>.</param>
/// <param name="Title">What was checked.</param>
/// <param name="Status">Pass or fail.</param>
/// <param name="Failure">The failure kind, <see cref="BuildFailure.None"/> on a pass.</param>
/// <param name="Expected">The documented value.</param>
/// <param name="Actual">The value found.</param>
/// <param name="Message">A sentence for the person who sees the failure; empty on a pass.</param>
public sealed record CheckResult(
    string Id, string Title, CheckStatus Status, BuildFailure Failure, string Expected, string Actual, string Message)
{
    /// <summary>True when the check did not fail (a <see cref="CheckStatus.Note"/> counts as passed).</summary>
    public bool Passed => Status != CheckStatus.Fail;
}

/// <summary>The counts the build check read (FORMATS section 2). A count is 0 when its file could not be read.</summary>
public sealed class BuildCounts
{
    /// <summary>Entries per triplet archive, by archive name.</summary>
    public Dictionary<string, int> TripletEntries { get; } = new(StringComparer.Ordinal);

    /// <summary>Sections per standalone file, by file name.</summary>
    public Dictionary<string, int> StandaloneSections { get; } = new(StringComparer.Ordinal);

    /// <summary>Scripts: names in allscr.mrg section 0.</summary>
    public int Scripts { get; set; }

    /// <summary>Text lines per language of script_text.mrg, in the order ja, en, zc, zt, ko.</summary>
    public Dictionary<string, int> TextLines { get; } = new(StringComparer.Ordinal);

    /// <summary>Names in se_nam.mrg section 2.</summary>
    public int VseNames { get; set; }

    /// <summary>Movie files (<c>*.mp4</c>) in <c>image/movie</c>.</summary>
    public int MovieFiles { get; set; }

    /// <summary>Entries over all triplet archives read.</summary>
    public int TripletTotal => TripletEntries.Values.Sum();

    /// <summary>Sections over all standalone files read.</summary>
    public int StandaloneTotal => StandaloneSections.Values.Sum();

    /// <summary>Entries of voice.hed.</summary>
    public int Voices => TripletEntries.GetValueOrDefault("voice");
}

/// <summary>The result of the build check (RR 3.3, ARCHITECTURE 4.1).</summary>
public sealed class BuildReport
{
    /// <summary>Creates a report.</summary>
    public BuildReport(string source, BuildInfo? info, IReadOnlyList<CheckResult> checks, BuildCounts counts, BuildIdentity? identity = null, Eboot.LocateResult? locations = null)
    {
        Source = source;
        Info = info;
        Checks = checks;
        Counts = counts;
        Identity = identity;
        Locations = locations;
    }

    /// <summary>What the dump is (package K25): null when eboot.bin could not be read.</summary>
    public BuildIdentity? Identity { get; }

    /// <summary>Where the eboot's tables were found by content; null for the verified build or when eboot.bin could not be read (package K25).</summary>
    public Eboot.LocateResult? Locations { get; }

    /// <summary>
    /// True when the game can be played from this copy (package K25): every check passed except, at most, the identity of the eboot (its size and hash are not the
    /// verified build's), and the tables were located by content. <see cref="Accepted"/> stays the strict word: the verified build, complete.
    /// </summary>
    public bool Usable => Info is not null && Failures.All(f => f.Failure is BuildFailure.EbootSizeMismatch or BuildFailure.EbootHashMismatch);

    /// <summary>The folder, or a description of the view that was checked.</summary>
    public string Source { get; }

    /// <summary>Size and SHA-256 of eboot.bin, or null when it could not be read.</summary>
    public BuildInfo? Info { get; }

    /// <summary>Every check that ran, in order.</summary>
    public IReadOnlyList<CheckResult> Checks { get; }

    /// <summary>The counts that were read.</summary>
    public BuildCounts Counts { get; }

    /// <summary>True when every check passed outright: the folder is the verified build, complete, and every address in the documents applies.</summary>
    public bool Accepted => Checks.All(c => c.Status == CheckStatus.Pass);

    /// <summary>The checks that are notes (another build's values).</summary>
    public IEnumerable<CheckResult> Notes => Checks.Where(c => c.Status == CheckStatus.Note);

    /// <summary>The checks that failed.</summary>
    public IEnumerable<CheckResult> Failures => Checks.Where(c => !c.Passed);

    /// <summary>The first failure kind in the order eboot, files, layout, counts; <see cref="BuildFailure.None"/> when accepted.</summary>
    public BuildFailure PrimaryFailure => Failures.Select(f => f.Failure).DefaultIfEmpty(BuildFailure.None).Min();
}
