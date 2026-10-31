using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Tsuki.Data.Containers;
using Tsuki.Data.IO;
using Tsuki.Engine.Contracts;

namespace Tsuki.Data.Verification;

/// <summary>
/// Confirms that a CUSA47999 folder is the analysed build before any table is read (RR 3.3,
/// ARCHITECTURE 4.1): eboot.bin by exact size and SHA-256, two structural probes, and the counts of
/// FORMATS section 2. Only small ranges of the archives are read (the .hed, the .nam, the lengths of the
/// .mrg files, a few sections of the standalone files), so it takes well under a second.
/// </summary>
public static class BuildCheck
{
    private const int MaxHashedEbootBytes = 64 * 1024 * 1024;
    private const string EbootPath = "eboot.bin";

    /// <summary>Checks the folder at <paramref name="gameDirectory"/>.</summary>
    public static BuildReport Run(string gameDirectory) => Run(new DirectoryGameFiles(gameDirectory));

    /// <summary>Checks a resolved layout (the update over the base; package K25).</summary>
    public static BuildReport Run(GameLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Run(layout.OpenFiles());
    }

    /// <summary>Checks any view of a game folder.</summary>
    public static BuildReport Run(IGameFiles files)
    {
        var run = new Runner(files);
        run.Guard("eboot.bin", run.CheckEboot);
        foreach (var spec in AnalysedBuild.Triplets)
        {
            run.Guard(spec.Name, () => run.CheckTriplet(spec));
        }

        foreach (var spec in AnalysedBuild.Standalone)
        {
            run.Guard(spec.FileName, () => run.CheckStandalone(spec));
        }

        run.Guard("allscr.mrg names", run.CheckScripts);
        run.Guard("script_text.mrg", run.CheckText);
        run.Guard("se_nam.mrg", run.CheckVseNames);
        run.Guard("image\\movie", run.CheckMovies);
        run.CheckTotals();
        return new BuildReport(files.Description, run.Info, run.Results, run.Counts, run.Identity, run.Locations);
    }

    private sealed class Runner(IGameFiles files)
    {
        public List<CheckResult> Results { get; } = [];

        public BuildCounts Counts { get; } = new();

        public BuildInfo? Info { get; private set; }

        public BuildIdentity? Identity { get; private set; }

        public Eboot.LocateResult? Locations { get; private set; }

        /// <summary>True while the identity says the eboot is not a known release: count mismatches become notes, not failures (package K25).</summary>
        private bool OtherRelease => Identity is { Match: BuildMatch.Unknown };

        /// <summary>Runs one check; a file that exists but cannot be read becomes a failed check, not a crash.</summary>
        public void Guard(string what, Action check)
        {
            try
            {
                check();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Fail("io." + what, what + " readable", BuildFailure.FileMissing, "readable", e.Message,
                    $"{what} could not be read: {e.Message}");
            }
        }

        private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        private void Pass(string id, string title, string expected, string actual) =>
            Results.Add(new CheckResult(id, title, CheckStatus.Pass, BuildFailure.None, expected, actual, string.Empty));

        private void Fail(string id, string title, BuildFailure kind, string expected, string actual, string message) =>
            Results.Add(new CheckResult(id, title, CheckStatus.Fail, kind, expected, actual, message));

        private void Note(string id, string title, string expected, string actual, string message) =>
            Results.Add(new CheckResult(id, title, CheckStatus.Note, BuildFailure.None, expected, actual, message));

        private void CountCheck(string id, string title, long expected, long actual, string what)
        {
            if (expected == actual)
            {
                Pass(id, title, N(expected), N(actual));
            }
            else if (OtherRelease)
            {
                Note(id, title, N(expected), N(actual), $"{what}: found {N(actual)}, the verified build has {N(expected)} (another data release).");
            }
            else
            {
                Fail(id, title, BuildFailure.CountMismatch, N(expected), N(actual),
                    $"{what}: found {N(actual)}, the analysed build has {N(expected)}. This is a different data release.");
            }
        }

        // ---- eboot.bin (RR 3.3 items 1 and 2) --------------------------------------------------

