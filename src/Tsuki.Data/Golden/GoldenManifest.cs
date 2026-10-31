#if TSUKI_DEV
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Tsuki.Data.Golden;

/// <summary>How a manifest input path is to be read (see <see cref="GoldenInput"/>).</summary>
public enum GoldenInputKind
{
    /// <summary>A file of the user's game folder, relative to it (<c>eboot.bin</c>, <c>image/allpac0.hed</c>).</summary>
    GameFile,

    /// <summary>A file of this repository, written <c>project:&lt;path relative to the repository root&gt;</c>.</summary>
    ProjectFile,

    /// <summary>A file outside both, recorded by its absolute path.</summary>
    Absolute,
}

/// <summary>The generator block: which oracle function wrote a file or an area, and the tool state it ran in.</summary>
/// <param name="Module">Python module of the oracle function.</param>
/// <param name="Function">Function name.</param>
/// <param name="Args">Arguments the function was called with (a JSON object).</param>
/// <param name="Uses">Reference-library calls it is built on (only in a file record's <c>produced_by</c>).</param>
public sealed record GoldenProducer(string Module, string Function, JsonElement Args, ImmutableArray<string> Uses);

/// <summary>The generator of an area or part manifest, with the tool environment (tool sources are hashed, there is no commit yet).</summary>
/// <param name="Producer">The oracle function that wrote the manifest.</param>
/// <param name="Python">Python version, or null.</param>
/// <param name="Numpy">NumPy version, or null.</param>
/// <param name="Platform">Platform string, or null.</param>
/// <param name="Ffmpeg">FFmpeg banner the audio decode used, or null.</param>
/// <param name="ToolCommit">Tool commit, null until the repository exists.</param>
/// <param name="ToolDigest">SHA-1 over the hashes of <c>tools/tsuki</c>, <c>tools/eboot</c> and <c>tools/oracle</c> sources.</param>
/// <param name="ToolSources">SHA-1 of every such source, keyed by the path relative to <c>tools/</c>.</param>
public sealed record GoldenGenerator(
    GoldenProducer Producer,
    string? Python,
    string? Numpy,
    string? Platform,
    string? Ffmpeg,
    string? ToolCommit,
    string? ToolDigest,
    ImmutableSortedDictionary<string, string> ToolSources);

/// <summary>The <c>game</c> block: the folder the export was made from and the identity of its eboot.bin.</summary>
/// <param name="Dir">The game folder on the exporting machine (informative; may not exist here), or null.</param>
/// <param name="EbootSize">Size of eboot.bin, or null if the block has none.</param>
/// <param name="EbootSha256">SHA-256 of eboot.bin as lower-case hex, or null.</param>
public sealed record GoldenGame(string? Dir, long? EbootSize, string? EbootSha256);

/// <summary>One part of an area (a sub-folder with its own manifest).</summary>
/// <param name="Name">Part name, also its folder name.</param>
/// <param name="Manifest">Path of the part manifest relative to the area folder.</param>
/// <param name="Description">What the part holds.</param>
/// <param name="Files">Number of files in the part.</param>
/// <param name="Bytes">Their total size.</param>
/// <param name="CreatedUtc">When the part was written.</param>
public sealed record GoldenPart(string Name, string Manifest, string Description, int Files, long Bytes, string? CreatedUtc);

/// <summary>One exported file.</summary>
/// <param name="Path">POSIX path relative to the area folder (<c>pictures/allpac0_00270.rgba</c>).</param>
/// <param name="Part">The part that wrote it.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Sha1">SHA-1 of the file, lower-case hex.</param>
/// <param name="Format">One of the manifest's <c>conventions</c> (<c>json</c>, <c>bytes</c>, <c>rgba8-tiles</c>, <c>a8-pages</c>, <c>f32le</c>).</param>
/// <param name="ProducedBy">The oracle function and arguments that wrote it.</param>
/// <param name="Meta">Free-form facts about the file (a JSON object; empty when there are none).</param>
public sealed record GoldenFileRecord(string Path, string Part, long Size, string Sha1, string Format, GoldenProducer ProducedBy, JsonElement Meta);

