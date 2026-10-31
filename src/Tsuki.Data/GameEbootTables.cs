using Tsuki.Data.Catalog;
using Tsuki.Data.Eboot;
using Tsuki.Data.Fonts;
using Tsuki.Data.Scripts;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Data;

/// <summary>
/// The eboot-only tables of the user's copy (<see cref="IEbootTables"/>, RR 3.2, FORMATS 5.4), read from <c>eboot.bin</c> on first use:
/// the image catalog and picture names, the movie table, the UI registration tables and <c>ui_text</c> (all of them in
/// <see cref="Catalog"/>), the scene ids, the layer motions and the glyph lookups. It is the shipping counterpart of
/// <c>GoldenEbootTables</c> (which holds the same figures from the oracle's export); more tables are hung on <see cref="Reader"/>
/// by the packages that decode them (Tsuki.Data/Eboot/Tables).
/// </summary>
/// <remarks>
/// Every table is immutable after it is built, so one instance is shared between the decode workers. Tables that cost milliseconds
/// (the 71,905 motion keyframes, the 17,423 glyph lookups) are built on first use.
/// </remarks>
public sealed partial class GameEbootTables : IEbootTables
{
    private readonly Lazy<SceneTable> _scenes;
    private readonly Lazy<MotionTables> _motions;
    private readonly Lazy<EbootFontData> _fontData;
    private readonly Lazy<FontTables> _fontTables;

    /// <summary>Creates the tables over a parsed eboot and the catalog built from it.</summary>
    public GameEbootTables(EbootReader reader, GameCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(catalog);
        Reader = reader;
        Catalog = catalog;
        _scenes = new(() => SceneTable.Read(reader), LazyThreadSafetyMode.ExecutionAndPublication);
        _motions = new(() => MotionTables.Read(reader), LazyThreadSafetyMode.ExecutionAndPublication);
        _fontData = new(() => EbootFontData.Read(reader), LazyThreadSafetyMode.ExecutionAndPublication);
        _fontTables = new(() => _fontData.Value.ToEngineTables(), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The parsed eboot.bin: address mapping, strict reads at a virtual address (A2).</summary>
    public EbootReader Reader { get; }

    /// <summary>The image table, picture names, NAM search, movie table, UI texture tables and UI text (A2).</summary>
    public GameCatalog Catalog { get; }

    /// <summary>Script number k to scene id: 549 entries (docs/formats/script.md 2.5).</summary>
    public SceneTable SceneIds => _scenes.Value;

    /// <summary>The movie table, one record per movie file: 134 records (docs/formats/archives_ui_movies.md 9.2).</summary>
    public IReadOnlyList<MovieRecord> Movies => Catalog.Movies;

    /// <summary>The layer motions of <c>_STMA</c> / <c>_SGMA</c>: 24,192 motions and 71,905 keyframes (docs/formats/layers.md).</summary>
    public MotionTables Motions => _motions.Value;

    /// <summary>The glyph lookup and rectangle tables as stored: 17,423 lookup entries (docs/formats/fonts.md 5).</summary>
    public EbootFontData FontData => _fontData.Value;

    /// <summary>The same tables in the form the text layout reads (<c>Tsuki.Engine.Text</c>).</summary>
    public FontTables FontTables => _fontTables.Value;

    /// <summary>The UI strings, 5 languages x 1,499 (RR 3.2).</summary>
    public Eboot.UiTextTable UiText => Catalog.UiText;
}
