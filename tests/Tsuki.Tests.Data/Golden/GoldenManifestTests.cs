#if TSUKI_DEV
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tsuki.Data.Containers;
using Tsuki.Data.Golden;

namespace Tsuki.Tests.Data.Golden;

/// <summary>
/// The manifest reader and the integrity check (<c>tools/oracle/manifest.py</c>, schema <c>tsuki-golden-manifest/1</c>): on the real
/// slice, and on a tiny synthetic area that needs no game data and shows each kind of damage being caught.
/// </summary>
public sealed class GoldenManifestTests
{
    private readonly ITestOutputHelper _output;

    public GoldenManifestTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // -- the real slice -----------------------------------------------------------------------------------------------

    [GameDataFact]
    public void TheAreaManifestParsesAndEqualsTheRawJson()
    {
        var manifest = GoldenManifest.Load(Path.Combine(SliceExpect.Dir, "manifest.json"));
        JsonElement raw = SliceExpect.Area;

        Assert.Equal(GoldenManifest.SchemaName, raw.GetProperty("schema").GetString());
        Assert.Equal("slice", manifest.Area);
        Assert.Equal(raw.GetProperty("description").GetString(), manifest.Description);
        Assert.Equal(raw.GetProperty("created_utc").GetString(), manifest.CreatedUtc);
        // Sizes follow the raw JSON, never a pinned number: the slice grows when export_slice is extended (followups.md).
        Assert.Equal(raw.GetProperty("parts").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray(), manifest.Parts.Keys.ToArray());
        foreach (string part in new[] { "audio", "eboot", "fonts", "layouts", "pictures", "scripts", "text" })
        {
            Assert.Contains(part, manifest.Parts.Keys);
        }

        Assert.NotEmpty(manifest.Files);
        Assert.Equal(raw.GetProperty("files").GetArrayLength(), manifest.Files.Length);
        Assert.Equal(raw.GetProperty("inputs").GetArrayLength(), manifest.Inputs.Length);
        Assert.Equal(raw.GetProperty("input_entries").GetArrayLength(), manifest.InputEntries.Length);
        Assert.Equal(raw.GetProperty("counts").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray(), manifest.Counts.Keys.ToArray());
        Assert.Equal(raw.GetProperty("checks").GetArrayLength(), manifest.Checks.Length);
        Assert.Equal(raw.GetProperty("findings").GetArrayLength(), manifest.Findings.Length);
        Assert.Equal(raw.GetProperty("conventions").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray(), manifest.Conventions.Keys.ToArray());

        var mismatches = new Mismatches();
        int i = 0;
        foreach (JsonElement f in raw.GetProperty("files").EnumerateArray())
        {
            GoldenFileRecord r = manifest.Files[i++];
            mismatches.Equal("path", f.GetProperty("path").GetString()!, r.Path);
            mismatches.Equal($"{r.Path} part", f.GetProperty("part").GetString()!, r.Part);
            mismatches.Equal($"{r.Path} size", f.GetProperty("size").GetInt64(), r.Size);
            mismatches.Equal($"{r.Path} sha1", f.GetProperty("sha1").GetString()!, r.Sha1);
            mismatches.Equal($"{r.Path} format", f.GetProperty("format").GetString()!, r.Format);
            mismatches.Equal($"{r.Path} module", f.GetProperty("produced_by").GetProperty("module").GetString()!, r.ProducedBy.Module);
            mismatches.Equal($"{r.Path} function", f.GetProperty("produced_by").GetProperty("function").GetString()!, r.ProducedBy.Function);
            mismatches.Equal($"{r.Path} args", f.GetProperty("produced_by").GetProperty("args").GetRawText(), r.ProducedBy.Args.GetRawText());
            mismatches.Equal($"{r.Path} meta", f.GetProperty("meta").GetRawText(), r.Meta.GetRawText());
            mismatches.Equal($"{r.Path} found by path", true, ReferenceEquals(manifest.Find(r.Path), r));
            mismatches.Equal($"{r.Path} format is a convention", true, manifest.Conventions.ContainsKey(r.Format));
        }

        mismatches.AssertNone("area manifest files");

        // the exporter's own numbers
        Assert.Equal(manifest.Files.Sum(f => f.Size), manifest.Parts.Values.Sum(p => p.Bytes));
        Assert.Equal(manifest.Files.Length, manifest.Parts.Values.Sum(p => p.Files));
        Assert.All(manifest.Parts.Values, p => Assert.Equal(p.Files, manifest.Files.Count(f => f.Part == p.Name)));
        Assert.True(manifest.Checks.All(c => c.Ok), "a recorded cross-check failed");
        Assert.NotEmpty(manifest.Checks);
        Assert.NotEmpty(manifest.Inputs);
        Assert.NotEmpty(manifest.InputEntries);   // their lengths were compared with the raw JSON above
        Assert.Equal(12_767_802L, manifest.Game.EbootSize);
        Assert.Equal("c2843415fdfda5ebbc84f4d55c6cca044a7476436cf0cfdfccac83116beeb918", manifest.Game.EbootSha256);
        Assert.NotNull(manifest.Generator);
        Assert.Equal("oracle.export_slice", manifest.Generator.Producer.Module);
        Assert.Equal("export", manifest.Generator.Producer.Function);
        Assert.NotEmpty(manifest.Generator.ToolSources);
        Assert.False(manifest.Contains("manifest.json"));
        Assert.Null(manifest.Find("nowhere/nothing.bin"));
    }