        public void CheckEboot()
        {
            if (!files.Exists(EbootPath))
            {
                Fail("eboot.present", "eboot.bin present", BuildFailure.EbootMissing, "present", "missing",
                    $"eboot.bin was not found in {files.Description}. Point the program at the CUSA47999 folder that holds eboot.bin and image\\.");
                return;
            }

            Pass("eboot.present", "eboot.bin present", "present", "present");
            long length = files.Length(EbootPath);
            bool sizeOk = length == BuildInfo.AnalysedEbootSize;
            if (sizeOk)
            {
                Pass("eboot.size", "eboot.bin size", N(BuildInfo.AnalysedEbootSize), N(length));
            }
            else
            {
                Fail("eboot.size", "eboot.bin size", BuildFailure.EbootSizeMismatch, N(BuildInfo.AnalysedEbootSize), N(length),
                    $"eboot.bin is {N(length)} bytes, the analysed build's is {N(BuildInfo.AnalysedEbootSize)}. " +
                    "This is another build or an incomplete copy; the addresses of the documents do not apply.");
            }

            if (length > MaxHashedEbootBytes)
            {
                Fail("eboot.sha256", "eboot.bin SHA-256", BuildFailure.EbootHashMismatch, BuildInfo.AnalysedEbootSha256,
                    "not computed", "eboot.bin is too large to be the analysed build; its hash was not computed.");
                return;
            }

            byte[] data = files.ReadAll(EbootPath);
            string sha = Convert.ToHexStringLower(SHA256.HashData(data));
            Info = new BuildInfo(files.Description, length, sha);
            Identity = BuildIdentity.Identify(files, data);
            if (string.Equals(sha, BuildInfo.AnalysedEbootSha256, StringComparison.Ordinal))
            {
                Pass("eboot.sha256", "eboot.bin SHA-256", BuildInfo.AnalysedEbootSha256, sha);
            }
            else
            {
                string how = sizeOk
                    ? "Its size matches but its content differs: the file is modified or damaged, or it is another build of the same size."
                    : "Its size differs too, so this is another build.";
                Fail("eboot.sha256", "eboot.bin SHA-256", BuildFailure.EbootHashMismatch, BuildInfo.AnalysedEbootSha256, sha,
                    $"The SHA-256 of eboot.bin is {sha}, not the verified build's. {how} Its tables are looked for by content instead (package K25).");
            }

            // package K25: what the dump is, and whether every table can be found in this eboot
            Results.Add(new CheckResult("eboot.identity", "build identity", Identity.Match == BuildMatch.Verified && Identity.Known == KnownBuilds.Verified ? CheckStatus.Pass : CheckStatus.Note,
                BuildFailure.None, KnownBuilds.Verified.Name, Identity.Describe(), string.Empty));

            // Structural probes: a self-test when the hash matches, diagnostics when it does not.
            bool selfOk = data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual(AnalysedBuild.SelfMagic);
            if (selfOk)
            {
                Pass("eboot.probe.self", "SELF magic 4F 15 3D 1D", "4F153D1D", "4F153D1D");
            }
            else
            {
                string found = Convert.ToHexString(data.AsSpan(0, Math.Min(4, data.Length)));
                Fail("eboot.probe.self", "SELF magic 4F 15 3D 1D", BuildFailure.EbootProbeFailed, "4F153D1D", found,
                    "eboot.bin does not start with the PS4 SELF magic; it is not a PS4 executable.");
            }

            var hunex = AnalysedBuild.HunexString;
            int at = AnalysedBuild.HunexStringOffset;
            bool hunexOk = data.Length >= at + hunex.Length && data.AsSpan(at, hunex.Length).SequenceEqual(hunex);
            if (hunexOk)
            {
                Pass("eboot.probe.hunex", "\"Hunex Consumer Library\" at text VA 0x2DA510", "found", "found");
            }
            else if (data.AsSpan().IndexOf(hunex) >= 0)
            {
                Note("eboot.probe.hunex", "\"Hunex Consumer Library\" at text VA 0x2DA510", "found", "found elsewhere", "The engine's name is in this eboot, at another place than in the verified build.");
            }
            else
            {
                Fail("eboot.probe.hunex", "\"Hunex Consumer Library\" at text VA 0x2DA510", BuildFailure.EbootProbeFailed, "found", "not found",
                    $"The string \"Hunex Consumer Library\" is not in eboot.bin (the verified build has it at file offset 0x{at:X}); this is not the game's engine.");
            }

            if (!selfOk)
            {
                return;
            }

            if (Identity.Match == BuildMatch.Verified && Identity.Known == KnownBuilds.Verified)
            {
                Pass("eboot.tables", "eboot tables", "at the documented addresses", "at the documented addresses");
                return;
            }

            Eboot.LocateResult located;
            try
            {
                located = Eboot.EbootLocator.Locate(Eboot.EbootReader.Parse(data));
            }
            catch (Eboot.EbootFormatException e)
            {
                Fail("eboot.tables", "eboot tables", BuildFailure.EbootProbeFailed, "every table located by content", "unreadable",
                    $"eboot.bin cannot be read as a PS4 SELF: {e.Message}");
                return;
            }

            Locations = located;
            if (located.Complete)
            {
                Note("eboot.tables", "eboot tables", "at the documented addresses", located.Summary(),
                    $"The eboot is not the verified one; {located.Summary()}.");
            }
            else
            {
                Fail("eboot.tables", "eboot tables", BuildFailure.EbootProbeFailed, "every table located by content", located.Summary(),
                    $"This copy ({Identity.ShortName}) is a build the port does not know, and {located.Missing.Count} of its tables could not be found: " +
                    $"{string.Join(", ", located.Missing.Select(m => m.Name))}. Please report it with the log file.");
            }
        }

