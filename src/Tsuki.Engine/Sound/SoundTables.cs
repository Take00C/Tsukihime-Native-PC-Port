using System.Numerics;
using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Sound;

/// <summary>
/// The voice-group tables (docs/formats/sound_runtime.md 8; eboot table 0x215E90, mask table 0x86A850, verified for all 54
/// voice.nam groups): voice name prefix to character, character to config slot.
/// </summary>
public static class VoiceGroups
{
    /// <summary>Character 23: the groups of the extra material (M00..), which have no voice row in the config.</summary>
    public const int ExtraCharacter = 23;

    private static readonly Dictionary<string, int> CharOfGroup = new(StringComparer.Ordinal)
    {
        ["A00"] = 1, ["A10"] = 2, ["A11"] = 2, ["A12"] = 2, ["A20"] = 3, ["A21"] = 3, ["A22"] = 3, ["A23"] = 3, ["A24"] = 3,
        ["B00"] = 4, ["B10"] = 5, ["B20"] = 6, ["B30"] = 7, ["B40"] = 8, ["B50"] = 9, ["B60"] = 10, ["B70"] = 11,
        ["B80"] = 12, ["B90"] = 13, ["C00"] = 14, ["C10"] = 15, ["C20"] = 16, ["C30"] = 17, ["C40"] = 18, ["C50"] = 19,
        ["C60"] = 20, ["C70"] = 21, ["C80"] = 0, ["C90"] = 22,
        ["M00"] = 23, ["M01"] = 23, ["M02"] = 23, ["M03"] = 23, ["M11"] = 23, ["M12"] = 23,
    };

    /// <summary>0x86A850: the config enable mask of each character (u32[24]).</summary>
    public static IReadOnlyList<uint> CharacterMask { get; } =
    [
        0x40000, 0x1, 0x2, 0x4, 0x200, 0x10000, 0x10, 0x20, 0x8, 0x80, 0x1000, 0x8000,
        0x20000, 0x1, 0x400, 0x2000, 0x4000, 0x800, 0x40, 0x40000, 0x40000, 0x100, 0x40000, 0x40000,
    ];

    /// <summary>The character the <c>_VPLY</c> handler derives from a voice name (its first 3 characters; every group not listed gets 0).</summary>
    public static int CharacterOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string prefix = name.Length >= 3 ? name[..3] : name;
        return CharOfGroup.GetValueOrDefault(prefix, 0);
    }

    /// <summary>The enable mask of <paramref name="character"/> (characters outside 0..23 use character 0, VA 0x3D2D0).</summary>
    public static uint MaskOf(int character) => CharacterMask[character is >= 0 and < 24 ? character : 0];

    /// <summary>The config slot k of a character: bit k of config +0x3C, volume u16 at config +0x76 + 2k (VA 0x3D2D0).</summary>
    public static int SlotOf(int character) => BitOperations.Log2(MaskOf(character));
}

/// <summary>
/// The system sounds (docs/formats/sound_runtime.md 10.1; eboot tables 0x2D3770 and 0x6241F0): <c>SystemSE_Play(id)</c> plays a
/// pair of HE-VAG entries, one hard left and one hard right.
/// </summary>
public static class SystemSounds
{
    /// <summary>Number of system sound ids (28).</summary>
    public const int Count = 28;

    private static readonly (int Bank, int Index)[] IdTable =
    [
        (0, 0), (0, 1), (0, 2), (0, 4), (0, 5), (0, 6), (0, 1), (0, 3), (0, 7), (0, 8),
        .. Enumerable.Repeat((0, 20), 18),
    ];

    private static readonly (int Left, int Right)[] VagTable =
    [
        (3, 4), (5, 6), (1, 2), (7, 8), (9, 10), (11, 12), (13, 14), (15, 16), (17, 18),
        .. Enumerable.Repeat((0, -1), 27),
    ];

    /// <summary>
    /// The (left, right) <c>vag.mrg</c> entries of system sound <paramref name="id"/> (VA 0xCC670): right is -1 for a mono sound and
    /// left &lt;= 0 means nothing plays.
    /// </summary>
    public static (int Left, int Right) Vags(int id)
    {
        if ((uint)id >= IdTable.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, $"system sound ids are 0..{IdTable.Length - 1}");
        }

        (int bank, int index) = IdTable[id];
        return VagTable[(bank * 128) + index];
    }

    /// <summary>The bank an <c>open_handle</c> of <paramref name="stream"/> belongs to: SE channels use vse, voices use voice.</summary>
    internal static AudioBank BankOfStream(int stream) => stream >= AudioStreams.FirstSe ? AudioBank.Se : AudioBank.Voice;
}