    [GameDataFact]
    public void CountsOfTheManifestAreTheNumbersTheSliceDocumentsState()
    {
        var manifest = GoldenManifest.Load(Path.Combine(SliceExpect.Dir, "manifest.json"));
        Assert.Equal(49_620, manifest.Counts["eboot.triplet_entries"].GetInt32());
        Assert.Equal(43_969, manifest.Counts["eboot.text_lines"].GetInt32());
        Assert.Equal(549 + 3, manifest.Counts["eboot.allscr_sections"].GetInt32());
        Assert.Equal(134, manifest.Counts["eboot.movies"].GetInt32());
        Assert.Equal(24_192, manifest.Counts["eboot.motions"].GetInt32());
        Assert.Equal(71_905, manifest.Counts["eboot.motion_keyframes"].GetInt32());
        Assert.Equal(17_423, manifest.Counts["eboot.font_lookup_entries"].GetInt32());
        Assert.Equal(129, manifest.Counts["scripts.script2.statements"].GetInt32());
        Assert.Equal(2130, manifest.Counts["scripts.script3.statements"].GetInt32());
        Assert.Equal(65, manifest.Counts["fonts.ja.pages"].GetInt32());
        Assert.Equal(JsonValueKind.Array, manifest.Counts["layouts.ja.nlines"].ValueKind);
    }