        // ---- triplet archives (FORMATS 2, containers.md 2 and 3) -------------------------------

        public void CheckTriplet(TripletSpec spec)
        {
            string id = "data." + spec.Name;
            string prefix = "image/" + spec.Name;
            var missing = new List<string>();
            foreach (string ext in spec.HasNam ? new[] { ".hed", ".nam", ".mrg" } : new[] { ".hed", ".mrg" })
            {
                if (!files.Exists(prefix + ext))
                {
                    missing.Add(spec.Name + ext);
                }
            }

            if (missing.Count > 0)
            {
                Fail(id + ".files", spec.Name + " files", BuildFailure.FileMissing, "all present", "missing " + string.Join(", ", missing),
                    $"Required file{(missing.Count > 1 ? "s" : string.Empty)} missing from image\\: {string.Join(", ", missing)}. The folder is incomplete.");
                return;
            }

            Pass(id + ".files", spec.Name + " files", "all present", "all present");
            ArchiveIndex index;
            try
            {
                index = ArchiveIndex.Load(files, prefix, null);
            }
            catch (ContainerFormatException e)
            {
                Fail(id + ".layout", spec.Name + " layout", BuildFailure.LayoutInvalid, "valid HED/NAM", e.Message,
                    $"{spec.Name}.hed or .nam is not a valid table: {e.Message}");
                return;
            }

            Counts.TripletEntries[spec.Name] = index.Count;
            var problems = new List<string>();
            if (index.Format != spec.Format)
            {
                problems.Add($"layout {index.Format} instead of {spec.Format}");
            }

            problems.AddRange(index.Validate());
            int termLen = index.Hed.Terminator.Length;
            if (spec.HasTerminator ? termLen == 0 : termLen != 0)
            {
                problems.Add(spec.HasTerminator ? "no 0xFF terminator record" : "unexpected terminator");
            }

            if (index.Hed.Trailing.Length != 0)
            {
                problems.Add($"{index.Hed.Trailing.Length} bytes after the terminator");
            }

            if (!index.Hed.BankStarts.SequenceEqual(spec.BankStarts))
            {
                problems.Add($"bank starts [{string.Join(", ", index.Hed.BankStarts)}] instead of [{string.Join(", ", spec.BankStarts)}]");
            }

            if (problems.Count == 0)
            {
                Pass(id + ".layout", spec.Name + " layout",
                    $"{spec.Format}, gapless, ends at the .mrg size", $"{index.Format}, {index.Count} entries");
            }
            else
            {
                string shown = string.Join("; ", problems.Take(3)) + (problems.Count > 3 ? $"; and {problems.Count - 3} more" : string.Empty);
                Fail(id + ".layout", spec.Name + " layout", BuildFailure.LayoutInvalid, "valid", $"{problems.Count} problem(s)",
                    $"{spec.Name} does not have the documented layout: {shown}.");
            }

            CountCheck(id + ".count", spec.Name + " entries", spec.Entries, index.Count, spec.Name + " entry count");
        }

