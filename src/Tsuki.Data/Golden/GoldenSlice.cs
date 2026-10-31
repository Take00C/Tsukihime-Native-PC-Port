#if TSUKI_DEV
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Tsuki.Data.Golden;

/// <summary>How much of the manifest <see cref="GoldenSlice"/> re-checks when it first opens a file.</summary>
public enum GoldenVerifyMode
{
    /// <summary>Nothing: the manifest only says which files exist.</summary>
    None,

    /// <summary>The file size must equal the manifest's (the default; costs one stat per file).</summary>
    Size,

    /// <summary>The SHA-1 of the whole file must equal the manifest's (reads every file once; the slice is 489 MB).</summary>
    Sha1,
}

/// <summary>
/// The golden slice folder (<c>samples/golden/slice</c>, area <c>slice</c>, ARCHITECTURE 10.1): its manifest and read access to
/// the files the manifest lists. Only listed files can be read; each is checked against the manifest on first use according to
/// <see cref="VerifyMode"/>. Reads are positional and safe from several threads. Dev builds only.
/// </summary>
public sealed class GoldenSlice : IDisposable
{
    /// <summary>The area name of the slice export.</summary>
    public const string AreaName = "slice";

    /// <summary>Environment variable that points at the golden root (the folder that holds <c>slice</c>).</summary>
    public const string EnvironmentVariable = "TSUKI_GOLDEN_DIR";

    private const int HashChunk = 1 << 20;

    private readonly ConcurrentDictionary<string, Lazy<SafeFileHandle>> _handles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<GoldenManifest>> _partManifests = new(StringComparer.Ordinal);
    private bool _disposed;

    private GoldenSlice(string directory, GoldenManifest manifest, GoldenVerifyMode verify)
    {
        Directory = directory;
        Manifest = manifest;
        VerifyMode = verify;
        RepositoryRoot = FindRepositoryRoot(directory) ?? FindRepositoryRoot(AppContext.BaseDirectory);
    }

    /// <summary>The slice folder, absolute.</summary>
    public string Directory { get; }

    /// <summary>The area manifest.</summary>
    public GoldenManifest Manifest { get; }

    /// <summary>How files are checked when first opened.</summary>
    public GoldenVerifyMode VerifyMode { get; }

    /// <summary>The repository root (the folder above <c>src/Tsuki.slnx</c>), or null when none is found.</summary>
    public string? RepositoryRoot { get; }