/// <summary>
/// A game-folder file the export read. Small files carry a SHA-256; the multi-GB archives are recorded by size only
/// (<see cref="Hash"/> is then <c>size-only</c>) and each entry read from them is in <see cref="GoldenInputEntry"/>.
/// </summary>
/// <param name="Path">The path as written by the exporter; see <see cref="Kind"/>.</param>
/// <param name="Size">File size in bytes.</param>
/// <param name="Sha256">SHA-256 as lower-case hex, or null for <c>size-only</c> inputs.</param>
/// <param name="Hash"><c>sha256</c> or <c>size-only</c>.</param>
public sealed record GoldenInput(string Path, long Size, string? Sha256, string Hash)
{
    /// <summary>The prefix of repository-relative inputs.</summary>
    public const string ProjectPrefix = "project:";

    /// <summary>
    /// How <see cref="Path"/> is to be read. The exporter writes a path relative to the game folder when the file is inside it,
    /// <c>project:</c> plus the path relative to the repository root when it is inside the repository
    /// (<c>tools/oracle/manifest.py</c>, <c>AreaWriter._input_record</c>), and the absolute path otherwise.
    /// </summary>
    public GoldenInputKind Kind
    {
        get
        {
            if (Path.StartsWith(ProjectPrefix, StringComparison.Ordinal))
            {
                return GoldenInputKind.ProjectFile;
            }

            bool drive = Path.Length >= 3 && char.IsAsciiLetter(Path[0]) && Path[1] == ':' && (Path[2] is '/' or '\\');
            return drive || Path.StartsWith('/') ? GoldenInputKind.Absolute : GoldenInputKind.GameFile;
        }
    }

    /// <summary>True when the file was too big to hash and only its size is recorded.</summary>
    public bool SizeOnly => Sha256 is null;

    /// <summary>
    /// The full path of the input on this machine: under <paramref name="gameDirectory"/> for a game file, under
    /// <paramref name="repositoryRoot"/> for a <c>project:</c> file, as written for an absolute one; null when the base folder is
    /// not known.
    /// </summary>
    public string? Resolve(string? gameDirectory, string? repositoryRoot)
    {
        switch (Kind)
        {
            case GoldenInputKind.GameFile:
                return gameDirectory is null ? null : System.IO.Path.Combine(gameDirectory, Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            case GoldenInputKind.ProjectFile:
                return repositoryRoot is null
                    ? null
                    : System.IO.Path.Combine(repositoryRoot, Path[ProjectPrefix.Length..].Replace('/', System.IO.Path.DirectorySeparatorChar));
            default:
                return Path;
        }
    }
}

/// <summary>One archive entry the export read, with the SHA-1 of its stored bytes.</summary>
/// <param name="Archive">Archive base name (<c>allpac0</c>, <c>voice</c>).</param>
/// <param name="Index">Entry index in its HED table.</param>
/// <param name="Name">Entry name from the NAM table, or null.</param>
/// <param name="Offset">Byte offset of the entry in the .mrg file.</param>
/// <param name="Size">Stored size in bytes.</param>
/// <param name="Sha1">SHA-1 of the stored bytes.</param>
public sealed record GoldenInputEntry(string Archive, int Index, string? Name, long Offset, long Size, string Sha1);

/// <summary>A hard cross-check the exporter made between the exported data and the documents.</summary>
/// <param name="Name">Check name (prefixed with the part name in an area manifest).</param>
/// <param name="Expected">The documented value.</param>
/// <param name="Actual">The exported value.</param>
/// <param name="Ok">Whether they agreed.</param>
/// <param name="Source">The document or statement the expectation comes from.</param>
public sealed record GoldenCheck(string Name, JsonElement Expected, JsonElement Actual, bool Ok, string Source);

/// <summary>A discrepancy the export found that is not an error of the exported data.</summary>
/// <param name="Name">Finding name.</param>
/// <param name="Detail">What differs.</param>
/// <param name="Data">Supporting values (any JSON).</param>
public sealed record GoldenFinding(string Name, string Detail, JsonElement Data);

/// <summary>
/// A golden manifest (<c>samples/golden/&lt;area&gt;/manifest.json</c> or <c>&lt;part&gt;/manifest.json</c>), schema
/// <c>tsuki-golden-manifest/1</c>, as <c>tools/oracle/manifest.py</c> writes it. The reader is strict: a missing required
/// property or a wrong schema is a <see cref="GoldenFormatException"/>.
/// </summary>
public sealed class GoldenManifest
{
    /// <summary>The schema name every manifest carries.</summary>
    public const string SchemaName = "tsuki-golden-manifest/1";

