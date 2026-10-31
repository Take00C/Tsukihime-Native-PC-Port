using System.Buffers.Binary;

namespace Tsuki.Data.Eboot.Tables;

/// <summary>One CG of the gallery (a 32-byte record of a group's NULL-terminated list).</summary>
/// <param name="Group">Group index (0..457).</param>
/// <param name="Index">Position inside the group.</param>
/// <param name="Name">The picture file name without the one-character prefix (<c>IMG1685.MZP</c>).</param>
/// <param name="Prefix">The prefix character (' ' or '-'); no code reads it.</param>
/// <param name="Thumbnail">Index into the GALLERY_PARTS_CG_TH sheet (knife's thumbnail id).</param>
/// <param name="X">View offset x (knife's CG_ORIGINS; 19 entries are non-zero).</param>
/// <param name="Y">View offset y.</param>
/// <param name="Width">Picture width in the viewer.</param>
/// <param name="Height">Picture height in the viewer.</param>
/// <param name="Flag">The +0x1C byte (meaning [H]; knife's "gallery-only" is a different quantity).</param>
/// <param name="Category">The group's unlock category (knife's group condition id).</param>
public sealed record CgEntry(int Group, int Index, string Name, char Prefix, int Thumbnail, int X, int Y, int Width, int Height, int Flag, int Category);

/// <summary>A gallery group: the entries that are shown as one tile.</summary>
/// <param name="Index">Group index.</param>
/// <param name="Category">Unlock category 0..19.</param>
/// <param name="Entries">The CGs of the group.</param>
public sealed record CgGroup(int Index, int Category, IReadOnlyList<CgEntry> Entries);

/// <summary>A CG unlocked by seeing another picture (<c>key</c> is shown when one of the aliases was seen).</summary>
/// <param name="Index">Table index.</param>
/// <param name="Key">The CG's picture name.</param>
/// <param name="Aliases">The pictures that unlock it.</param>
public sealed record CgAlias(int Index, string Key, IReadOnlyList<string> Aliases);

/// <summary>An entry of the picture seen-index map: image <c>Name</c> is recorded under bit <c>SeenIndex</c> (knife's SEEN_ALIAS).</summary>
/// <param name="Index">Table index.</param>
/// <param name="Name">Picture name.</param>
/// <param name="SeenIndex">The seen bit (2701 and up).</param>
public sealed record SeenMapEntry(int Index, string Name, int SeenIndex);

/// <summary>A track of the music room.</summary>
/// <param name="Index">Position in the list.</param>
/// <param name="Bgm">bgm.hed index; the unlock bit is 0xC80 + this.</param>
/// <param name="TitleUi">ui_text id of the title.</param>
/// <param name="VocalUi">Credit ui_text id after "Vocals:", -1 none.</param>
/// <param name="LyricsUi">Lyrics credit, -1 none.</param>
/// <param name="ComposerUi">Composer credit, -1 none.</param>
/// <param name="ArrangerUi">Arranger credit, -1 none.</param>
/// <param name="LengthMs">Length in ms (0 for some).</param>
/// <param name="LoopStart">Loop start sample (48 kHz).</param>
/// <param name="LoopEnd">Loop end sample (knife's end is this + 1).</param>
/// <param name="Variants">Other bgm indices that also unlock the track.</param>
public sealed record MusicTrack(
    int Index, int Bgm, int TitleUi, int VocalUi, int LyricsUi, int ComposerUi, int ArrangerUi, int LengthMs, int LoopStart, int LoopEnd, IReadOnlyList<int> Variants);

/// <summary>An entry of the ending list (the 25 "Teach Me, Miss Ciel" scenarios).</summary>
/// <param name="Index">Table row.</param>
/// <param name="ScriptScene">Scene id passed to the replay (1000..1024).</param>
/// <param name="FlowScene">The script's RDST id (533..557; knife's node bit).</param>
/// <param name="UnlockA">scene_done(UnlockA) or scene_done(UnlockB) unlocks the entry.</param>
/// <param name="UnlockB">Second unlock scene, -1 none.</param>
/// <param name="Message">Index into the help-message table.</param>
/// <param name="MessageUi">The ui_text id of the "Play ...?" prompt, -1 when out of range.</param>
public sealed record ExtraScenario(int Index, int ScriptScene, int FlowScene, int UnlockA, int UnlockB, int Message, int MessageUi);

