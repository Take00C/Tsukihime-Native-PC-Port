using Tsuki.Data.Containers;
using Tsuki.Data.Eboot;
using Tsuki.Data.IO;

namespace Tsuki.Data.Catalog;

/// <summary>
/// Everything the runtime needs from the eboot to find a picture, a movie or a UI texture, loaded once from the user's
/// folder: the image catalog, the picture-name lists, the NAM search over the five searched archives, the script image
/// resolver, the movie table and the UI texture registration tables. The data is read-only after loading; the instance
/// can be shared between threads. Call it only after the build check accepted the folder (RR 3.3).
/// </summary>
public sealed class GameCatalog
{
    private readonly Lazy<UiTextTable> _uiText;

    private GameCatalog(
        EbootReader eboot, ImageCatalog images, IReadOnlyList<string> names, IReadOnlyList<string> oldNames,
        ImageNameIndex nameIndex, ScriptImageResolver resolver, IReadOnlyList<MovieRecord> movies,
        IReadOnlyList<string> languageSuffixes, IReadOnlyList<int> prologueIds, IReadOnlyList<UiTexTable> uiTables,
        IReadOnlyDictionary<ImageArchive, NamTable> nams)
    {
        Eboot = eboot;
        Images = images;
        PictureNames = names;
        OldPictureNames = oldNames;
        NameIndex = nameIndex;
        Resolver = resolver;
        Movies = movies;
        LanguageSuffixes = languageSuffixes;
        PrologueIds = prologueIds;
        UiTables = uiTables;
        Nams = nams;
        _uiText = new Lazy<UiTextTable>(() => UiTextTable.Read(eboot));
    }

    /// <summary>The parsed eboot.bin.</summary>
    public EbootReader Eboot { get; }

    /// <summary>The image table: 14,744 records.</summary>
    public ImageCatalog Images { get; }

    /// <summary>The picture-name list (14,745 records, the last empty): name k is the name of record k.</summary>
    public IReadOnlyList<string> PictureNames { get; }

    /// <summary>The old name list (9,959 records).</summary>
    public IReadOnlyList<string> OldPictureNames { get; }

    /// <summary>The NAM name tables of the archives that exist as image sources (allpac0, allpac1, allpac1_2, allpac3, allpac4).</summary>
    public IReadOnlyDictionary<ImageArchive, NamTable> Nams { get; }

    /// <summary>The engine's NAM file-name search.</summary>
    public ImageNameIndex NameIndex { get; }

    /// <summary>Script image id to image-table record to archive entries.</summary>
    public ScriptImageResolver Resolver { get; }

    /// <summary>The movie table (134 records).</summary>
    public IReadOnlyList<MovieRecord> Movies { get; }

    /// <summary>The language suffixes <c>ja en zc zt ko</c>.</summary>
    public IReadOnlyList<string> LanguageSuffixes { get; }

    /// <summary>The prologue movie ids per language (4, 46, 47, 106).</summary>
    public IReadOnlyList<int> PrologueIds { get; }

    /// <summary>The 21 UI texture registration tables.</summary>
    public IReadOnlyList<UiTexTable> UiTables { get; }

    /// <summary>The UI strings, 5 x 1,499; read on first use.</summary>
    public UiTextTable UiText => _uiText.Value;

    /// <summary>The five archives whose name tables the NAM search uses, with the file that holds each.</summary>
    public static IReadOnlyList<(ImageArchive Archive, string Nam)> SearchedArchives { get; } =
    [
        (ImageArchive.Allpac4, "image/allpac4.nam"), (ImageArchive.Allpac3, "image/allpac3.nam"),
        (ImageArchive.Allpac1_2, "image/allpac1_2.nam"), (ImageArchive.Allpac1, "image/allpac1.nam"),
        (ImageArchive.Allpac0, "image/allpac0.nam"),
    ];

    /// <summary>Loads the catalog from a folder view: <c>eboot.bin</c> and the five <c>.nam</c> files.</summary>
    /// <exception cref="EbootFormatException">The eboot does not hold the tables at the analysed addresses.</exception>
    /// <exception cref="ContainerFormatException">A .nam file breaks its layout.</exception>
    public static GameCatalog Load(IGameFiles files)
    {
        byte[] data = files.ReadAll("eboot.bin");
        var eb = EbootReader.Parse(data);
        var nams = new Dictionary<ImageArchive, NamTable>();
        foreach (var (arc, path) in SearchedArchives)
        {
            nams[arc] = NamTable.ParseNam32(files.ReadAll(path));
        }

        return Create(eb, nams);
    }

    /// <summary>Builds the catalog from a parsed eboot and the name tables of the searched archives.</summary>
    public static GameCatalog Create(EbootReader eb, IReadOnlyDictionary<ImageArchive, NamTable> nams)
    {
        var images = ImageCatalog.Read(eb);
        var names = PictureNameLists.Read(eb);
        var index = new ImageNameIndex(nams);
        return new GameCatalog(
            eb, images, names, PictureNameLists.ReadOld(eb), index, new ScriptImageResolver(names, images.Entries, index),
            MovieTable.Read(eb), MovieTable.ReadLanguageSuffixes(eb), MovieTable.ReadPrologueIds(eb), UiTextureTables.ReadAll(eb), nams);
    }
}
