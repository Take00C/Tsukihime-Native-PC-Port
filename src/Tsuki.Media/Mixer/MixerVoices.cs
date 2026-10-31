namespace Tsuki.Media.Mixer;

/// <summary>
/// The mixer voice slot of every sound source (the layout of <see cref="AudioMixer.MaxVoices"/> = 64): the PS4 stream numbers
/// 0..25 keep their numbers (sound_runtime.md 2.1), the HE-VAG system voices sit at 0x2C..0x2F (10.1; the right clip of each pair on 26..29), and the movie audio of
/// slot s plays on 0x30 + s.
/// </summary>
public static class MixerVoices
{
    /// <summary>First system voice (0x2C).</summary>
    public const int FirstSystem = 0x2C;

    /// <summary>Number of system voices (4, rotating).</summary>
    public const int SystemCount = 4;

    /// <summary>First movie voice (0x30).</summary>
    public const int FirstMovie = 0x30;

    /// <summary>Number of movie voices (slots 0..15).</summary>
    public const int MovieCount = AudioMixer.MaxVoices - FirstMovie;

    /// <summary>The voice of sound stream <paramref name="stream"/> (0..25).</summary>
    public static int Stream(int stream) =>
        (uint)stream < 26 ? stream : throw new ArgumentOutOfRangeException(nameof(stream), stream, "streams are 0..25");

    /// <summary>The voice of system voice <paramref name="voice"/> (0x2C..0x2F, as <c>PlaySystem</c> carries it).</summary>
    public static int System(int voice) =>
        voice is >= FirstSystem and < FirstSystem + SystemCount
            ? voice
            : throw new ArgumentOutOfRangeException(nameof(voice), voice, "system voices are 0x2C..0x2F");

    /// <summary>
    /// The mixer voice of the right half of a system sound: <c>SystemSE_Play</c> plays two HE-VAG clips, the left one hard left on
    /// <see cref="System"/> and the right one hard right on this voice (26..29).
    /// </summary>
    public static int SystemRight(int voice) => System(voice) - FirstSystem + 26;

    /// <summary>The voice of the audio of movie slot <paramref name="slot"/>.</summary>
    public static int Movie(int slot) =>
        (uint)slot < MovieCount ? FirstMovie + slot : throw new ArgumentOutOfRangeException(nameof(slot), slot, $"movie slots are 0..{MovieCount - 1}");
}
