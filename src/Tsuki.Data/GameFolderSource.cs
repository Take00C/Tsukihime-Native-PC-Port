using System.Collections.Concurrent;
using System.Security.Cryptography;
using Tsuki.Data.Audio;
using Tsuki.Data.Catalog;
using Tsuki.Data.Compression;
using Tsuki.Data.Containers;
using Tsuki.Data.Eboot;
using Tsuki.Data.Fonts;
using Tsuki.Data.Images;
using Tsuki.Data.IO;
using Tsuki.Data.Scripts;
using Tsuki.Data.Text;
using Tsuki.Data.Verification;
using Tsuki.Engine.Contracts;

namespace Tsuki.Data;

/// <summary>
/// <see cref="IGameData"/> over the user's CUSA47999 folder, read with the C# decoders (ARCHITECTURE 3.3). It ships. The folder is only
/// ever opened for reading, and nothing large is read into memory: the archives (up to 24 GB) are memory-mapped and an entry is a window
/// onto the mapping; <c>allscr.mrg</c>, <c>script_text.mrg</c> and the font files are read by section with positional reads.
/// </summary>
/// <remarks>
/// <para>
/// Opening reads <c>eboot.bin</c> (12.7 MB), the five NAM tables of the image archives and nothing else, about 25 ms. Everything else is
/// opened on first use: an archive when its first entry is asked for, the script text when the first line is, the glyph lookups when the
/// layout asks for them. All members are safe to call from several threads (ARCHITECTURE 3.4); every call decodes afresh and returns a new
/// object (no picture, clip or script is cached here: the texture cache and the engine own that).
/// </para>
/// <para>
/// It returns the same values as <c>GoldenSource</c> on everything the golden slice holds (a test in Tsuki.Tests.Data checks it). Audio
/// needs an <see cref="IAudioDecoder"/> (FFmpeg and the HE-VAG port live in Tsuki.Media, which Tsuki.Data must not reference), handed in
/// when the source is created; without one <see cref="Audio"/> throws <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// Package K25: the folder may be any layout <see cref="GameLayoutResolver"/> understands (the game itself, a parent folder, the base game with the update folder
/// beside it, whose files take precedence). The eboot is identified (<see cref="Identity"/>); the verified build's tables are read at the documented addresses, any
/// other eboot's are located by content (<see cref="EbootLocator"/>) and the open fails with an <see cref="UnsupportedBuildException"/> that names the missing tables
/// when one cannot be found.
/// </para>
/// </remarks>
public sealed class GameFolderSource : IGameData, IDisposable
{
    private static readonly string[] ImageArchiveNames = ["allpac0", "allpac1", "allpac1_2", "allpac2", "allpac3", "allpac4"];

    private readonly IGameFiles _files;
    private readonly IAudioDecoder? _audio;
    private readonly int _decodeParallelism;
    private readonly ConcurrentDictionary<string, Lazy<MrgArchive>> _archives = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Language, Lazy<FontArchive>> _fonts = new();
    private readonly Lazy<AllscrArchive> _allscr;
    private readonly Lazy<ScriptText> _text;
    private readonly Lazy<SeNames> _seNames;
    private readonly UseGate _uses = new();
    private volatile bool _disposed;