    /// <summary>
    /// Finds the slice folder: <paramref name="hint"/> if given, else <c>%TSUKI_GOLDEN_DIR%\slice</c>, else
    /// <c>samples/golden/slice</c> in the nearest parent folder of the program that has one. Null when none holds a manifest.
    /// </summary>
    public static string? FindDirectory(string? hint = null) =>
        FindDirectory(hint, Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// <see cref="FindDirectory(string?)"/> with the golden root given explicitly instead of read from
    /// <see cref="EnvironmentVariable"/>. A <paramref name="hint"/> or a <paramref name="goldenRoot"/> that holds no manifest
    /// gives null; there is no fall-through to the next rule.
    /// </summary>
    public static string? FindDirectory(string? hint, string? goldenRoot)
    {
        if (!string.IsNullOrWhiteSpace(hint))
        {
            return IsSlice(hint) ? System.IO.Path.GetFullPath(hint) : null;
        }

        if (!string.IsNullOrWhiteSpace(goldenRoot))
        {
            string candidate = System.IO.Path.Combine(goldenRoot, AreaName);
            return IsSlice(candidate) ? System.IO.Path.GetFullPath(candidate) : null;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = System.IO.Path.Combine(dir.FullName, "samples", "golden", AreaName);
            if (IsSlice(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Opens the slice folder and reads its manifest.</summary>
    /// <param name="directory">The slice folder; null for <see cref="FindDirectory(string?)"/>.</param>
    /// <param name="verify">How files are checked on first use.</param>
    /// <exception cref="DirectoryNotFoundException">No slice folder was found.</exception>
    /// <exception cref="GoldenFormatException">The manifest does not follow the schema.</exception>
    public static GoldenSlice Open(string? directory = null, GoldenVerifyMode verify = GoldenVerifyMode.Size)
    {
        string dir = FindDirectory(directory)
            ?? throw new DirectoryNotFoundException(
                "golden slice not found (no samples/golden/slice/manifest.json); create it with: "
                + "$env:PYTHONPATH='D:\\TsukihimePort\\tools'; .venv\\Scripts\\python.exe -m oracle.run slice");
        GoldenManifest manifest = GoldenManifest.Load(System.IO.Path.Combine(dir, GoldenManifest.FileName));
        if (!string.Equals(manifest.Area, AreaName, StringComparison.Ordinal))
        {
            throw new GoldenFormatException($"{dir}: manifest is for area \"{manifest.Area}\", expected \"{AreaName}\"");
        }

        return new GoldenSlice(dir, manifest, verify);
    }

    /// <summary>The manifest of one part (<c>pictures/manifest.json</c>), read on first use.</summary>
    /// <exception cref="GoldenSliceMissingException">The area has no such part.</exception>
    public GoldenManifest PartManifest(string part)
    {
        if (!Manifest.Parts.TryGetValue(part, out GoldenPart? info))
        {
            throw new GoldenSliceMissingException($"part \"{part}\"", string.Join(", ", Manifest.Parts.Keys));
        }

        return _partManifests.GetOrAdd(
            part,
            _ => new Lazy<GoldenManifest>(() => GoldenManifest.Load(FullPath(info.Manifest)), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>True if the area manifest lists <paramref name="path"/>.</summary>
    public bool Contains(string path) => Manifest.Contains(path);

    /// <summary>The manifest record of <paramref name="path"/>.</summary>
    /// <exception cref="GoldenSliceMissingException">The manifest does not list it.</exception>
    public GoldenFileRecord Record(string path) =>
        Manifest.Find(path) ?? throw new GoldenSliceMissingException($"file {path}");

    /// <summary>The full path of a file of the area (it need not be listed).</summary>
    public string FullPath(string path) =>
        System.IO.Path.Combine(Directory, path.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes at <paramref name="offset"/> of a listed file.</summary>
    /// <exception cref="GoldenSliceMissingException">The manifest does not list the file.</exception>
    /// <exception cref="EndOfStreamException">The file is shorter than the range.</exception>
    public void ReadAt(string path, long offset, Span<byte> buffer)
    {
        SafeFileHandle handle = Handle(path);
        int total = 0;
        while (total < buffer.Length)
        {
            int n = RandomAccess.Read(handle, buffer[total..], offset + total);
            if (n == 0)
            {
                throw new EndOfStreamException($"{path}: {buffer.Length} bytes at {offset} reach past the end of the file");
            }

            total += n;
        }
    }

    /// <summary>Reads <paramref name="count"/> bytes at <paramref name="offset"/> of a listed file.</summary>
    public byte[] ReadRange(string path, long offset, int count)
    {
        byte[] data = GC.AllocateUninitializedArray<byte>(count);
        ReadAt(path, offset, data);
        return data;
    }

    /// <summary>Reads a whole listed file.</summary>
    public byte[] ReadAll(string path)
    {
        GoldenFileRecord record = Record(path);
        if (record.Size > Array.MaxLength)
        {
            throw new IOException($"{path} is {record.Size} bytes; read it by range");
        }

        return ReadRange(path, 0, (int)record.Size);
    }

    /// <summary>Reads and parses a listed JSON file. The caller disposes the document.</summary>
    public JsonDocument ReadJson(string path) => GoldenJson.Parse(ReadAll(path), path);

    /// <summary>
    /// Checks the whole export against its manifest, like <c>python -m oracle.run slice --verify</c>
    /// (<c>manifest.verify_area</c>): every listed file exists with its size and SHA-1, every part manifest lists the same files as
    /// the area manifest, no unlisted file is present, and every recorded cross-check passed. Empty means consistent.
    /// </summary>
    /// <param name="hashFiles">False to compare sizes only.</param>
    public IReadOnlyList<string> Verify(bool hashFiles = true)
    {
        var problems = new List<string>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (GoldenFileRecord f in Manifest.Files)
        {
            if (!listed.Add(f.Path))
            {
                problems.Add($"{f.Path}: listed twice");
                continue;
            }

            string full = FullPath(f.Path);
            if (!File.Exists(full))
            {
                problems.Add($"{f.Path}: missing");
                continue;
            }

            long size = new FileInfo(full).Length;
            if (size != f.Size)
            {
                problems.Add($"{f.Path}: size {size} != manifest {f.Size}");
                continue;
            }

            if (hashFiles)
            {
                string digest = HashFile(full);
                if (!string.Equals(digest, f.Sha1, StringComparison.Ordinal))
                {
                    problems.Add($"{f.Path}: SHA-1 {digest} != manifest {f.Sha1}");
                }
            }
        }

        var manifests = new HashSet<string>(StringComparer.Ordinal) { GoldenManifest.FileName };
        foreach (GoldenPart part in Manifest.Parts.Values)
        {
            manifests.Add(part.Manifest);
            string partPath = FullPath(part.Manifest);
            if (!File.Exists(partPath))
            {
                problems.Add($"part {part.Name}: {part.Manifest} missing");
                continue;
            }

            GoldenManifest pm;
            try
            {
                pm = GoldenManifest.Load(partPath);
            }
            catch (GoldenFormatException e)
            {
                problems.Add(e.Message);
                continue;
            }

            string[] mine = [.. Manifest.Files.Where(f => f.Part == part.Name).Select(f => $"{f.Path}\0{f.Sha1}").Order(StringComparer.Ordinal)];
            string[] theirs = [.. pm.Files.Select(f => $"{f.Path}\0{f.Sha1}").Order(StringComparer.Ordinal)];
            if (!mine.AsSpan().SequenceEqual(theirs))
            {
                problems.Add($"part {part.Name}: part manifest lists different files than the area manifest");
            }
        }

        foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories))
        {
            string rel = System.IO.Path.GetRelativePath(Directory, file).Replace('\\', '/');
            if (!listed.Contains(rel) && !manifests.Contains(rel))
            {
                problems.Add($"{rel}: not listed in the manifest");
            }
        }

        foreach (GoldenCheck c in Manifest.Checks.Where(c => !c.Ok))
        {
            problems.Add($"check {c.Name} failed: expected {c.Expected.GetRawText()}, got {c.Actual.GetRawText()}");
        }

        return problems;
    }

    /// <summary>Closes the open files.</summary>
    public void Dispose()
    {
        _disposed = true;
        foreach (Lazy<SafeFileHandle> h in _handles.Values)
        {
            if (h.IsValueCreated)
            {
                h.Value.Dispose();
            }
        }

        _handles.Clear();
    }

    private static bool IsSlice(string directory) =>
        File.Exists(System.IO.Path.Combine(directory, GoldenManifest.FileName));

    private static string? FindRepositoryRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "src", "Tsuki.slnx")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    private SafeFileHandle Handle(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        GoldenFileRecord record = Record(path);
        return _handles.GetOrAdd(path, _ => new Lazy<SafeFileHandle>(() => OpenChecked(record), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private SafeFileHandle OpenChecked(GoldenFileRecord record)
    {
        string full = FullPath(record.Path);
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        }
        catch (FileNotFoundException e)
        {
            throw new GoldenFormatException($"{record.Path}: listed in the manifest but missing from {Directory}", e);
        }

        try
        {
            if (VerifyMode != GoldenVerifyMode.None)
            {
                long length = RandomAccess.GetLength(handle);
                if (length != record.Size)
                {
                    throw new GoldenFormatException($"{record.Path}: size {length} != manifest {record.Size}");
                }
            }

            if (VerifyMode == GoldenVerifyMode.Sha1)
            {
                string digest = HashHandle(handle, record.Size);
                if (!string.Equals(digest, record.Sha1, StringComparison.Ordinal))
                {
                    throw new GoldenFormatException($"{record.Path}: SHA-1 {digest} != manifest {record.Sha1}");
                }
            }
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        return handle;
    }

    private static string HashFile(string full)
    {
        using SafeFileHandle handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        return HashHandle(handle, RandomAccess.GetLength(handle));
    }

    private static string HashHandle(SafeFileHandle handle, long length)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        byte[] buffer = new byte[(int)Math.Min(length, HashChunk) + 1];
        for (long pos = 0; pos < length;)
        {
            int want = (int)Math.Min(length - pos, buffer.Length);
            int n = RandomAccess.Read(handle, buffer.AsSpan(0, want), pos);
            if (n == 0)
            {
                break;
            }

            sha.AppendData(buffer, 0, n);
            pos += n;
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
#endif
