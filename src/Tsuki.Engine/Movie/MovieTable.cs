using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Movie;

/// <summary>
/// The movie table and its language remap (docs/formats/archives_ui_movies.md 9.2-9.3; ARCHITECTURE 7.5). A script's
/// <c>_TOMP(id)</c> passes the language-neutral id; the engine remaps it to the table index of the language's copy before it
/// emits <see cref="MovieCommand.Open"/>.
/// </summary>
public static class MovieTable
{
    /// <summary>Records in the table (ids 0..133; the record after them has id -1).</summary>
    public const int Count = 134;

    /// <summary>The first title background loop (<c>titlebg1</c>); the four are ids 0..3.</summary>
    public const int FirstTitleBackground = 0;

    /// <summary>The prologue movie id passed by the script.</summary>
    public const int Prologue = 4;

    /// <summary>Opening 1 (<c>op1_arc</c>).</summary>
    public const int Opening1 = 5;

    /// <summary>Opening 2 (<c>op2_ciel</c>).</summary>
    public const int Opening2 = 6;

    /// <summary>The first chapter title card (<c>phasetitle_*</c>); ids 8..34.</summary>
    public const int FirstPhaseTitle = 8;

    /// <summary>The last chapter title card.</summary>
    public const int LastPhaseTitle = 34;

    private static readonly int[] PrologueByLanguage = [4, 46, 47, 106];

    /// <summary>
    /// The table index of movie <paramref name="id"/> in <paramref name="language"/> (the routine at eboot VA 0x5E280, 9.3):
    /// the prologue has four copies, the 27 title cards and the two openings have <c>_en</c> / <c>_zc</c> (and title cards <c>_zt</c>)
    /// variants, every other movie is shared.
    /// </summary>
    public static int Remap(int id, Language language)
    {
        int lang = (int)language;
        if (id == Prologue)
        {
            return lang < PrologueByLanguage.Length ? PrologueByLanguage[lang] : PrologueByLanguage[1];
        }

        if (id is >= FirstPhaseTitle and <= LastPhaseTitle)
        {
            return lang switch
            {
                0 => id,
                3 => id + 99,
                2 => id + 67,
                _ => id + 40,
            };
        }

        if (id is Opening1 or Opening2)
        {
            return lang switch
            {
                2 or 3 => id + 99,
                0 => id,
                _ => id + 97,
            };
        }

        return id;
    }
}

/// <summary>The movie slots the engine uses in <see cref="MovieCommand"/>s (slot numbers are ours; ARCHITECTURE 3.2).</summary>
public static class MovieSlots
{
    /// <summary>The full-screen movie of <c>_TOMP</c>, the TYPE-MOON logo and the chapter cards.</summary>
    public const int FullScreen = 0;

    /// <summary>The looping title background (<c>titlebg1..4</c>, texture slot 0x87 on the PS4, system_flow.md 4.2).</summary>
    public const int TitleBackground = 1;
}