        // ---- standalone mrgd00 files (containers.md 8) -----------------------------------------

        public void CheckStandalone(StandaloneSpec spec)
        {
            string id = "data." + spec.FileName;
            string path = "image/" + spec.FileName;
            if (!files.Exists(path))
            {
                Fail(id + ".files", spec.FileName, BuildFailure.FileMissing, "present", "missing",
                    $"Required file missing from image\\: {spec.FileName}. The folder is incomplete.");
                return;
            }

            Pass(id + ".files", spec.FileName + " present", "present", "present");
            try
            {
                var sections = ReadSections(path, out long length);
                Counts.StandaloneSections[spec.FileName] = sections.Length;
                long end = sections.Length == 0 ? 0 : sections.Max(s => s.End);
                long packed = end + Mzp.PadAfter(end);
                if (packed != length)
                {
                    Fail(id + ".layout", spec.FileName + " layout", BuildFailure.LayoutInvalid, N(length), N(packed),
                        $"{spec.FileName}: the sections end at {N(packed)} (with padding) but the file has {N(length)} bytes.");
                }
                else
                {
                    Pass(id + ".layout", spec.FileName + " layout", "sections fill the file", "sections fill the file");
                }

                CountCheck(id + ".sections", spec.FileName + " sections", spec.Sections, sections.Length, spec.FileName + " section count");
            }
            catch (ContainerFormatException e)
            {
                Fail(id + ".layout", spec.FileName + " layout", BuildFailure.LayoutInvalid, "valid mrgd00", e.Message,
                    $"{spec.FileName} is not a valid mrgd00 container: {e.Message}");
            }
        }

        private MzpSection[] ReadSections(string path, out long length)
        {
            length = files.Length(path);
            byte[] head8 = files.ReadRange(path, 0, (int)Math.Min(8, length));
            if (head8.Length < 8 || !head8.AsSpan(0, 6).SequenceEqual(Mzp.Magic))
            {
                throw new ContainerFormatException("bad magic (expected mrgd00)");
            }

            int count = BinaryPrimitives.ReadUInt16LittleEndian(head8.AsSpan(6));
            int tableEnd = 8 + 8 * count;
            if (tableEnd > length)
            {
                throw new ContainerFormatException($"section table ({count} records) exceeds the file size {length}");
            }

            return MzpContainer.ReadTable(files.ReadRange(path, 0, tableEnd), length);
        }

        // ---- scripts (allscr.mrg section 0) -----------------------------------------------------

        public void CheckScripts()
        {
            const string id = "data.scripts";
            const string path = "image/allscr.mrg";
            if (!files.Exists(path))
            {
                return;
            }

            try
            {
                var sections = ReadSections(path, out _);
                var names = NamTable.ParseNam32(files.ReadRange(path, sections[0].Offset, sections[0].Size));
                Counts.Scripts = names.Count;
                CountCheck(id, "scripts (names in allscr.mrg section 0)", AnalysedBuild.Scripts, names.Count, "script name count");
                if (sections.Length - 3 != names.Count)
                {
                    Fail(id + ".aligned", "script sections match the names", BuildFailure.LayoutInvalid,
                        N(names.Count), N(sections.Length - 3),
                        $"allscr.mrg has {sections.Length} sections for {names.Count} script names; script k should be section k + 3.");
                }
                else
                {
                    Pass(id + ".aligned", "script sections match the names", "sections - 3 = names", "sections - 3 = names");
                }
            }
            catch (Exception e) when (e is ContainerFormatException or IndexOutOfRangeException)
            {
                Fail(id, "scripts (names in allscr.mrg section 0)", BuildFailure.LayoutInvalid, "549 NAM32 names", e.Message,
                    $"allscr.mrg section 0 is not the script name table: {e.Message}");
            }
        }

        // ---- script_text.mrg (text.md 2) --------------------------------------------------------

