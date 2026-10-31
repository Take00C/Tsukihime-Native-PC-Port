using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Save;

/// <summary>
/// The persistent system data (the PS4 system block, 0x18AC bytes; adv_state.md 7.5, engine.md 7.3): the config, the
/// persistent G variables G[0..499], the unlock bit sets (pictures seen, BGM heard), the read-text table, the latest
/// slot and the choice-taken bytes. The game keeps the live copies of all but G and the config in RAM and merges them
/// into the block when it saves; here the block is the one copy, so the unlock bits, readmax and choice-taken bytes
/// are read and written on it directly, and only G needs the explicit sync with the interpreter's variables
/// (<see cref="LoadGlobalsInto"/> at start-up, <see cref="CaptureGlobalsFrom"/> before a save).
/// </summary>
public sealed class SystemData
{
    /// <summary>The highest text block that readmax records (blocks 0xFB4 and above are never recorded; engine.md 5.6).</summary>
    public const int ReadmaxBlocks = SystemBlock.ReadmaxCount;

    /// <summary>The persistent G variables (G[0..499]).</summary>
    public const int PersistentGlobals = SystemBlock.GCount;

    /// <summary>Creates fresh system data: magic and used flag set, the config at its boot defaults, the rest zero.</summary>
    public SystemData()
        : this(CreateFreshBlock())
    {
    }

    /// <summary>Wraps an existing block (read from a save file); the block is not copied.</summary>
    public SystemData(SystemBlock block)
    {
        Block = block ?? throw new ArgumentNullException(nameof(block));
    }

    /// <summary>The block (the layout of save image offset 0).</summary>
    public SystemBlock Block { get; }

    /// <summary>The configuration (the live one: pass it to <see cref="AdvLiveState"/>).</summary>
    public ConfigBlock Config => Block.Config;

    /// <summary>The slot "Continue" loads: the newest manual or auto save (updated by <see cref="SaveStore"/>).</summary>
    public int LatestSlot => Block.LatestSlot;

    private static SystemBlock CreateFreshBlock()
    {
        var block = new SystemBlock { Magic = SystemBlock.MagicValue, Used = 1 };
        block.Config.CopyFrom(ConfigBlock.CreateBootDefaults());
        return block;
    }

    // -- read text (adv_state.md 6; engine.md 5.6) -------------------------------------------------------------------------

    /// <summary>
    /// "Is this line read" (VA 0x75C90): <c>readmax[block] != 0 and (line &lt;= readmax[block] or block &gt;= 0xFB4)</c>.
    /// Blocks outside 0..0xFFF read as unread.
    /// </summary>
    public bool IsRead(int block, int line)
    {
        if (block < 0 || block >= ReadmaxBlocks)
        {
            return false;
        }

        int max = Block.GetReadmax(block);
        return max != 0 && line <= max;
    }

    /// <summary>
    /// The Z handler's read mark (VA 0x96615): before it switches to a new label, the previous one counts as read:
    /// <c>if line &gt; readmax[block] and block &lt;= 0xFB3: readmax[block] = line</c> (the value is a byte).
    /// </summary>
    public void MarkRead(int block, int line)
    {
        if (block is < 0 or >= ReadmaxBlocks)
        {
            return;
        }

        if (line > Block.GetReadmax(block))
        {
            Block.SetReadmax(block, (byte)Math.Min(line, 255));
        }
    }

    // -- G variables ------------------------------------------------------------------------------------------------------

    /// <summary>Boot / load: G[0..499] of the interpreter's variables take the saved values (VA 0x6F470).</summary>
    public void LoadGlobalsInto(Vars vars)
    {
        ArgumentNullException.ThrowIfNull(vars);
        for (int i = 0; i < PersistentGlobals; i++)
        {
            vars.SetG(i, Block.GetG(i));
        }
    }

    /// <summary>Before a save: the block takes G[0..499] of the interpreter's variables (VA 0x6FF2D).</summary>
    public void CaptureGlobalsFrom(Vars vars)
    {
        ArgumentNullException.ThrowIfNull(vars);
        for (int i = 0; i < PersistentGlobals; i++)
        {
            Block.SetG(i, vars.GetG(i));
        }
    }

    /// <summary>
    /// The load fix-up of config +0x24 (VA 0x6FE94): a negative language (the boot value 0xFFFF) becomes the system
    /// language (0 ja, 1 en, 2 zc, 3 zt).
    /// </summary>
    public void ApplyLanguageFixup(int systemLanguage)
    {
        if (Config.Language < 0)
        {
            Config.Language = (short)systemLanguage;
        }
    }

