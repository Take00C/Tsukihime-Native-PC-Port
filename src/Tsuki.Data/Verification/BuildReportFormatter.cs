using System.Globalization;
using System.Text;

namespace Tsuki.Data.Verification;

/// <summary>Turns a <see cref="BuildReport"/> into the text <c>Tsuki.Cli verify</c> prints (and a message box could show).</summary>
public static class BuildReportFormatter
{
    /// <summary>Formats the report. <paramref name="verbose"/> lists every check; otherwise passing checks are summarised.</summary>
    public static string Format(BuildReport report, bool verbose = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Tsuki build check (RR 3.3)");
        sb.Append("folder: ").AppendLine(report.Source);
        if (report.Info is { } info)
        {
            sb.Append("eboot.bin: ").Append(N(info.EbootSize)).Append(" bytes, SHA-256 ").AppendLine(info.EbootSha256);
        }

        if (report.Identity is { } identity)
        {
            sb.Append("build: ").AppendLine(identity.Describe());
            sb.Append("languages: ").AppendLine(identity.DescribeLanguages());
        }

        if (report.Locations is { } located)
        {
            sb.Append("tables: ").AppendLine(located.Summary());
            if (verbose)
            {
                sb.Append(located.Report());
            }
        }

        sb.AppendLine();

        if (verbose)
        {
            foreach (var c in report.Checks)
            {
                AppendLine(sb, c);
            }
        }
        else
        {
            AppendGroup(sb, report, "eboot.bin", "eboot.");
            foreach (var spec in AnalysedBuild.Triplets)
            {
                AppendGroup(sb, report, $"{spec.Name} ({spec.Format.ToString().ToLowerInvariant()})", "data." + spec.Name + ".",
                    report.Counts.TripletEntries.TryGetValue(spec.Name, out int n) ? $"{N(n)} entries" : "unreadable");
            }

            foreach (var spec in AnalysedBuild.Standalone)
            {
                AppendGroup(sb, report, spec.FileName, "data." + spec.FileName + ".",
                    report.Counts.StandaloneSections.TryGetValue(spec.FileName, out int n) ? $"{N(n)} sections" : "unreadable");
            }

            AppendGroup(sb, report, "scripts", "data.scripts", $"{N(report.Counts.Scripts)} scripts");
            foreach (string lang in AnalysedBuild.TextLanguages)
            {
                AppendGroup(sb, report, "text " + lang, "data.text." + lang,
                    report.Counts.TextLines.TryGetValue(lang, out int n) ? $"{N(n)} lines" : "unreadable");
            }

            AppendGroup(sb, report, "vse names", "data.vse.names", $"{N(report.Counts.VseNames)} names");
            AppendGroup(sb, report, "movies", "data.movies", $"{N(report.Counts.MovieFiles)} files");
            AppendGroup(sb, report, "totals", "data.triplets.total", $"{N(report.Counts.TripletTotal)} entries, {N(report.Counts.StandaloneTotal)} sections");
        }

        sb.AppendLine();
        sb.AppendLine("Counts (FORMATS 2)");
        sb.AppendLine($"  triplet archives   {report.Counts.TripletEntries.Count,6}   entries  {N(report.Counts.TripletTotal),7}   (analysed build: 12 archives, {N(AnalysedBuild.TripletEntryTotal)} entries)");
        sb.AppendLine($"  standalone files   {report.Counts.StandaloneSections.Count,6}   sections {N(report.Counts.StandaloneTotal),7}   (analysed build: 8 files, {N(AnalysedBuild.StandaloneSectionTotal)} sections)");
        sb.AppendLine($"  scripts            {N(report.Counts.Scripts),6}");
        sb.AppendLine("  text lines         " + string.Join(" + ", AnalysedBuild.TextLanguages.Select(l => report.Counts.TextLines.TryGetValue(l, out int n) ? N(n) : "?")) + $"   (analysed build: 5 x {N(AnalysedBuild.TextLines)})");
        sb.AppendLine($"  voices             {N(report.Counts.Voices),6}");
        sb.AppendLine($"  movies             {N(report.Counts.MovieFiles),6}");
        sb.AppendLine();

        if (report.Accepted)
        {
            sb.AppendLine("RESULT: ACCEPTED - this is the verified build; every eboot address and archive count of the documents applies.");
        }
        else if (report.Usable)
        {
            sb.AppendLine($"RESULT: USABLE - not the verified build ({report.Identity?.Describe() ?? "identity unknown"}); the tables were located by content and the data is complete. Notes:");
            foreach (var n in report.Notes.Where(n => n.Message.Length > 0))
            {
                sb.Append("  - ").AppendLine(n.Message);
            }
        }
        else
        {
            sb.AppendLine($"RESULT: REJECTED ({report.PrimaryFailure}) - {report.Failures.Count()} check(s) failed:");
            foreach (var f in report.Failures)
            {
                sb.Append("  - ").AppendLine(f.Message);
            }
        }

        return sb.ToString();
    }

    private static void AppendLine(StringBuilder sb, CheckResult c)
    {
        sb.Append(c.Status switch { CheckStatus.Pass => "  ok    ", CheckStatus.Note => "  note  ", _ => "  FAIL  " }).Append(c.Title).Append(": ").Append(c.Actual);
        if (!c.Passed)
        {
            sb.Append("   (expected ").Append(c.Expected).Append(')');
        }

        sb.AppendLine();
    }

    private static void AppendGroup(StringBuilder sb, BuildReport report, string title, string idPrefix, string? detail = null)
    {
        var checks = report.Checks.Where(c => c.Id.StartsWith(idPrefix, StringComparison.Ordinal)).ToList();
        if (checks.Count == 0)
        {
            sb.AppendLine($"  skip  {title}");
            return;
        }

        bool ok = checks.All(c => c.Status == CheckStatus.Pass);
        bool failed = checks.Any(c => !c.Passed && c.Failure is not (BuildFailure.EbootSizeMismatch or BuildFailure.EbootHashMismatch));
        sb.Append(ok ? "  ok    " : failed ? "  FAIL  " : "  note  ").Append(title);
        if (detail is not null)
        {
            sb.Append(": ").Append(detail);
        }

        sb.AppendLine();
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
