using System.Globalization;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;
using Tsuki.Engine.Vm;

namespace Tsuki.Engine.Sound;

/// <summary>
/// The marks the PS4's <c>_VPLY</c> / <c>_VPL2</c> handler leaves in the G words (docs/formats/sound_runtime.md 3.6, adv_state.md 9.4; package K36):
/// they are what the Sound tab of the config reads to show one row per character heard, and what its test buttons play. All of them lie in
/// G[0..499], so they persist in the system block with the read text and the unlocks.
/// </summary>
/// <remarks>
/// VA 0x99EF7..0x99F08 (the handler, after the group lookup): <c>G[73] |= (u16)(1 &lt;&lt; character)</c>. VA 0x3D260 (our <c>VoiceGallery_Heard</c>,
/// called at 0x9A058 with the character and the entry of the statement): nothing for character 23 or a negative entry; else
/// <c>k = log2(mask[character &lt; 24 ? character : 0])</c>, <c>G[31 + k] = (u16)entry</c>, <c>G[words[k]] |= 4</c> with the row words i32[19] at
/// text VA 0x1A6850. The handler returns before any of it in scan mode (byte 0x174DE48, VA 0x968B2).
/// </remarks>
public static class VoiceMarks
{
    /// <summary>G[73]: bit c set once character c (0..15; the shift is truncated to 16 bits) spoke.</summary>
    public const int HeardWord = 73;

    /// <summary>G[31 + k]: the entry of the last voice of config slot k (what the row's test button plays, VA 0x401E0).</summary>
    public const int LastVoiceWord = 31;

    /// <summary>The bit of a row word that shows the row (VA 0x3D2A6; read at 0x3E088).</summary>
    public const int RowShownBit = 4;

    /// <summary>The bit of a row word that gives rows 15 and 16 their own face (VA 0x3E1C9).</summary>
    public const int RowFaceBit = 1;

    /// <summary>The bit of a row word that gives the rows outside mask 0x430C9 their own name (VA 0x3E22E).</summary>
    public const int RowNameBit = 2;

    /// <summary>Number of config voice rows (slots 0..18).</summary>
    public const int RowCount = SoundConfig.CharacterSlots;

    /// <summary>
    /// What a <c>_VPLY</c> / <c>_VPL2</c> of <paramref name="character"/> with <paramref name="entry"/> writes (VA 0x99F08 and 0x3D260).
    /// <paramref name="rowWords"/> is the eboot's row word table (null: the row bit is not written, a test rig without the table).
    /// </summary>
    public static void Note(Vars vars, IReadOnlyList<int>? rowWords, int character, int entry)
    {
        ArgumentNullException.ThrowIfNull(vars);
        // or word ptr [G73], dx with edx = 1 << cl: characters 16 and above leave the word alone
        vars.SetG(HeardWord, (ushort)(vars.GetG(HeardWord) | (ushort)(1u << (character & 31))));
        if (character == VoiceGroups.ExtraCharacter || entry < 0)
        {
            return;
        }

        int slot = VoiceGroups.SlotOf(character);
        vars.SetG(LastVoiceWord + slot, unchecked((ushort)entry));
        if (rowWords is not null && slot < rowWords.Count && (uint)rowWords[slot] < Vars.GCount)
        {
            int w = rowWords[slot];
            vars.SetG(w, (ushort)(vars.GetG(w) | RowShownBit));
        }
    }

    /// <summary>True when the row word of slot <paramref name="slot"/> has its shown bit (the row's character spoke).</summary>
    public static bool IsRowShown(Vars vars, IReadOnlyList<int> rowWords, int slot)
    {
        ArgumentNullException.ThrowIfNull(vars);
        ArgumentNullException.ThrowIfNull(rowWords);
        return slot >= 0 && slot < rowWords.Count && (uint)rowWords[slot] < Vars.GCount && (vars.GetG(rowWords[slot]) & RowShownBit) != 0;
    }

    /// <summary>
    /// True for a profile written before the marks were recorded (package K36): text was read (<c>readmax</c> has an entry) but no character
    /// ever got its heard bit. The first voiced line of the game (00_00, Shiki) sets G[73], so a profile that played anything with the marks
    /// in place never qualifies again.
    /// </summary>
    public static bool NeedsRebuild(Vars vars, SystemBlock system)
    {
        ArgumentNullException.ThrowIfNull(vars);
        ArgumentNullException.ThrowIfNull(system);
        if (vars.GetG(HeardWord) != 0)
        {
            return false;
        }

        for (int b = 0; b < SystemBlock.ReadmaxCount; b++)
        {
            if (system.GetReadmax(b) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rebuilds the marks of a profile that predates them from the read text: every <c>_VPLY</c> / <c>_VPL2</c> whose next marker counts as read
    /// (<see cref="SystemData.IsRead"/>: the marker's line is at most the block's <c>readmax</c>, which the engine raises when the marker after it is
    /// reached) ran on the PS4 too, so its marks are written as the handler would have. The entry of G[31 + k] is the last one in script order
    /// (the PS4 keeps the last one heard in play order). Returns the number of voice statements credited.
    /// </summary>
    public static int RebuildFromReadText(IGameData data, SystemData system, Vars vars, IReadOnlyList<int>? rowWords)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(vars);
        int credited = 0;
        var pending = new List<(int Character, int Entry)>();
        for (int k = 0; k < data.ScriptCount; k++)
        {
            ScriptImage image = data.Script(k);
            ReadOnlySpan<byte> bytes = image.Bytes.Span;
            pending.Clear();
            int entry = -1;                                 // 0x16541FC: -1 after a message consumed it
            foreach (int start in image.SegmentStarts)
            {
                ScriptSegment seg = ScriptSegmenter.Read(bytes, start);
                if (!seg.IsCommand)
                {
                    continue;
                }

                byte first = bytes[seg.NameStart];
                if (first == (byte)'Z')
                {
                    if (!ScriptSegmenter.TryReadMarkerId(bytes, seg, out int block, out int sub))
                    {
                        continue;
                    }

                    if (pending.Count > 0)
                    {
                        if (system.IsRead(block, sub))
                        {
                            foreach ((int c, int e) in pending)
                            {
                                Note(vars, rowWords, c, e);
                                credited++;
                            }
                        }

                        pending.Clear();
                    }

                    if (bytes[seg.NameStart + 1] == (byte)'M')
                    {
                        entry = -1;                         // the message display takes the pending voice (VA 0x92380)
                    }

                    continue;
                }

                if (first != (byte)'V' || !ScriptSegmenter.TryGetStatement(k, image.Bytes, seg, out Statement st))
                {
                    continue;
                }

                string name = st.Name.ToString();
                if (name is not ("VPLY" or "VPL2"))
                {
                    continue;
                }

                ReadOnlySpan<byte> b = st.Args.Span;
                string voice = SoundTags.ArgText(b, 0, 0);
                int character = VoiceGroups.CharacterOf(voice);
                if (!voice.StartsWith('_'))
                {
                    string hex = SoundTags.ArgText(b, 0, 1).Trim();
                    entry = hex.Length == 0 ? 0 : int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int idx) ? idx : 0;
                }

                pending.Add((character, entry));
                if (name == "VPL2")
                {
                    entry = -1;                             // _VPL2 plays at once and clears the pending voice
                }
            }
        }

        return credited;
    }
}