/// <summary>
/// The CG gallery, music room and ending list tables of the eboot (docs/formats/gallery.md 4-6; knife's <c>gallery_table.rs</c>, <c>gallery.rs</c> and the
/// <c>SEEN_ALIAS</c> of <c>save_tables.rs</c>). Port of the gallery half of <c>tsuki.misc_gallery_extras_flowchart.load</c>; the knife cross-check of the
/// research document (docs/research/knife_tables.md) found 1972 of 1972 entries, 105 of 105 aliases, 2144 of 2144 seen-map entries and 95 of 95 tracks equal.
/// </summary>
public sealed class GalleryTables
{
    /// <summary>RELRO VA of the 458 group records of 32 bytes.</summary>
    public const long GroupVa = 0x400830;

    /// <summary>Number of groups.</summary>
    public const int GroupCount = 458;

    /// <summary>RELRO VA of the 105 alias records of 0x28 bytes.</summary>
    public const long AliasVa = 0x404170;

    /// <summary>Number of aliases.</summary>
    public const int AliasCount = 105;

    /// <summary>RELRO VA of the 11 special (four-language-band) CG names.</summary>
    public const long SpecialVa = 0x4051E0;

    /// <summary>Number of special names.</summary>
    public const int SpecialCount = 11;

    /// <summary>Data VA of the 16-byte seen-map records (NULL-name terminated).</summary>
    public const long SeenMapVa = 0x86AA30;

    /// <summary>Text VA of the 95 music records of 0x38 bytes.</summary>
    public const long MusicVa = 0x1A47D0;

    /// <summary>Number of music tracks.</summary>
    public const int MusicCount = 95;

    /// <summary>Text VA of the 25 ending-list records of 20 bytes.</summary>
    public const long ExtraVa = 0x18F810;

    /// <summary>Number of ending-list entries.</summary>
    public const int ExtraCount = 25;

    /// <summary>Text VA of the 55 help-message ids (the dialog table's tail, ending prompts).</summary>
    public const long HelpMessageVa = 0x2D2D40;

    /// <summary>Number of help messages.</summary>
    public const int HelpMessageCount = 55;

    /// <summary>Text VA of the 27 trophy scene records of 8 bytes.</summary>
    public const long TrophySceneVa = 0x221400;

    /// <summary>Number of trophy scenes.</summary>
    public const int TrophySceneCount = 27;

    /// <summary>Text VA of the jump table of the CG category switch (VA 0x1C460).</summary>
    public const long CategoryJumpTableVa = 0x188380;

    /// <summary>The common join of the category switch.</summary>
    public const long CategoryJoin = 0x1C51B;

    /// <summary>Number of unlock categories with a rule (1..19).</summary>
    public const int CategoryCount = 19;

    private GalleryTables(
        IReadOnlyList<CgGroup> groups, IReadOnlyList<CgAlias> aliases, IReadOnlyList<string> special, IReadOnlyList<SeenMapEntry> seenMap,
        IReadOnlyList<MusicTrack> music, IReadOnlyList<ExtraScenario> extras, IReadOnlyList<int> helpMessages, IReadOnlyList<int> trophyScenes,
        IReadOnlyDictionary<int, IReadOnlyList<int>> categoryFlags)
    {
        Groups = groups;
        Aliases = aliases;
        Special = special;
        SeenMap = seenMap;
        Music = music;
        Extras = extras;
        HelpMessages = helpMessages;
        TrophyScenes = trophyScenes;
        CategoryFlags = categoryFlags;
    }