    /// <summary>File name of a manifest.</summary>
    public const string FileName = "manifest.json";

    /// <summary>Sanity limit for a manifest file (the largest real one, the slice manifest, is about 114 kB).</summary>
    private const int MaxManifestBytes = 64 * 1024 * 1024;

    private readonly Dictionary<string, GoldenFileRecord> _byPath;

    private GoldenManifest(
        string area,
        string description,
        string? createdUtc,
        GoldenGenerator? generator,
        GoldenGame game,
        ImmutableSortedDictionary<string, string> conventions,
        ImmutableSortedDictionary<string, GoldenPart> parts,
        ImmutableArray<GoldenFileRecord> files,
        ImmutableArray<GoldenInput> inputs,
        ImmutableArray<GoldenInputEntry> inputEntries,
        ImmutableSortedDictionary<string, JsonElement> counts,
        ImmutableArray<GoldenCheck> checks,
        ImmutableArray<GoldenFinding> findings)
    {
        Area = area;
        Description = description;
        CreatedUtc = createdUtc;
        Generator = generator;
        Game = game;
        Conventions = conventions;
        Parts = parts;
        Files = files;
        Inputs = inputs;
        InputEntries = inputEntries;
        Counts = counts;
        Checks = checks;
        Findings = findings;
        _byPath = new Dictionary<string, GoldenFileRecord>(files.Length, StringComparer.Ordinal);
        foreach (GoldenFileRecord f in files)
        {
            _byPath.TryAdd(f.Path, f);
        }
    }

    /// <summary>The area name (<c>slice</c>), or <c>slice/eboot</c> in a part manifest.</summary>
    public string Area { get; }

    /// <summary>What the area holds.</summary>
    public string Description { get; }

    /// <summary>UTC time the manifest was written, as the exporter formatted it.</summary>
    public string? CreatedUtc { get; }

    /// <summary>The generator block, or null if the manifest has none.</summary>
    public GoldenGenerator? Generator { get; }

    /// <summary>The game folder and eboot identity the export used.</summary>
    public GoldenGame Game { get; }

    /// <summary>Description of every file format used (<c>json</c>, <c>bytes</c>, <c>rgba8-tiles</c>, <c>a8-pages</c>, <c>f32le</c>).</summary>
    public ImmutableSortedDictionary<string, string> Conventions { get; }

    /// <summary>The parts of an area manifest, by name; empty in a part manifest.</summary>
    public ImmutableSortedDictionary<string, GoldenPart> Parts { get; }

    /// <summary>Every exported file, sorted by path.</summary>
    public ImmutableArray<GoldenFileRecord> Files { get; }

    /// <summary>Every game or repository file the export read.</summary>
    public ImmutableArray<GoldenInput> Inputs { get; }

    /// <summary>Every archive entry the export read.</summary>
    public ImmutableArray<GoldenInputEntry> InputEntries { get; }

    /// <summary>Counters of the export (an area manifest prefixes them with the part name, <c>audio.voices</c>).</summary>
    public ImmutableSortedDictionary<string, JsonElement> Counts { get; }

    /// <summary>The exporter's hard cross-checks.</summary>
    public ImmutableArray<GoldenCheck> Checks { get; }

    /// <summary>Discrepancies that are not errors of the data.</summary>
    public ImmutableArray<GoldenFinding> Findings { get; }

    /// <summary>The record of <paramref name="path"/>, or null if the manifest does not list it.</summary>
    public GoldenFileRecord? Find(string path) => _byPath.GetValueOrDefault(path);