    [GameDataFact]
    public void EveryPartManifestListsTheFilesOfItsPartInTheAreaManifest()
    {
        var manifest = GoldenManifest.Load(Path.Combine(SliceExpect.Dir, "manifest.json"));
        foreach (GoldenPart part in manifest.Parts.Values)
        {
            var pm = GoldenManifest.Load(Path.Combine(SliceExpect.Dir, part.Manifest.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal("slice/" + part.Name, pm.Area);
            Assert.Empty(pm.Parts);
            string[] mine = manifest.Files.Where(f => f.Part == part.Name).Select(f => $"{f.Path} {f.Sha1} {f.Size}").Order(StringComparer.Ordinal).ToArray();
            string[] theirs = pm.Files.Select(f => $"{f.Path} {f.Sha1} {f.Size}").Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(mine, theirs);
            Assert.Equal(part.Files, pm.Files.Length);
            Assert.All(pm.Checks, c => Assert.True(c.Ok, c.Name));
        }
    }

    [GameDataFact]
    public void TheSliceVerifiesCleanLikeOracleRunSliceVerify()
    {
        using GoldenSlice slice = GoldenSlice.Open(SliceExpect.Dir);
        var sw = Stopwatch.StartNew();
        IReadOnlyList<string> problems = slice.Verify();
        sw.Stop();
        _output.WriteLine($"GoldenSlice.Verify (size + SHA-1 of {slice.Manifest.Files.Sum(f => f.Size):N0} bytes in {slice.Manifest.Files.Length} files): {problems.Count} problems, {sw.Elapsed.TotalSeconds:F2} s");
        Assert.Empty(problems);
        Assert.Empty(slice.Verify(hashFiles: false));
    }

    [GameDataFact]
    public void ProjectPrefixedInputsAreFilesOfTheRepository()
    {
        using GoldenSlice slice = GoldenSlice.Open(SliceExpect.Dir);
        GoldenInput[] project = slice.Manifest.Inputs.Where(i => i.Kind == GoldenInputKind.ProjectFile).ToArray();
        int rawProject = SliceExpect.Area.GetProperty("inputs").EnumerateArray().Count(i => i.GetProperty("path").GetString()!.StartsWith("project:", StringComparison.Ordinal));
        Assert.Equal(rawProject, project.Length);
        Assert.All(project, i => Assert.StartsWith("project:", i.Path, StringComparison.Ordinal));

        // samples/misc_layer_state_and_motions/motion_loops.json, the loop flags of the motions
        GoldenInput input = Assert.Single(project, i => i.Path == "project:samples/misc_layer_state_and_motions/motion_loops.json");
        Assert.False(input.SizeOnly);

        string full = input.Resolve(GameFolder.Path, slice.RepositoryRoot)!;
        Assert.Equal(Path.Combine(GoldenData.RepoRoot, "samples", "misc_layer_state_and_motions", "motion_loops.json"), full);
        Assert.True(File.Exists(full), full);
        byte[] bytes = File.ReadAllBytes(full);
        Assert.Equal(input.Size, bytes.LongLength);
        Assert.Equal(input.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.Null(input.Resolve(GameFolder.Path, repositoryRoot: null));

        // and a game file does not resolve under the repository
        GoldenInput eboot = slice.Manifest.Inputs.Single(i => i.Path == "eboot.bin");
        Assert.Equal(GoldenInputKind.GameFile, eboot.Kind);
        Assert.Equal(Path.Combine(GameFolder.Require(), "eboot.bin"), eboot.Resolve(GameFolder.Path, slice.RepositoryRoot));
        Assert.Null(eboot.Resolve(gameDirectory: null, slice.RepositoryRoot));
    }

    [GameDataFact]
    public void TheGameFolderStillHasTheFilesTheSliceWasExportedFrom()
    {
        // followups.md: verify_area does not detect input-hash drift. This does: sizes of all inputs, SHA-256 of every input
        // that is hashed and small (the 1.9 GB of prologue movies are checked by size only).
        using GoldenSlice slice = GoldenSlice.Open(SliceExpect.Dir);
        var mismatches = new Mismatches();
        int hashed = 0;
        foreach (GoldenInput input in slice.Manifest.Inputs.Where(i => i.Kind == GoldenInputKind.GameFile))
        {
            string path = input.Resolve(GameFolder.Path, null)!;
            if (!File.Exists(path))
            {
                mismatches.Add($"{input.Path}: missing from the game folder");
                continue;
            }

            long size = new FileInfo(path).Length;
            mismatches.Equal($"{input.Path} size", input.Size, size);
            if (input.Sha256 is { } sha && size <= GoldenInputSizeLimit)
            {
                using FileStream stream = File.OpenRead(path);
                mismatches.Equal($"{input.Path} SHA-256", sha, Convert.ToHexStringLower(SHA256.HashData(stream)));
                hashed++;
            }
        }

        _output.WriteLine($"{slice.Manifest.Inputs.Count(i => i.Kind == GoldenInputKind.GameFile)} game inputs checked by size, {hashed} by SHA-256");
        mismatches.AssertNone("slice inputs against the game folder");
        Assert.True(hashed >= 25, $"only {hashed} inputs were hashed");
    }

    private const long GoldenInputSizeLimit = 64L * 1024 * 1024;     // oracle manifest.SIZE_ONLY_LIMIT

    [GameDataFact]
    public void TheArchiveEntriesTheSliceWasExportedFromStillHaveTheRecordedBytes()
    {
        using GoldenSlice slice = GoldenSlice.Open(SliceExpect.Dir);
        var mismatches = new Mismatches();
        var archives = new Dictionary<string, MrgArchive>(StringComparer.Ordinal);
        StandaloneMrg? allscr = null;
        try
        {
            foreach (GoldenInputEntry e in slice.Manifest.InputEntries)
            {
                string at = $"{e.Archive}#{e.Index}";
                if (e.Archive == "allscr")
                {
                    allscr ??= StandaloneMrg.Open(GameImage.Path("allscr.mrg"));
                    MzpSection section = allscr.Container.Sections[e.Index];
                    mismatches.Equal($"{at} offset", e.Offset, section.Offset);
                    mismatches.Equal($"{at} size", e.Size, (long)section.Size);
                    mismatches.Equal($"{at} SHA-1", e.Sha1, SliceExpect.Sha1(allscr.Container.Section(e.Index).Span));
                    continue;
                }

                if (!archives.TryGetValue(e.Archive, out MrgArchive? archive))
                {
                    archive = GameImage.Open(e.Archive);
                    archives[e.Archive] = archive;
                }

                MrgEntry entry = archive.Entries[e.Index];
                mismatches.Equal($"{at} offset", e.Offset, entry.Offset);
                ReadOnlyMemory<byte> stored = e.Archive is "voice" or "voice2" ? archive.SectorMemory(e.Index) : archive.Memory(e.Index);
                mismatches.Equal($"{at} size", e.Size, (long)stored.Length);
                mismatches.Equal($"{at} SHA-1", e.Sha1, SliceExpect.Sha1(stored.Span));
                if (e.Name is not null && archive.Index.Nam is { } nam)
                {
                    mismatches.Equal($"{at} name", e.Name, nam[e.Index]);
                }
            }
        }
        finally
        {
            allscr?.Dispose();
            foreach (MrgArchive a in archives.Values)
            {
                a.Dispose();
            }
        }

        mismatches.AssertNone("archive entries against the game folder");
    }

    // -- classification of input paths (no data needed) ---------------------------------------------------------------

    [Theory]
    [InlineData("eboot.bin", GoldenInputKind.GameFile)]
    [InlineData("image/allpac0.hed", GoldenInputKind.GameFile)]
    [InlineData("project:samples/x/y.json", GoldenInputKind.ProjectFile)]
    [InlineData("C:/Data/someone/file.bin", GoldenInputKind.Absolute)]
    [InlineData("D:\\Games\\file.bin", GoldenInputKind.Absolute)]
    [InlineData("/mnt/data/file.bin", GoldenInputKind.Absolute)]
    public void InputPathsAreClassifiedByTheExportersRule(string path, GoldenInputKind kind)
    {
        Assert.Equal(kind, new GoldenInput(path, 1, null, "size-only").Kind);
    }

    [Fact]
    public void InputsResolveAgainstTheirOwnBaseFolder()
    {
        string game = Path.Combine(Path.GetTempPath(), "game");
        string repo = Path.Combine(Path.GetTempPath(), "repo");
        Assert.Equal(Path.Combine(game, "image", "a.hed"), new GoldenInput("image/a.hed", 1, null, "size-only").Resolve(game, repo));
        Assert.Equal(Path.Combine(repo, "samples", "m.json"), new GoldenInput("project:samples/m.json", 1, "00", "sha256").Resolve(game, repo));
        Assert.Equal("C:/abs/f.bin", new GoldenInput("C:/abs/f.bin", 1, null, "size-only").Resolve(game, repo));
        Assert.Null(new GoldenInput("a.bin", 1, null, "size-only").Resolve(null, repo));
        Assert.Null(new GoldenInput("project:a.bin", 1, null, "size-only").Resolve(game, null));
        Assert.True(new GoldenInput("a.bin", 1, null, "size-only").SizeOnly);
        Assert.False(new GoldenInput("a.bin", 1, "ab", "sha256").SizeOnly);
    }

    // -- a synthetic area: each kind of damage is caught --------------------------------------------------------------

    [Fact]
    public void ASyntheticAreaVerifiesClean()
    {
        using var area = new SyntheticArea();
        using GoldenSlice slice = area.Open();
        Assert.Empty(slice.Verify());
        Assert.Equal(2, slice.Manifest.Files.Length);
        Assert.Equal(area.Data, slice.ReadAll("part/data.bin"));
        Assert.Equal(area.Data[1..3], slice.ReadRange("part/data.bin", 1, 2));
        using JsonDocument doc = slice.ReadJson("part/info.json");
        Assert.Equal(7, doc.RootElement.GetProperty("seven").GetInt32());
        Assert.Equal("slice/part", slice.PartManifest("part").Area);
        Assert.Throws<GoldenSliceMissingException>(() => slice.PartManifest("nope"));
    }

    [Fact]
    public void AFlippedByteIsCaughtByTheSha1()
    {
        using var area = new SyntheticArea();
        area.Mutate("part/data.bin", b => b[2] ^= 0x40);
        using GoldenSlice slice = area.Open();
        string problem = Assert.Single(slice.Verify());
        Assert.StartsWith("part/data.bin: SHA-1 ", problem, StringComparison.Ordinal);
        Assert.Contains("!= manifest", problem, StringComparison.Ordinal);
        Assert.Empty(slice.Verify(hashFiles: false));      // the size is unchanged
    }

    [Fact]
    public void ATruncatedFileIsASizeProblem()
    {
        using var area = new SyntheticArea();
        area.Replace("part/data.bin", b => b[..^1]);
        using GoldenSlice slice = area.Open();
        string problem = Assert.Single(slice.Verify());
        Assert.Equal($"part/data.bin: size {area.Data.Length - 1} != manifest {area.Data.Length}", problem);
        Assert.Equal(problem, Assert.Single(slice.Verify(hashFiles: false)));
    }

    [Fact]
    public void AMissingFileAStrayFileAndABrokenPartManifestAreReported()
    {
        using var area = new SyntheticArea();
        File.Delete(area.PathOf("part/info.json"));
        File.WriteAllBytes(area.PathOf("part/stray.bin"), [9]);
        File.WriteAllBytes(area.PathOf("loose.txt"), [9]);
        area.EditPartManifest(m => m["files"]![0]!["sha1"] = new string('0', 40));
        using GoldenSlice slice = area.Open();
        IReadOnlyList<string> problems = slice.Verify();
        Assert.Contains("part/info.json: missing", problems);
        Assert.Contains("part/stray.bin: not listed in the manifest", problems);
        Assert.Contains("loose.txt: not listed in the manifest", problems);
        Assert.Contains("part part: part manifest lists different files than the area manifest", problems);
        Assert.Equal(4, problems.Count);
    }

    [Fact]
    public void AFailedCrossCheckOfTheExporterFailsTheVerification()
    {
        using var area = new SyntheticArea();
        area.EditAreaManifest(m => m["checks"] = new JsonArray(new JsonObject
        {
            ["name"] = "part.size", ["expected"] = 5, ["actual"] = 6, ["ok"] = false, ["source"] = "FORMATS 2",
        }));
        using GoldenSlice slice = area.Open();
        string problem = Assert.Single(slice.Verify());
        Assert.Equal("check part.size failed: expected 5, got 6", problem);
    }

    [Fact]
    public void OpeningChecksFilesAsTheModeSays()
    {
        using var area = new SyntheticArea();
        area.Mutate("part/data.bin", b => b[0] ^= 1);
        using (GoldenSlice none = area.Open(GoldenVerifyMode.None))
        {
            Assert.Equal(area.Data.Length, none.ReadAll("part/data.bin").Length);
        }

        using (GoldenSlice size = area.Open(GoldenVerifyMode.Size))
        {
            Assert.Equal(area.Data.Length, size.ReadAll("part/data.bin").Length);
        }

        using (GoldenSlice sha = area.Open(GoldenVerifyMode.Sha1))
        {
            var e = Assert.Throws<GoldenFormatException>(() => sha.ReadAll("part/data.bin"));
            Assert.Contains("SHA-1", e.Message, StringComparison.Ordinal);
            Assert.NotEmpty(sha.ReadAll("part/info.json"));
        }

        area.Replace("part/data.bin", b => b[..2]);
        using GoldenSlice truncated = area.Open(GoldenVerifyMode.Size);
        Assert.Contains("size 2 != manifest", Assert.Throws<GoldenFormatException>(() => truncated.ReadAll("part/data.bin")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyFilesTheManifestListsCanBeRead()
    {
        using var area = new SyntheticArea();
        File.WriteAllBytes(area.PathOf("part/stray.bin"), [1, 2, 3]);
        using GoldenSlice slice = area.Open();
        Assert.Throws<GoldenSliceMissingException>(() => slice.ReadAll("part/stray.bin"));
        Assert.Throws<GoldenSliceMissingException>(() => slice.ReadAll("../outside.bin"));
        Assert.False(slice.Contains("part/stray.bin"));
        Assert.Throws<EndOfStreamException>(() => slice.ReadRange("part/data.bin", 3, 10));
        slice.Dispose();
        Assert.Throws<ObjectDisposedException>(() => slice.ReadAll("part/data.bin"));
    }

    [Fact]
    public void TheParserIsStrictAboutSchemaAndRequiredProperties()
    {
        using var area = new SyntheticArea();
        JsonObject good = area.AreaManifest();

        JsonObject wrongSchema = (JsonObject)good.DeepClone();
        wrongSchema["schema"] = "tsuki-golden-manifest/2";
        Assert.Contains("schema", Assert.Throws<GoldenFormatException>(() => Parse(wrongSchema)).Message, StringComparison.Ordinal);

        foreach (string key in new[] { "area", "files", "inputs", "input_entries", "counts", "checks" })
        {
            JsonObject missing = (JsonObject)good.DeepClone();
            missing.Remove(key);
            Assert.Contains($"\"{key}\"", Assert.Throws<GoldenFormatException>(() => Parse(missing)).Message, StringComparison.Ordinal);
        }

        JsonObject unsafePath = (JsonObject)good.DeepClone();
        unsafePath["files"]![0]!["path"] = "../escape.bin";
        Assert.Contains("unsafe path", Assert.Throws<GoldenFormatException>(() => Parse(unsafePath)).Message, StringComparison.Ordinal);

        JsonObject badHash = (JsonObject)good.DeepClone();
        badHash["inputs"] = new JsonArray(new JsonObject { ["path"] = "a.bin", ["size"] = 1, ["sha256"] = null, ["hash"] = "md5" });
        Assert.Contains("md5", Assert.Throws<GoldenFormatException>(() => Parse(badHash)).Message, StringComparison.Ordinal);

        Assert.Contains("not valid JSON", Assert.Throws<GoldenFormatException>(() => GoldenManifest.Parse("{\"schema\": "u8.ToArray(), "broken.json")).Message, StringComparison.Ordinal);
        Assert.Contains("not a JSON object", Assert.Throws<GoldenFormatException>(() => GoldenManifest.Parse("[]"u8.ToArray(), "array.json")).Message, StringComparison.Ordinal);

        GoldenManifest parsed = Parse(good);
        Assert.Equal(GoldenManifest.SchemaName, "tsuki-golden-manifest/1");
        Assert.Equal("slice", parsed.Area);
        Assert.Equal("part/data.bin", parsed.Files[0].Path);
        Assert.Equal(GoldenInputKind.ProjectFile, parsed.Inputs[1].Kind);
        Assert.Equal("proj", parsed.InputEntries[0].Archive);
    }

    [Fact]
    public void OpeningAFolderWhoseManifestIsForAnotherAreaFails()
    {
        using var area = new SyntheticArea();
        area.EditAreaManifest(m => m["area"] = "image");
        var e = Assert.Throws<GoldenFormatException>(() => area.Open());
        Assert.Contains("area \"image\"", e.Message, StringComparison.Ordinal);
    }

    private static GoldenManifest Parse(JsonObject manifest) =>
        GoldenManifest.Parse(System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString()), "synthetic.json");

    /// <summary>A throw-away golden area under the temp folder (synthetic bytes only; deleted on dispose).</summary>
    private sealed class SyntheticArea : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-golden-synthetic-" + Guid.NewGuid().ToString("N"));

        public SyntheticArea()
        {
            Directory.CreateDirectory(Path.Combine(_root, "part"));
            File.WriteAllBytes(PathOf("part/data.bin"), Data);
            File.WriteAllBytes(PathOf("part/info.json"), Info);
            File.WriteAllBytes(PathOf("part/manifest.json"), System.Text.Encoding.UTF8.GetBytes(PartManifestJson().ToJsonString()));
            File.WriteAllBytes(PathOf("manifest.json"), System.Text.Encoding.UTF8.GetBytes(AreaManifest().ToJsonString()));
        }

        public byte[] Data { get; } = [10, 20, 30, 40, 50];

        public static byte[] Info { get; } = System.Text.Encoding.UTF8.GetBytes("{\"seven\": 7}\n");

        public string PathOf(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

        public GoldenSlice Open(GoldenVerifyMode mode = GoldenVerifyMode.Size) => GoldenSlice.Open(_root, mode);

        public void Mutate(string relative, Action<byte[]> change)
        {
            byte[] bytes = File.ReadAllBytes(PathOf(relative));
            change(bytes);
            File.WriteAllBytes(PathOf(relative), bytes);
        }

        public void Replace(string relative, Func<byte[], byte[]> change) =>
            File.WriteAllBytes(PathOf(relative), change(File.ReadAllBytes(PathOf(relative))));

        public void EditAreaManifest(Action<JsonObject> edit)
        {
            JsonObject m = AreaManifest();
            edit(m);
            File.WriteAllBytes(PathOf("manifest.json"), System.Text.Encoding.UTF8.GetBytes(m.ToJsonString()));
        }

        public void EditPartManifest(Action<JsonObject> edit)
        {
            JsonObject m = PartManifestJson();
            edit(m);
            File.WriteAllBytes(PathOf("part/manifest.json"), System.Text.Encoding.UTF8.GetBytes(m.ToJsonString()));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // best effort; the folder only holds synthetic bytes
            }
        }

        public JsonObject AreaManifest()
        {
            JsonObject m = Common("slice");
            m["description"] = "synthetic";
            m["conventions"] = new JsonObject { ["bytes"] = "opaque", ["json"] = "json" };
            m["parts"] = new JsonObject
            {
                ["part"] = new JsonObject
                {
                    ["manifest"] = "part/manifest.json", ["description"] = "p", ["files"] = 2,
                    ["bytes"] = Data.Length + Info.Length, ["created_utc"] = "2026-01-01T00:00:00Z",
                },
            };
            return m;
        }

        private JsonObject PartManifestJson() => Common("slice/part");

        private JsonObject Common(string area)
        {
            string Sha1(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));
            JsonObject Producer() => new()
            {
                ["module"] = "synthetic", ["function"] = "make", ["args"] = new JsonObject { ["n"] = 1 }, ["uses"] = new JsonArray(),
            };

            return new JsonObject
            {
                ["schema"] = GoldenManifest.SchemaName,
                ["area"] = area,
                ["description"] = "synthetic part",
                ["created_utc"] = "2026-01-01T00:00:00Z",
                ["generator"] = new JsonObject
                {
                    ["module"] = "synthetic", ["function"] = "export", ["args"] = new JsonObject(), ["uses"] = new JsonArray(),
                    ["python"] = "3.13", ["numpy"] = "2.0", ["platform"] = "test", ["ffmpeg"] = null, ["tool_commit"] = null,
                    ["tool_digest"] = "00", ["tool_sources"] = new JsonObject { ["oracle/x.py"] = "11" },
                },
                ["game"] = new JsonObject { ["dir"] = "C:/nowhere", ["eboot"] = new JsonObject { ["size"] = 1, ["sha256"] = "ab" } },
                ["conventions"] = new JsonObject { ["bytes"] = "opaque", ["json"] = "json" },
                ["files"] = new JsonArray(
                    new JsonObject
                    {
                        ["path"] = "part/data.bin", ["part"] = "part", ["size"] = Data.Length, ["sha1"] = Sha1(Data),
                        ["format"] = "bytes", ["produced_by"] = Producer(), ["meta"] = new JsonObject(),
                    },
                    new JsonObject
                    {
                        ["path"] = "part/info.json", ["part"] = "part", ["size"] = Info.Length, ["sha1"] = Sha1(Info),
                        ["format"] = "json", ["produced_by"] = Producer(), ["meta"] = new JsonObject { ["k"] = "v" },
                    }),
                ["inputs"] = new JsonArray(
                    new JsonObject { ["path"] = "eboot.bin", ["size"] = 1, ["sha256"] = "ab", ["hash"] = "sha256" },
                    new JsonObject { ["path"] = "project:samples/x.json", ["size"] = 2, ["sha256"] = null, ["hash"] = "size-only" }),
                ["input_entries"] = new JsonArray(
                    new JsonObject { ["archive"] = "proj", ["index"] = 3, ["name"] = null, ["offset"] = 0, ["size"] = 5, ["sha1"] = "cd" }),
                ["counts"] = new JsonObject { ["n"] = 2 },
                ["checks"] = new JsonArray(),
                ["findings"] = new JsonArray(),
            };
        }
    }
}
#endif