        public void CheckText()
        {
            const string path = "image/script_text.mrg";
            if (!files.Exists(path))
            {
                return;
            }

            try
            {
                var sections = ReadSections(path, out _);
                if (sections.Length != 2 * AnalysedBuild.TextLanguages.Count)
                {
                    return; // the section count check already failed
                }

                for (int k = 0; k < AnalysedBuild.TextLanguages.Count; k++)
                {
                    string lang = AnalysedBuild.TextLanguages[k];
                    string id = "data.text." + lang;
                    var table = sections[2 * k];
                    var blob = sections[(2 * k) + 1];
                    byte[] raw = files.ReadRange(path, table.Offset, table.Size);
                    int entries = raw.Length / 4;
                    int end = -1;
                    for (int i = 0; i < entries; i++)
                    {
                        if (BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(i * 4)) == 0xFFFFFFFFu)
                        {
                            end = i;
                            break;
                        }
                    }

                    if (raw.Length % 4 != 0 || end < 2)
                    {
                        Fail(id, "text lines " + lang, BuildFailure.LayoutInvalid, "offset table with an end mark", "no end mark",
                            $"script_text.mrg section {2 * k} is not a big-endian offset table with a 0xFFFFFFFF end mark.");
                        continue;
                    }

                    uint guard1 = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan((end - 2) * 4));
                    uint guard2 = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan((end - 1) * 4));
                    if (guard1 != blob.Size || guard2 != blob.Size)
                    {
                        Fail(id, "text lines " + lang, BuildFailure.LayoutInvalid, N(blob.Size), $"{N(guard1)}, {N(guard2)}",
                            $"script_text.mrg {lang}: the offset table does not end with the blob size twice.");
                        continue;
                    }

                    int lines = end - 2;
                    Counts.TextLines[lang] = lines;
                    CountCheck(id, "text lines " + lang, AnalysedBuild.TextLines, lines, "text line count (" + lang + ")");
                }
            }
            catch (ContainerFormatException e)
            {
                Fail("data.text", "script_text.mrg tables", BuildFailure.LayoutInvalid, "valid", e.Message,
                    $"script_text.mrg is not a valid container: {e.Message}");
            }
        }

        // ---- vse names (se_nam.mrg section 2) ---------------------------------------------------

        public void CheckVseNames()
        {
            const string path = "image/se_nam.mrg";
            if (!files.Exists(path))
            {
                return;
            }

            try
            {
                var sections = ReadSections(path, out _);
                if (sections.Length < 3)
                {
                    return;
                }

                var names = NamTable.ParseNam32(files.ReadRange(path, sections[2].Offset, sections[2].Size));
                Counts.VseNames = names.Count;
                CountCheck("data.vse.names", "vse names (se_nam.mrg section 2)", AnalysedBuild.VseNames, names.Count, "vse name count");
            }
            catch (ContainerFormatException e)
            {
                Fail("data.vse.names", "vse names (se_nam.mrg section 2)", BuildFailure.LayoutInvalid, "NAM32", e.Message,
                    $"se_nam.mrg section 2 is not a NAM32 table: {e.Message}");
            }
        }

        // ---- movies ----------------------------------------------------------------------------

        public void CheckMovies()
        {
            var movies = files.ListFiles("image/movie").Count(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
            Counts.MovieFiles = movies;
            if (movies == 0 && !files.ListFiles("image/movie").Any())
            {
                Fail("data.movies", "movie files (image/movie/*.mp4)", BuildFailure.FileMissing, N(AnalysedBuild.MovieFiles), "0",
                    "The folder image\\movie is missing or empty; the movies cannot play.");
                return;
            }

            CountCheck("data.movies", "movie files (image/movie/*.mp4)", AnalysedBuild.MovieFiles, movies, "movie file count");
        }

        // ---- totals ----------------------------------------------------------------------------

        public void CheckTotals()
        {
            if (Counts.TripletEntries.Count == AnalysedBuild.Triplets.Count)
            {
                CountCheck("data.triplets.total", "triplet entries over 12 archives", AnalysedBuild.TripletEntryTotal, Counts.TripletTotal, "triplet entry total");
            }

            if (Counts.StandaloneSections.Count == AnalysedBuild.Standalone.Count)
            {
                CountCheck("data.standalone.total", "standalone sections over 8 files", AnalysedBuild.StandaloneSectionTotal, Counts.StandaloneTotal, "standalone section total");
            }
        }
    }
}