    /// <summary>True if the manifest lists <paramref name="path"/>.</summary>
    public bool Contains(string path) => _byPath.ContainsKey(path);

    /// <summary>Reads and parses a manifest file.</summary>
    /// <exception cref="GoldenFormatException">The file is not a manifest of schema <see cref="SchemaName"/>.</exception>
    public static GoldenManifest Load(string path)
    {
        // A positional read through a handle, like every other file access of Tsuki.Data (ROADMAP M1 acceptance 8 bans the
        // whole-file convenience readers; a manifest is small, but the rule is checked by text and holds for every file).
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        long length = RandomAccess.GetLength(handle);
        if (length > MaxManifestBytes)
        {
            throw new GoldenFormatException($"{path}: {length} bytes is larger than any manifest ({MaxManifestBytes})");
        }

        byte[] buffer = new byte[(int)length];
        for (int done = 0; done < buffer.Length;)
        {
            int n = RandomAccess.Read(handle, buffer.AsSpan(done), done);
            if (n == 0)
            {
                throw new GoldenFormatException($"{path}: the file ended after {done} of {length} bytes");
            }

            done += n;
        }

        return Parse(buffer, path);
    }

    /// <summary>Parses manifest JSON. <paramref name="source"/> names it in error messages.</summary>
    /// <exception cref="GoldenFormatException">The text is not a manifest of schema <see cref="SchemaName"/>.</exception>
    public static GoldenManifest Parse(ReadOnlyMemory<byte> utf8, string source)
    {
        using JsonDocument doc = GoldenJson.Parse(utf8, source);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new GoldenFormatException($"{source}: not a JSON object");
        }

        string schema = root.Str("schema", source);
        if (!string.Equals(schema, SchemaName, StringComparison.Ordinal))
        {
            throw new GoldenFormatException($"{source}: schema \"{schema}\", expected \"{SchemaName}\"");
        }

        string area = root.Str("area", source);
        JsonElement files = root.Arr("files", source);
        JsonElement inputs = root.Arr("inputs", source);
        JsonElement entries = root.Arr("input_entries", source);
        JsonElement counts = root.Obj("counts", source);
        JsonElement checks = root.Arr("checks", source);

        var fileBuilder = ImmutableArray.CreateBuilder<GoldenFileRecord>(files.GetArrayLength());
        foreach (JsonElement f in files.EnumerateArray())
        {
            fileBuilder.Add(ReadFile(f, source));
        }

        var inputBuilder = ImmutableArray.CreateBuilder<GoldenInput>(inputs.GetArrayLength());
        foreach (JsonElement i in inputs.EnumerateArray())
        {
            string hash = i.Str("hash", source);
            if (hash is not ("sha256" or "size-only"))
            {
                throw new GoldenFormatException($"{source}: input hash kind \"{hash}\" is neither sha256 nor size-only");
            }

            inputBuilder.Add(new GoldenInput(i.Str("path", source), i.Long("size", source), i.StrOrNull("sha256", source), hash));
        }

        var entryBuilder = ImmutableArray.CreateBuilder<GoldenInputEntry>(entries.GetArrayLength());
        foreach (JsonElement e in entries.EnumerateArray())
        {
            entryBuilder.Add(new GoldenInputEntry(
                e.Str("archive", source), e.Int("index", source), e.StrOrNull("name", source),
                e.Long("offset", source), e.Long("size", source), e.Str("sha1", source)));
        }

        var checkBuilder = ImmutableArray.CreateBuilder<GoldenCheck>(checks.GetArrayLength());
        foreach (JsonElement c in checks.EnumerateArray())
        {
            checkBuilder.Add(new GoldenCheck(
                c.Str("name", source), c.Prop("expected", source).Clone(), c.Prop("actual", source).Clone(),
                c.Bool("ok", source), c.Str("source", source)));
        }

        var findingBuilder = ImmutableArray.CreateBuilder<GoldenFinding>();
        if (root.TryProp("findings", out JsonElement findings))
        {
            foreach (JsonElement f in findings.EnumerateArray())
            {
                findingBuilder.Add(new GoldenFinding(
                    f.Str("name", source), f.Str("detail", source), f.TryProp("data", out JsonElement d) ? d.Clone() : default));
            }
        }

