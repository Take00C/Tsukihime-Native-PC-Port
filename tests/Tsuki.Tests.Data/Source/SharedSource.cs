using Tsuki.Data;
using Tsuki.Media.Audio;

namespace Tsuki.Tests.Data.Source;

/// <summary>
/// One <see cref="GameFolderSource"/> over the user's folder for the tests of this assembly (opening costs about 25 ms; the archives are
/// mapped on first use and stay mapped until the process ends). The decoder is the real one of Tsuki.Media.
/// </summary>
internal static class SharedSource
{
    private static readonly Lazy<GameFolderSource> Instance = new(
        () => GameFolderSource.Open(GameFolder.Require(), new MediaAudioDecoder(), decodeParallelism: 4),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The shared source; only call it from a game-data test.</summary>
    public static GameFolderSource Source => Instance.Value;
}