    /// <summary>The 458 groups.</summary>
    public IReadOnlyList<CgGroup> Groups { get; }

    /// <summary>All 1,972 CGs in group order.</summary>
    public IEnumerable<CgEntry> Entries => Groups.SelectMany(g => g.Entries);

    /// <summary>The 105 aliases.</summary>
    public IReadOnlyList<CgAlias> Aliases { get; }

    /// <summary>The 11 CG names that stack one picture per language (knife's CG_LANGUAGE_STRIPS).</summary>
    public IReadOnlyList<string> Special { get; }

    /// <summary>The seen-index map (2,144 entries).</summary>
    public IReadOnlyList<SeenMapEntry> SeenMap { get; }

    /// <summary>The 95 music tracks.</summary>
    public IReadOnlyList<MusicTrack> Music { get; }

    /// <summary>The 25 ending-list entries.</summary>
    public IReadOnlyList<ExtraScenario> Extras { get; }

    /// <summary>The 55 help-message ui_text ids (-1 none).</summary>
    public IReadOnlyList<int> HelpMessages { get; }

    /// <summary>The first field of the 27 trophy scene records.</summary>
    public IReadOnlyList<int> TrophyScenes { get; }

    /// <summary>For each CG category 1..19, the scene_seen ids of which any one unlocks it (read from the compiled switch at VA 0x1C460).</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<int>> CategoryFlags { get; }

    /// <summary>Reads every gallery table.</summary>
    /// <exception cref="EbootFormatException">A table does not have the expected shape.</exception>
    public static GalleryTables Read(EbootReader eboot)
    {
        ArgumentNullException.ThrowIfNull(eboot);

        EbootLayout layout = eboot.Layout;
        List<CgGroup> groups = [];
        for (int g = 0; g < layout.GalleryGroupCount; g++)
        {
            long va = layout.GalleryGroups + (32L * g);
            long list = Pointer(eboot, va);
            int category = eboot.I32(va + 8);
            if (!eboot.Read(va + 12, 20).ToArray().All(b => b == 0))
            {
                throw new EbootFormatException($"CG group {g}: unexpected non-zero tail");
            }

            List<CgEntry> entries = [];
            for (long e = list; ; e += 32)
            {
                long namePtr = Pointer(eboot, e);
                if (namePtr == 0)
                {
                    break;
                }

                string raw = eboot.CString(namePtr, 64);
                entries.Add(new CgEntry(
                    g, entries.Count, raw[1..], raw[0], eboot.I32(e + 8), eboot.I32(e + 12), eboot.I32(e + 16), eboot.I32(e + 20), eboot.I32(e + 24), eboot.I32(e + 28), category));
            }

            groups.Add(new CgGroup(g, category, entries));
        }

        List<CgAlias> aliases = [];
        for (int i = 0; i < layout.GalleryAliasCount; i++)
        {
            long va = layout.GalleryAliases + (0x28L * i);
            string key = eboot.CString(Pointer(eboot, va), 64);
            List<string> names = [];
            for (int k = 1; k < 5; k++)
            {
                long p = Pointer(eboot, va + (8L * k));
                if (p != 0)
                {
                    names.Add(eboot.CString(p, 64));
                }
            }

            aliases.Add(new CgAlias(i, key, names));
        }

        List<string> special = [];
        for (int i = 0; i < layout.GallerySpecialCount; i++)
        {
            special.Add(eboot.CString(Pointer(eboot, layout.GallerySpecial + (8L * i)), 64));
        }

        if (Pointer(eboot, layout.GallerySpecial + (8L * layout.GallerySpecialCount)) != 0)
        {
            throw new EbootFormatException($"the special CG name list is longer than {layout.GallerySpecialCount}");
        }

        List<SeenMapEntry> seen = [];
        for (long va = layout.SeenMap; ; va += 16)
        {
            long p = Pointer(eboot, va);
            if (p == 0)
            {
                break;
            }

            seen.Add(new SeenMapEntry(seen.Count, eboot.CString(p, 64), eboot.I32(va + 12)));
        }

        List<MusicTrack> music = [];
        for (int i = 0; i < layout.MusicCount; i++)
        {
            long va = layout.Music + (0x38L * i);
            int[] f = new int[14];
            for (int k = 0; k < 14; k++)
            {
                f[k] = eboot.I32(va + (4L * k));
            }

            music.Add(new MusicTrack(i, f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], [.. f[9..].Where(v => v >= 0)]));
        }

