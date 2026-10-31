using System.Globalization;

namespace Tsuki.Data.Census;

/// <summary>What a script reference points at (the lower-case name is the first column of the oracle's lines).</summary>
public enum ReferenceKind
{
    /// <summary>A picture id of <c>_STCH</c>, <c>_SCH2</c>, <c>_SPFC</c>, <c>_SPRD</c> or <c>_STCG</c> (name list, image table, NAM search).</summary>
    Image,

    /// <summary>A voice of <c>_VPLY</c> / <c>_VPL2</c>: a <c>voice.hed</c> entry.</summary>
    Voice,

    /// <summary>A sound-effect name of <c>_SEPL</c> / <c>_SEFI</c>: a <c>vse.hed</c> entry through <c>se_nam.mrg</c>.</summary>
    Se,

    /// <summary>A music track of <c>_MPLY</c>: a <c>bgm.hed</c> entry.</summary>
    Bgm,

    /// <summary>A movie of <c>_TOMP</c>: a movie-table record and a file in <c>image/movie</c>.</summary>
    Movie,

    /// <summary>A scene id of a <c>_J...</c> command: a script.</summary>
    Scene,

    /// <summary>A label of a jump (<c>_JUMP</c>, <c>_GOTO</c>, <c>_IF__</c>, <c>_GOTS</c> ...): a record of the target script's label table.</summary>
    Label,

    /// <summary>A <c>$NNNNNN</c> text line id of <c>_ZM</c>, <c>_ZD</c>, <c>_MSAD</c> or <c>_SELR</c>.</summary>
    Text,

    /// <summary>A motion id of <c>_STMA</c> / <c>_SGMA</c>.</summary>
    Motion,

    /// <summary>The wipe mask of <c>_FADS(2, ms, n)</c>: an <c>allpac3</c> entry.</summary>
    Rule,
}

/// <summary>One reference a script makes, with what it resolved to.</summary>
/// <param name="Kind">What it points at.</param>
/// <param name="Script">Script number k.</param>
/// <param name="Offset">Offset of the statement (the segment start).</param>
/// <param name="Command">The command (<c>STCH</c>, <c>ZM</c>, <c>JUMP</c> ...).</param>
/// <param name="Key">What the script wrote, in the form the oracle prints it (the picture key with its variant suffix, the voice name, the SE name ...).</param>
/// <param name="Result">What it resolved to, in the form the oracle prints it (<c>allpac0#12+allpac0#14;m=0</c>, an entry index, a file name ...).</param>
/// <param name="Resolved">True when the reference is complete: nothing is missing.</param>
public readonly record struct CensusReference(
    ReferenceKind Kind, int Script, int Offset, string Command, string Key, string Result, bool Resolved)
{
    /// <summary>The line the Python oracle (<c>census_oracle.py refs</c>) prints for the same reference.</summary>
    public string Line =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Kind.ToString().ToLowerInvariant()}\t{Script}\t{Offset}\t{Command}\t{Key}\t{Result}");
}

/// <summary>A reference or an entry that did not resolve or did not read, and the allow-list entry that excuses it (null: it is a failure).</summary>
/// <param name="Check">The reference kind, or the name of the presence check that failed.</param>
/// <param name="Detail">What failed.</param>
/// <param name="AllowedBecause">The documented reason, or null.</param>
public sealed record CensusFinding(string Check, string Detail, string? AllowedBecause)
{
    /// <summary>True when the allow-list excuses it.</summary>
    public bool Allowed => AllowedBecause is not null;
}

/// <summary>Counts of one reference kind.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="References">Statements-level references.</param>
/// <param name="Distinct">Distinct keys among them.</param>
/// <param name="Resolved">References that resolved completely.</param>
/// <param name="Allowed">References that did not, and are on the allow-list.</param>
/// <param name="Unexpected">References that did not, and are not on the allow-list.</param>
public sealed record CensusKindTotals(ReferenceKind Kind, int References, int Distinct, int Resolved, int Allowed, int Unexpected);