    // -- progress flags (docs/formats/gallery.md 2.1): bit sets that live in G words -----------------------------------------

    /// <summary>First G word of the scene_seen set (G[110..145]).</summary>
    public const int SceneSeenWord = 110;

    /// <summary>First G word of the scene_done set (G[300..335]).</summary>
    public const int SceneDoneWord = 300;

    /// <summary>First G word of the edge_seen set (G[400..443]); bit n - 564.</summary>
    public const int EdgeSeenWord = 400;

    /// <summary>The G word whose low 16 bits are the movie_seen flags (G[5]).</summary>
    public const int MovieSeenWord = 5;

    /// <summary>scene_seen(n), n at most 0x23F (the bit set of G[110..145]): the scene was started at least once (reads the interpreter's variables).</summary>
    public static bool SceneSeen(Vars vars, int n) => GetWordBit(vars, SceneSeenWord, n, 0x23F);

    /// <summary>scene_done(n), n at most 0x23F (G[300..335]): the scene was finished (gates the flowchart nodes and the gallery).</summary>
    public static bool SceneDone(Vars vars, int n) => GetWordBit(vars, SceneDoneWord, n, 0x23F);

    /// <summary>edge_seen(n), 564 &lt;= n &lt;= 1267 (G[400..443]): the flowchart edge was taken.</summary>
    public static bool EdgeSeen(Vars vars, int n) => n is >= 564 and <= 1267 && GetWordBit(vars, EdgeSeenWord, n - 564, 0xFFFF);

    /// <summary>movie_seen(bit): bit 0..15 of G[5].</summary>
    public static bool MovieSeen(Vars vars, int bit) => bit is >= 0 and < 16 && GetWordBit(vars, MovieSeenWord, bit, 15);

    /// <summary>Sets scene_seen(n).</summary>
    public static void SetSceneSeen(Vars vars, int n) => SetWordBit(vars, SceneSeenWord, n, 0x23F);

    /// <summary>Sets scene_done(n).</summary>
    public static void SetSceneDone(Vars vars, int n) => SetWordBit(vars, SceneDoneWord, n, 0x23F);

    /// <summary>Sets edge_seen(n).</summary>
    public static void SetEdgeSeen(Vars vars, int n)
    {
        if (n is >= 564 and <= 1267)
        {
            SetWordBit(vars, EdgeSeenWord, n - 564, 0xFFFF);
        }
    }

    /// <summary>Sets movie_seen(bit).</summary>
    public static void SetMovieSeen(Vars vars, int bit)
    {
        if (bit is >= 0 and < 16)
        {
            SetWordBit(vars, MovieSeenWord, bit, 15);
        }
    }

    private static bool GetWordBit(Vars vars, int firstWord, int n, int max)
    {
        ArgumentNullException.ThrowIfNull(vars);
        if (n < 0 || n > max)
        {
            return false;
        }

        return ((vars.GetG(firstWord + (n >> 4)) >> (n & 15)) & 1) != 0;
    }

    private static void SetWordBit(Vars vars, int firstWord, int n, int max)
    {
        ArgumentNullException.ThrowIfNull(vars);
        if (n < 0 || n > max)
        {
            return;
        }

        int w = firstWord + (n >> 4);
        vars.SetG(w, (ushort)(vars.GetG(w) | (1 << (n & 15))));
    }

    /// <summary>Marks picture <paramref name="seenIndex"/> as seen (the gallery CG unlock; gallery.md 2.2). Indices outside 0..3199 are ignored.</summary>
    public void MarkPictureSeen(int seenIndex)
    {
        if (seenIndex is >= 0 and < SystemBlock.PictureSeenBits)
        {
            Block.SetPictureSeen(seenIndex, true);
        }
    }

    /// <summary>Marks BGM <paramref name="track"/> as heard (the music player unlock). Tracks outside 0..0x207 are ignored.</summary>
    public void MarkBgmHeard(int track)
    {
        if (track is >= 0 and < SystemBlock.BgmHeardTracks)
        {
            Block.SetBgmHeard(track, true);
        }
    }

    /// <summary>Records that option <paramref name="option"/> (0..7) of choice <paramref name="choiceId"/> was taken (byte choiceId, bit option).</summary>
    public void MarkChoiceTaken(int choiceId, int option)
    {
        if (choiceId is >= 0 and < SystemBlock.ChoiceTakenCount && option is >= 0 and < 8)
        {
            Block.SetChoiceTaken(choiceId, (byte)(Block.GetChoiceTaken(choiceId) | (1 << option)));
        }
    }
}
