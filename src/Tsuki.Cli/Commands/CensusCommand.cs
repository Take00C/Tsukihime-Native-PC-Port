using System.Globalization;
using Tsuki.Data;
using Tsuki.Data.Census;
using Tsuki.Media.Audio;

namespace Tsuki.Cli.Commands;

/// <summary>
/// <c>Tsuki.Cli census [--game DIR] [--threads N] [--no-targets] [--no-glyphs] [--sample N] [--refs FILE] [-v]</c>: census T0 (PACE row 10, ROADMAP M1
/// acceptance 5 and 7). Resolves every reference the 549 scripts make (pictures, voices, sound effects, music, movies, scene jumps and labels, text
/// lines, motions, wipe masks) through <see cref="GameFolderSource"/>, reads the targets, and checks a glyph for every character of every referenced
/// text line in the four languages. Everything must resolve except <see cref="CensusAllowList"/>. Reads the game folder only; exit code 1 when anything
/// outside the allow-list fails. <c>--refs FILE</c> writes one line per reference in the format of the Python oracle
/// (<c>tests/Tsuki.Tests.Data/Census/census_oracle.py refs</c>) for a diff.
/// </summary>
public sealed class CensusCommand : ICliCommand
{
    /// <inheritdoc />
    public string Name => "census";

    /// <inheritdoc />
    public string Summary => "resolve every reference of the 549 scripts (pictures, audio, movies, text, fonts); options: --game DIR, --threads N, --no-targets, --no-glyphs, --sample N, --refs FILE, -v";

    /// <inheritdoc />
    public int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? game = null;
        string? refs = null;
        bool targets = true;
        bool glyphs = true;
        bool verbose = false;
        int threads = Environment.ProcessorCount;
        int sample = 48;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--game" when i + 1 < args.Length:
                    game = args[++i];
                    break;
                case "--refs" when i + 1 < args.Length:
                    refs = args[++i];
                    break;
                case "--threads" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int t) && t > 0:
                    threads = t;
                    i++;
                    break;
                case "--sample" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int n):
                    sample = n;
                    i++;
                    break;
                case "--no-targets":
                    targets = false;
                    break;
                case "--no-glyphs":
                    glyphs = false;
                    break;
                case "-v" or "--verbose":
                    verbose = true;
                    break;
                default:
                    error.WriteLine($"census: unexpected argument '{args[i]}'");
                    return ExitCode.Usage;
            }
        }

        string? folder = GameFolderLocator.Resolve(game);
        if (folder is null || !File.Exists(Path.Combine(folder, "eboot.bin")))
        {
            error.WriteLine("census: no game folder; pass --game <dir> or set TSUKI_GAME_DIR");
            return ExitCode.Usage;
        }

        using GameFolderSource source = GameFolderSource.Open(folder, new MediaAudioDecoder(), decodeParallelism: Math.Max(1, threads / 4));
        if (!source.Build.IsAnalysedBuild)
        {
            error.WriteLine($"census: {folder} is not the analysed build (eboot.bin SHA-256 {source.Build.EbootSha256}); run `verify` first");
            return ExitCode.Failed;
        }

        var options = new CensusOptions
        {
            Parallelism = threads,
            CheckTargets = targets,
            CheckGlyphs = glyphs,
            DecodeAudioSample = sample,
            DecodePictureSample = sample,
        };
        CensusReport report = ReferenceCensus.Run(source, options);
        Print(report, output, verbose);
        if (refs is not null)
        {
            File.WriteAllLines(refs, report.References.Select(r => r.Line));
            output.WriteLine($"wrote {report.References.Count} reference lines to {refs}");
        }

        return report.IsClean ? ExitCode.Ok : ExitCode.Failed;
    }

    private static void Print(CensusReport report, TextWriter w, bool verbose)
    {
        w.WriteLine($"census T0: {report.Scripts} scripts, {report.Statements:N0} statements, {report.References.Count:N0} references, {report.Elapsed.TotalSeconds:F1} s");
        w.WriteLine($"{"kind",-7}{"references",12}{"distinct",10}{"resolved",10}{"allowed",9}{"failed",8}");
        foreach (CensusKindTotals t in report.Totals)
        {
            w.WriteLine($"{t.Kind.ToString().ToLowerInvariant(),-7}{t.References,12:N0}{t.Distinct,10:N0}{t.Resolved,10:N0}{t.Allowed,9}{t.Unexpected,8}");
        }

        if (report.Checks.Count > 0)
        {
            w.WriteLine("targets read through the source:");
            foreach (CensusCheck c in report.Checks)
            {
                w.WriteLine($"  {c.Name}: {c.Checked:N0} checked, {c.Failed} failed");
            }
        }

        if (report.Glyphs.Count > 0)
        {
            w.WriteLine("glyphs of the referenced text lines:");
            foreach (GlyphCensusLanguage g in report.Glyphs)
            {
                w.WriteLine(
                    $"  {g.Language}: {g.Lines:N0} lines, {g.DistinctCharacters:N0} distinct characters ({g.Occurrences:N0} occurrences), {g.Missing.Count} without a glyph, {g.PagesUsed} pages used, {g.OutOfPage} outside their page");
            }
        }

        var allowed = report.Findings.Where(f => f.Allowed).ToList();
        w.WriteLine($"allow-listed: {allowed.Count}");
        foreach (CensusFinding f in allowed.Where(f => verbose || f.Check != "motion"))
        {
            w.WriteLine($"  {f.Check}: {f.Detail.Replace('\t', ' ')} ({f.AllowedBecause})");
        }

        if (!verbose && allowed.Any(f => f.Check == "motion"))
        {
            w.WriteLine($"  motion: {allowed.Count(f => f.Check == "motion")} (id 0 and one id past the table; -v lists them)");
        }

        var failures = report.Failures.ToList();
        w.WriteLine($"failures: {failures.Count}");
        foreach (CensusFinding f in failures.Take(verbose ? int.MaxValue : 40))
        {
            w.WriteLine($"  {f.Check}: {f.Detail.Replace('\t', ' ')}");
        }

        w.WriteLine(failures.Count == 0 ? "census T0: every reference resolves except the allow-list" : "census T0: FAILED");
    }
}