/// <summary>A presence check over the distinct targets (an image header parsed, a voice unwrapped, a movie file found ...).</summary>
/// <param name="Name">What was checked.</param>
/// <param name="Checked">How many distinct things.</param>
/// <param name="Failed">How many failed (allow-listed ones included).</param>
public sealed record CensusCheck(string Name, int Checked, int Failed);

/// <summary>The result of the glyph part of the census for one language.</summary>
/// <param name="Language">The language code.</param>
/// <param name="Lines">Distinct text lines read: the referenced ids and the lines they reference with <c>$</c>.</param>
/// <param name="DistinctCharacters">Distinct characters in them.</param>
/// <param name="Occurrences">Characters in them, with repeats.</param>
/// <param name="Missing">Characters with no glyph lookup entry, as their packed UTF-8 key (hex) and the number of occurrences.</param>
/// <param name="PagesUsed">Distinct font pages the glyphs lie on.</param>
/// <param name="OutOfPage">Distinct glyph rectangles that do not lie inside their page.</param>
public sealed record GlyphCensusLanguage(
    string Language, int Lines, int DistinctCharacters, long Occurrences, IReadOnlyList<(string Key, int Count)> Missing, int PagesUsed, int OutOfPage);

/// <summary>Everything one census run found.</summary>
public sealed class CensusReport
{
    /// <summary>Creates a report.</summary>
    public CensusReport(
        int scripts, int statements, IReadOnlyList<CensusReference> references, IReadOnlyList<CensusKindTotals> totals,
        IReadOnlyList<CensusFinding> findings, IReadOnlyList<CensusCheck> checks, IReadOnlyList<GlyphCensusLanguage> glyphs, TimeSpan elapsed)
    {
        Scripts = scripts;
        Statements = statements;
        References = references;
        Totals = totals;
        Findings = findings;
        Checks = checks;
        Glyphs = glyphs;
        Elapsed = elapsed;
    }

    /// <summary>Scripts read (549).</summary>
    public int Scripts { get; }

    /// <summary>Statements walked.</summary>
    public int Statements { get; }

    /// <summary>Every reference, in script and statement order.</summary>
    public IReadOnlyList<CensusReference> References { get; }

    /// <summary>Counts per kind.</summary>
    public IReadOnlyList<CensusKindTotals> Totals { get; }

    /// <summary>Everything that did not resolve or did not read: the allow-listed ones and the failures.</summary>
    public IReadOnlyList<CensusFinding> Findings { get; }

    /// <summary>The presence checks.</summary>
    public IReadOnlyList<CensusCheck> Checks { get; }

    /// <summary>The glyph census of the four selectable languages (empty when it was not run).</summary>
    public IReadOnlyList<GlyphCensusLanguage> Glyphs { get; }

    /// <summary>Time the run took.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>The findings the allow-list does not excuse.</summary>
    public IEnumerable<CensusFinding> Failures => Findings.Where(f => !f.Allowed);

    /// <summary>True when every reference resolved except the allow-listed ones.</summary>
    public bool IsClean => !Failures.Any();
}

/// <summary>Options of <see cref="ReferenceCensus.Run"/>.</summary>
public sealed class CensusOptions
{
    /// <summary>Worker threads (default: all cores).</summary>
    public int Parallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>Also read every referenced target through the source (image headers, audio payloads, movie files, text lines).</summary>
    public bool CheckTargets { get; init; } = true;

    /// <summary>Also resolve the glyph of every character of the referenced text lines in the four languages.</summary>
    public bool CheckGlyphs { get; init; } = true;

    /// <summary>How many referenced audio entries to decode completely with the injected decoder (spread evenly; 0 for none).</summary>
    public int DecodeAudioSample { get; init; } = 48;

    /// <summary>How many referenced pictures to decode to pixels (spread evenly; 0 for none).</summary>
    public int DecodePictureSample { get; init; } = 48;
}