        var countBuilder = ImmutableSortedDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty p in counts.EnumerateObject())
        {
            countBuilder[p.Name] = p.Value.Clone();
        }

        var conventions = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (root.TryProp("conventions", out JsonElement conv) && conv.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in conv.EnumerateObject())
            {
                conventions[p.Name] = p.Value.GetString() ?? string.Empty;
            }
        }

        var parts = ImmutableSortedDictionary.CreateBuilder<string, GoldenPart>(StringComparer.Ordinal);
        if (root.TryProp("parts", out JsonElement partsElement))
        {
            foreach (JsonProperty p in partsElement.EnumerateObject())
            {
                JsonElement v = p.Value;
                parts[p.Name] = new GoldenPart(
                    p.Name, v.Str("manifest", source), v.StrOrNull("description", source) ?? string.Empty,
                    v.Int("files", source), v.Long("bytes", source), v.StrOrNull("created_utc", source));
            }
        }

        return new GoldenManifest(
            area,
            root.StrOrNull("description", source) ?? string.Empty,
            root.StrOrNull("created_utc", source),
            root.TryProp("generator", out JsonElement gen) ? ReadGenerator(gen, source) : null,
            root.TryProp("game", out JsonElement game) ? ReadGame(game, source) : new GoldenGame(null, null, null),
            conventions.ToImmutable(),
            parts.ToImmutable(),
            fileBuilder.MoveToImmutable(),
            inputBuilder.MoveToImmutable(),
            entryBuilder.MoveToImmutable(),
            countBuilder.ToImmutable(),
            checkBuilder.MoveToImmutable(),
            findingBuilder.ToImmutable());
    }

    private static GoldenFileRecord ReadFile(JsonElement f, string source)
    {
        string path = f.Str("path", source);
        string where = $"{source} (file {path})";
        string rel = path;
        if (rel.Length == 0 || rel.StartsWith('/') || rel.Contains('\\', StringComparison.Ordinal) || rel.Split('/').Contains(".."))
        {
            throw new GoldenFormatException($"{where}: unsafe path");
        }

        return new GoldenFileRecord(
            path,
            f.Str("part", where),
            f.Long("size", where),
            f.Str("sha1", where),
            f.Str("format", where),
            ReadProducer(f.Obj("produced_by", where), where),
            f.TryProp("meta", out JsonElement meta) ? meta.Clone() : default);
    }

    private static GoldenProducer ReadProducer(JsonElement p, string source)
    {
        JsonElement args = p.Obj("args", source).Clone();
        var uses = ImmutableArray.CreateBuilder<string>();
        if (p.TryProp("uses", out JsonElement u) && u.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement s in u.EnumerateArray())
            {
                uses.Add(s.GetString() ?? string.Empty);
            }
        }

        return new GoldenProducer(p.Str("module", source), p.Str("function", source), args, uses.ToImmutable());
    }

    private static GoldenGenerator ReadGenerator(JsonElement g, string source)
    {
        var tools = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (g.TryProp("tool_sources", out JsonElement ts) && ts.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in ts.EnumerateObject())
            {
                tools[p.Name] = p.Value.GetString() ?? string.Empty;
            }
        }

        return new GoldenGenerator(
            ReadProducer(g, source),
            g.StrOrNull("python", source),
            g.StrOrNull("numpy", source),
            g.StrOrNull("platform", source),
            g.StrOrNull("ffmpeg", source),
            g.StrOrNull("tool_commit", source),
            g.StrOrNull("tool_digest", source),
            tools.ToImmutable());
    }

    private static GoldenGame ReadGame(JsonElement g, string source)
    {
        long? size = null;
        string? sha = null;
        if (g.TryProp("eboot", out JsonElement e) && e.ValueKind == JsonValueKind.Object)
        {
            size = e.Long("size", source);
            sha = e.StrOrNull("sha256", source);
        }

        return new GoldenGame(g.StrOrNull("dir", source), size, sha);
    }
}
#endif