    private GameFolderSource(GameLayout layout, IGameFiles files, BuildInfo build, BuildIdentity identity, LocateResult? located, GameEbootTables tables, IAudioDecoder? audio, int decodeParallelism)
    {
        Layout = layout;
        _files = files;
        Build = build;
        Identity = identity;
        TableLocations = located;
        Tables = tables;
        _audio = audio;
        _decodeParallelism = decodeParallelism;
        _allscr = new(() => AllscrArchive.Open(files), LazyThreadSafetyMode.ExecutionAndPublication);
        _text = new(() => ScriptText.Open(files), LazyThreadSafetyMode.ExecutionAndPublication);
        _seNames = new(() => SeNames.Open(files), LazyThreadSafetyMode.ExecutionAndPublication);
        _paramSfo = new(() => identity.Sfo, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The folder that was opened: the base game folder (the update folder, when there is one, is <see cref="Layout"/>'s).</summary>
    public string RootDirectory => Layout.BaseFolder!;

    /// <summary>How the dump is laid out on disk (package K25).</summary>
    public GameLayout Layout { get; }

    /// <summary>What the dump is: title id, version, the eboot's hash against the known builds, the languages the data carries (package K25).</summary>
    public BuildIdentity Identity { get; }

    /// <summary>Where the eboot's tables were found when they had to be located by content; null for the verified build (package K25).</summary>
    public LocateResult? TableLocations { get; }

    /// <summary>The languages the data supports (<see cref="BuildIdentity.AvailableLanguages"/>).</summary>
    public IReadOnlyList<Language> AvailableLanguages => Identity.AvailableLanguages;

    /// <summary>The <c>sce_sys/param.sfo</c> of the dump (the update's when there is one): title id, version, the localized titles; null when missing or unreadable (package K20).</summary>
    public ParamSfo? ParamSfo => _paramSfo.Value;

    private readonly Lazy<ParamSfo?> _paramSfo;

    /// <summary>The game's title in <paramref name="language"/>, from <see cref="ParamSfo"/> with <see cref="GameTitles"/>' fallback.</summary>
    public string Title(Language language) => GameTitles.Of(ParamSfo, language);

    /// <summary>Identity of the copy: the folder, and the size and SHA-256 of its eboot.bin (RR 3.3).</summary>
    public BuildInfo Build { get; }

    /// <summary>The eboot tables with their typed readers (the same object as <see cref="Eboot"/>).</summary>
    public GameEbootTables Tables { get; }

    /// <inheritdoc />
    public IEbootTables Eboot => Tables;

    /// <inheritdoc />
    IReadOnlyList<Language> IGameData.AvailableLanguages => AvailableLanguages;

    /// <summary>The catalog: image table, picture names, NAM search, movie table, UI tables (A2).</summary>
    public GameCatalog Catalog => Tables.Catalog;

    /// <summary><c>allscr.mrg</c>: the script names, label tables and script sections (opened on first use).</summary>
    public AllscrArchive Scripts => _allscr.Value;

    /// <summary><c>script_text.mrg</c>: the text tables of the five languages (opened on first use).</summary>
    public ScriptText Text => _text.Value;

    /// <summary><c>se_nam.mrg</c>: the sound-effect names (opened on first use).</summary>
    public SeNames SoundEffectNames => _seNames.Value;

    /// <summary>Opens the game folder (any layout <see cref="GameLayoutResolver.Resolve"/> understands).</summary>
    /// <param name="folder">The CUSA47999 folder (holding <c>eboot.bin</c> and <c>image/</c>), or the folder above it, or the base game with the update beside it.</param>
    /// <param name="audioDecoder">Decodes audio entries (<c>Tsuki.Media.Audio.MediaAudioDecoder</c>); null if the caller never asks for audio.</param>
    /// <param name="decodeParallelism">Threads one <c>Picture</c> call may use for the tiles of a large picture (1 = decode on the calling thread).</param>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="FileNotFoundException">eboot.bin or a NAM table is missing.</exception>
    /// <exception cref="GameLayoutException">The path is a .pkg file, an update folder alone, or holds no game.</exception>
    /// <exception cref="UnsupportedBuildException">The eboot is not a known build and a table could not be located.</exception>
    /// <exception cref="EbootFormatException">The eboot does not hold the tables where the layout says.</exception>
    public static GameFolderSource Open(string folder, IAudioDecoder? audioDecoder = null, int decodeParallelism = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        GameLayout layout = GameLayoutResolver.Resolve(folder);
        if (layout.Kind == GameLayoutKind.Missing)
        {
            throw new DirectoryNotFoundException($"game folder {folder} does not exist");
        }

        if (layout.Kind == GameLayoutKind.NoGame && Directory.Exists(folder) && !File.Exists(Path.Combine(folder, "eboot.bin")) && !Directory.EnumerateDirectories(folder).Any())
        {
            // an empty folder: the message the callers of old expect (the file that is missing)
            throw new FileNotFoundException($"eboot.bin not found in {folder}", "eboot.bin");
        }

        return Open(layout, audioDecoder, decodeParallelism);
    }

    /// <summary>Opens a resolved layout (see <see cref="Open(string, IAudioDecoder?, int)"/>).</summary>
    public static GameFolderSource Open(GameLayout layout, IAudioDecoder? audioDecoder = null, int decodeParallelism = 1)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentOutOfRangeException.ThrowIfLessThan(decodeParallelism, 1);
        if (!layout.IsGame)
        {
            throw new GameLayoutException(layout);
        }

        IGameFiles files = layout.OpenFiles();
        const string eboot = "eboot.bin";
        byte[] ebootBytes = files.ReadAll(eboot);
        BuildIdentity identity = BuildIdentity.Identify(files, ebootBytes);
        EbootReader reader = EbootReader.Parse(ebootBytes);
        LocateResult? located = null;
        if (identity.Match != BuildMatch.Verified || !reader.Layout.IsAnalysed || identity.Known != KnownBuilds.Verified)
        {
            // another eboot: every table by content, or a clear refusal (package K25)
            located = EbootLocator.Locate(reader);
            if (!located.Complete)
            {
                throw new UnsupportedBuildException(identity, located);
            }

            reader = reader.WithLayout(located.Layout!);
        }

        var nams = new Dictionary<ImageArchive, NamTable>();
        foreach ((ImageArchive archive, string nam) in GameCatalog.SearchedArchives)
        {
            nams[archive] = NamTable.ParseNam32(files.ReadAll(nam));
        }

        var catalog = GameCatalog.Create(reader, nams);
        var build = new BuildInfo(layout.BaseFolder!, ebootBytes.Length, identity.EbootSha256);
        return new GameFolderSource(layout, files, build, identity, located, new GameEbootTables(reader, catalog), audioDecoder, decodeParallelism);
    }

    // -- scripts ------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public int ScriptCount => Scripts.Count;

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="k"/> is not a script number.</exception>
    public ScriptImage Script(int k)
    {
        using UseGate.Use use = _uses.Enter(this);
        return Scripts.Script(k);
    }

    // -- text ---------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public int TextLineCount => Text.Count;

    /// <inheritdoc />
    /// <remarks>The bytes the engine's copy routine (VA 0x75F80) yields: no CR LF, at most 0x2FF bytes, markup intact, <c>$</c> references not expanded (text.md 2, 4).</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The language or the id is out of range (ids are 0..43,968).</exception>
    public ReadOnlyMemory<byte> TextLine(Language lang, int id)
    {
        ThrowIfDisposed();
        _ = TextRules.Code(lang);
        ScriptText text = Text;
        if (id < 0 || id >= text.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, $"text line ids are 0..{text.Count - 1}");
        }

        return text.EngineLine(lang, id);
    }

    // -- fonts --------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The language is unknown or the page is not 0..64.</exception>
    public FontPage FontPage(Language lang, int page)
    {
        using UseGate.Use use = _uses.Enter(this);
        return FontFile(lang).ReadPage(page);
    }

    /// <summary>The opened <c>ft_&lt;lang&gt;.mrg</c> (the section table only is read at once; a page is decoded per call).</summary>
    public FontArchive FontFile(Language lang)
    {
        ThrowIfDisposed();
        _ = TextRules.Code(lang);
        return _fonts.GetOrAdd(lang, l => new Lazy<FontArchive>(() => FontArchive.Open(_files, l), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    // -- pictures -----------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The archive is not 0..5 or the entry is not in it.</exception>
    /// <exception cref="ImageFormatException">The entry is not an image (the allpac3 sheets are containers of images: use <see cref="UiSheet"/>).</exception>
    public Picture Picture(int archive, int entry)
    {
        using UseGate.Use use = _uses.Enter(this);
        return OpenImage(archive, entry).ToPicture(_decodeParallelism);
    }

    /// <summary>
    /// Parses the header and tile table of picture <paramref name="entry"/> of image archive <paramref name="archive"/> without decoding a
    /// pixel (the container is a window onto the archive mapping, valid until this source is disposed).
    /// </summary>
    public ImageMzp OpenImage(int archive, int entry)
    {
        ThrowIfDisposed();
        if ((uint)archive >= (uint)ImageArchiveNames.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(archive), archive, "image archive ids are 0..5 (allpac0 .. allpac4)");
        }

        MrgArchive a = Archive(ImageArchiveNames[archive]);
        if ((uint)entry >= (uint)a.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), entry, $"{ImageArchiveNames[archive]} has entries 0..{a.Count - 1}");
        }

        return ImageMzp.Open(a.Memory(entry));
    }

    /// <summary>The picture a catalog lookup found (<see cref="Catalog"/>), decoded like <see cref="Picture(int, int)"/>.</summary>
    public Picture Picture(ImageId id) => Picture((int)id.Archive, id.Index);

    // -- audio --------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The source was created without an <see cref="IAudioDecoder"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The bank is unknown or the entry is not in it.</exception>
    public AudioClip Audio(AudioBank bank, int index)
    {
        using UseGate.Use use = _uses.Enter(this);
        IAudioDecoder decoder = _audio ?? throw new InvalidOperationException(
            "GameFolderSource was opened without an IAudioDecoder; pass Tsuki.Media.Audio.MediaAudioDecoder to GameFolderSource.Open");
        return decoder.Decode(bank, index, AudioPayload(bank, index));
    }

    /// <summary>
    /// The bytes an <see cref="IAudioDecoder"/> takes for an entry: the RIFF/WAVE file of <c>bgm</c> and <c>vse</c>, the VAGp file of <c>vag</c>, and for
    /// the voice banks the RIFF file after the LV chunk and the MZX0 stream (docs/formats/audio.md 5.1, containers.md 6).
    /// </summary>
    public ReadOnlyMemory<byte> AudioPayload(AudioBank bank, int index)
    {
        ThrowIfDisposed();
        string name = BankFile(bank);
        MrgArchive archive = Archive(name);
        if ((uint)index >= (uint)archive.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"{name} has entries 0..{archive.Count - 1}");
        }

        return bank is AudioBank.Voice or AudioBank.Voice2
            ? Lv.Decompress(archive.SectorMemory(index).Span).Data
            : archive.Memory(index);
    }

