namespace Tsuki.Engine.Contracts;

/// <summary>
/// Named views of the two unlock regions of the system block (docs/formats/gallery.md 2, 7.3). The
/// field map itself is generated (Generated/SystemBlock.g.cs).
/// </summary>
public sealed partial class SystemBlock
{
    /// <summary>picture_seen occupies unlock bits 0..3199 (gallery.md 2.2).</summary>
    public const int PictureSeenBits = 3200;

    /// <summary>bgm_heard(track) is unlock bit 0xC80 + track (gallery.md 2, <c>_MPLY</c> 0x87250).</summary>
    public const int BgmHeardFirstBit = 0xC80;

    /// <summary>bgm_heard covers tracks 0..0x207.</summary>
    public const int BgmHeardTracks = 0x208;

    /// <summary>picture_seen(<paramref name="seenIndex"/>); false for a negative index (gallery.md: picture_seen(-1) is false).</summary>
    public bool IsPictureSeen(int seenIndex) => seenIndex is >= 0 and < PictureSeenBits && GetUnlockBit(seenIndex);

    /// <summary>Sets picture_seen(<paramref name="seenIndex"/>).</summary>
    public void SetPictureSeen(int seenIndex, bool value) => SetUnlockBit(CheckIndex(seenIndex, PictureSeenBits), value);

    /// <summary>bgm_heard(<paramref name="track"/>), track 0..0x207; false outside.</summary>
    public bool IsBgmHeard(int track) => track is >= 0 and < BgmHeardTracks && GetUnlockBit(BgmHeardFirstBit + track);

    /// <summary>Sets bgm_heard(<paramref name="track"/>).</summary>
    public void SetBgmHeard(int track, bool value) => SetUnlockBit(BgmHeardFirstBit + CheckIndex(track, BgmHeardTracks), value);

    /// <summary>choice_taken: bit <paramref name="option"/> of byte <paramref name="choiceId"/> (gallery.md 2, choice screen 0xA6647).</summary>
    public bool IsChoiceTaken(int choiceId, int option) => ((GetChoiceTaken(choiceId) >> CheckIndex(option, 8)) & 1) != 0;
}
