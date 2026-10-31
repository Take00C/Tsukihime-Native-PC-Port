using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Data;

/// <summary>The game folder the data tests read (RR 3.3 identity, size only; the full build check is Tsuki.Data.BuildCheck).</summary>
public sealed class GameFolderTests
{
    [GameDataFact]
    public void EbootHasTheAnalysedSize()
    {
        var eboot = new FileInfo(Path.Combine(GameFolder.Require(), "eboot.bin"));
        Assert.Equal(BuildInfo.AnalysedEbootSize, eboot.Length);
    }
}