    /// <summary>The number of entries of an audio bank.</summary>
    public int AudioCount(AudioBank bank)
    {
        ThrowIfDisposed();
        return Archive(BankFile(bank)).Count;
    }

    // -- movies -------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>The table's path (<c>/app0/image/movie/prologue.mp4</c>) placed under <see cref="RootDirectory"/>, as the file is on disk.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The index is not a record of the movie table (0..133).</exception>
    /// <exception cref="FileNotFoundException">The movie file is not in the folder.</exception>
    public string Movie(int index)
    {
        ThrowIfDisposed();
        IReadOnlyList<MovieRecord> movies = Tables.Movies;
        if ((uint)index >= (uint)movies.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"movie table records are 0..{movies.Count - 1}");
        }

        const string prefix = "/app0/";
        string path = movies[index].Path;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"movie {index}: path \"{path}\" does not start with {prefix}");
        }

        return _files.ResolveFile(path[prefix.Length..])
            ?? throw new FileNotFoundException($"movie {index} ({path}) is not in {_files.Description}", path);
    }

    // -- UI sheets ----------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// The sheet is an allpac3 archive (<see cref="UiSheetNames.FileName"/>, found by the engine's NAM search); its sections are image MZPs.
    /// A registration record may name a child past the end of its sheet, which the engine clamps (<see cref="UiTextureTables.ChildIndex"/>);
    /// this method does not.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The set or language is unknown, or <paramref name="child"/> is not a child of the sheet.</exception>
    /// <exception cref="FileNotFoundException">The sheet is not in allpac3.</exception>
    public Picture UiSheet(UiSet set, Language lang, int child)
    {
        using UseGate.Use use = _uses.Enter(this);
        return ImageMzp.Open(UiSheetContainer(set, lang).Child(CheckChild(set, lang, child))).ToPicture(_decodeParallelism);
    }

    /// <inheritdoc />
    /// <remarks><c>image/parts.mrg</c> holds image MZPs (the boot notices); null for an index past the end.</remarks>
    public Picture? Parts(int index)
    {
        using UseGate.Use use = _uses.Enter(this);
        MrgArchive archive = Archive("parts");
        return (uint)index < (uint)archive.Count ? ImageMzp.Open(archive.Memory(index)).ToPicture(_decodeParallelism) : null;
    }

    /// <inheritdoc />
    public (int Width, int Height) UiSheetSize(UiSet set, Language lang, int child)
    {
        using UseGate.Use use = _uses.Enter(this);
        ImageHeader h = OpenUiSheetImage(set, lang, child).Header;
        return (h.DisplayWidth, h.DisplayHeight);
    }

    /// <inheritdoc />
    public Picture UiSheetLayout(UiSet set, Language lang, int child)
    {
        using UseGate.Use use = _uses.Enter(this);
        return OpenUiSheetImage(set, lang, child).ToLayoutPicture();
    }

    /// <summary>Parses the header and tile table of one child of a UI sheet without decoding a pixel (see <see cref="UiSheet"/>).</summary>
    public ImageMzp OpenUiSheetImage(UiSet set, Language lang, int child)
    {
        ThrowIfDisposed();
        return ImageMzp.Open(UiSheetContainer(set, lang).Child(CheckChild(set, lang, child)));
    }

    /// <summary>The number of children of a UI sheet (a sheet of a language-free set ignores <paramref name="lang"/>).</summary>
    public int UiSheetChildCount(UiSet set, Language lang)
    {
        ThrowIfDisposed();
        return UiSheetContainer(set, lang).Count;
    }

    private int CheckChild(UiSet set, Language lang, int child)
    {
        int count = UiSheetContainer(set, lang).Count;
        return (uint)child < (uint)count
            ? child
            : throw new ArgumentOutOfRangeException(nameof(child), child, $"{UiSheetNames.FileName(set, lang)} has children 0..{count - 1}");
    }

    private MzpContainer UiSheetContainer(UiSet set, Language lang)
    {
        _ = TextRules.Code(lang);
        string file = UiSheetNames.FileName(set, lang);
        ImageId id = Catalog.NameIndex.Find(file)
            ?? throw new FileNotFoundException($"UI sheet {file} is not in any image archive", file);
        MrgArchive archive = Archive(ImageArchiveNames[(int)id.Archive]);
        return MzpContainer.Parse(archive.Memory(id.Index));
    }

    // -- archives -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The memory-mapped archive <c>image/&lt;name&gt;</c> (<c>allpac0</c> .. <c>allpac4</c>, <c>parts</c>, <c>bgm</c>, <c>vse</c>, <c>vag</c>, <c>voice</c>,
    /// <c>voice2</c>), opened on first use and kept until this source is disposed.
    /// </summary>
    /// <exception cref="FileNotFoundException">The archive is not in the folder.</exception>
    public MrgArchive Archive(string name)
    {
        ThrowIfDisposed();
        return _archives.GetOrAdd(name, n => new Lazy<MrgArchive>(() => OpenArchive(n), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private MrgArchive OpenArchive(string name)
    {
        string hed = $"image/{name}.hed";
        string path = _files.ResolveFile(hed)
            ?? throw new FileNotFoundException($"{hed} is not in {_files.Description}; the game folder is incomplete", hed);
        return MrgArchive.Open(path);
    }

    private static string BankFile(AudioBank bank) => bank switch
    {
        AudioBank.Bgm => "bgm",
        AudioBank.Se => "vse",
        AudioBank.Voice => "voice",
        AudioBank.Voice2 => "voice2",
        AudioBank.Vag => "vag",
        _ => throw new ArgumentOutOfRangeException(nameof(bank), bank, "not an audio bank"),
    };

    /// <summary>
    /// Drops a range of a mapping from the process's working set (address, length). Set by the shell, which can call the operating system (Tsuki.Data makes no
    /// P/Invoke); without it <see cref="TrimMappedPages"/> does nothing.
    /// </summary>
    public Action<nint, nuint>? PageTrimmer { get; set; }

    /// <summary>Archives smaller than this stay in the working set (the scripts and fonts are read all the time and cost nothing).</summary>
    public const long TrimmedArchiveBytes = 128L << 20;

    /// <summary>
    /// Drops the pages of the large archives (the image, audio and voice banks, 40 GB in all) from the process's working set (package K11). A page that a decode read
    /// stays in the working set until the system needs the memory, so over a long play the working set grew by everything the game had ever read; trimmed pages stay in
    /// the system file cache, and a later read of one is a soft fault. Call it from a background thread every few seconds; it costs about 0.15 ms per MiB that was touched.
    /// </summary>
    /// <returns>The archives trimmed (0 when no <see cref="PageTrimmer"/> is set or the source is disposed).</returns>
    public int TrimMappedPages()
    {
        Action<nint, nuint>? trim = PageTrimmer;
        if (trim is null || !_uses.TryEnter(out UseGate.Use use))
        {
            return 0;
        }

        using (use)
        {
            int trimmed = 0;
            foreach (Lazy<MrgArchive> lazy in _archives.Values)
            {
                if (lazy.IsValueCreated && lazy.Value is { } archive && archive.MappedLength >= TrimmedArchiveBytes)
                {
                    archive.TrimResident(trim);
                    trimmed++;
                }
            }

            return trimmed;
        }
    }

    /// <summary>How long <see cref="Dispose"/> waits for decodes that are reading the mappings.</summary>
    private static readonly TimeSpan UnmapWait = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Unmaps the archives. Windows handed out by <see cref="Archive"/> or <see cref="OpenImage"/> must not be used afterwards. Calls that start after this
    /// one throw <see cref="ObjectDisposedException"/>; a decode that is running (<see cref="Audio"/>, <see cref="Picture(int, int)"/>, ...) is waited for first,
    /// because a read of an unmapped page is an access violation that ends the process (package K10). If a decode is still running after 15 s the mappings
    /// are left to the operating system (they go at process exit) instead of being pulled out from under it.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_uses.Close(UnmapWait))
        {
            return;
        }

        foreach (Lazy<MrgArchive> lazy in _archives.Values)
        {
            if (lazy.IsValueCreated)
            {
                lazy.Value.Dispose();
            }
        }

        _archives.Clear();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