        int[] help = new int[HelpMessageCount];
        for (int i = 0; i < help.Length; i++)
        {
            help[i] = eboot.I32(layout.DialogMessages + (4L * i));
        }

        List<ExtraScenario> extras = [];
        for (int i = 0; i < layout.ExtraCount; i++)
        {
            long va = layout.Extras + (20L * i);
            int message = eboot.I32(va + 16);
            extras.Add(new ExtraScenario(
                i, eboot.I32(va), eboot.I32(va + 4), eboot.I32(va + 8), eboot.I32(va + 12), message, message >= 0 && message < HelpMessageCount ? help[message] : -1));
        }

        int[] trophies = new int[layout.TrophySceneCount];
        for (int i = 0; i < trophies.Length; i++)
        {
            trophies[i] = eboot.I32(layout.TrophyScenes + (8L * i));
        }

        return new GalleryTables(groups, aliases, special, seen, music, extras, help, trophies, ReadCategoryFlags(eboot));
    }

    /// <summary>The absolute pointer stored at <paramref name="slot"/> (image base 0); 0 is NULL.</summary>
    internal static long Pointer(EbootReader eboot, long slot)
    {
        ulong raw = eboot.U64(slot);
        if (raw != 0 && eboot.FileOffset((long)raw) is null)
        {
            throw new EbootFormatException($"slot 0x{slot:x}: pointer 0x{raw:x} is not file-backed");
        }

        return (long)raw;
    }

    /// <summary>
    /// Category 1..19 to its scene_seen ids from the switch at 0x1C460: each case loads <c>mov edi, imm32</c> once, or twice with an early-out
    /// <c>call 0xB81A0; test al, al; jne</c> between (category 15 = 0x113 or 0x114). Scanned byte by byte over those few instruction forms.
    /// </summary>
    private static Dictionary<int, IReadOnlyList<int>> ReadCategoryFlags(EbootReader eboot)
    {
        EbootLayout layout = eboot.Layout;
        Dictionary<int, IReadOnlyList<int>> result = [];
        for (int c = 1; c <= layout.CategoryCount; c++)
        {
            long at = layout.CategoryJumpTable + eboot.I32(layout.CategoryJumpTable + (4L * (c - 1)));
            at &= 0xFFFFFFFFL;
            List<int> flags = [];
            while (at < layout.CategoryJoin)
            {
                ReadOnlySpan<byte> b = eboot.Read(at, 8);
                if (b[0] == 0xBF)
                {
                    flags.Add(BinaryPrimitives.ReadInt32LittleEndian(b[1..]));
                    at += 5;
                }
                else if (b[0] == 0xE8)
                {
                    if (BinaryPrimitives.ReadInt32LittleEndian(b[1..]) + at + 5 != 0xB81A0)
                    {
                        throw new EbootFormatException($"category {c}: unexpected call at 0x{at:x}");
                    }

                    at += 5;
                }
                else if (b[0] == 0x84 && b[1] == 0xC0)
                {
                    at += 2;
                }
                else if (b[0] == 0x0F && b[1] == 0x85)
                {
                    at += 6;
                }
                else if (b[0] is 0xE9 or 0xEB)
                {
                    break;
                }
                else
                {
                    throw new EbootFormatException($"category {c}: unexpected opcode {b[0]:X2} at 0x{at:x}");
                }
            }

            if (flags.Count == 0)
            {
                throw new EbootFormatException($"category {c}: no flag found");
            }

            result[c] = flags;
        }

        return result;
    }
}
