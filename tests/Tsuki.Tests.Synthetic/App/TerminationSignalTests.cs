using System.Diagnostics;
using Tsuki.App.Play;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>deck-gamemode-1: on Linux and macOS the loop holds back the first SIGTERM (Steam's Exit Game) and quits the way the title's EXIT does, saved.</summary>
public sealed class TerminationSignalTests
{
    [Fact]
    public void OffWindowsTheFirstSigtermAsksTheLoopToQuitAndTheProcessLivesOn()
    {
        using TerminationSignal signal = TerminationSignal.Listen();
        Assert.Equal(!OperatingSystem.IsWindows(), signal.Listening);
        if (OperatingSystem.IsWindows())
        {
            Assert.False(signal.Requested);
            return;
        }

        Assert.False(signal.Requested);
        using (var kill = Process.Start(new ProcessStartInfo("kill", ["-TERM", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)]) { UseShellExecute = false })!)
        {
            kill.WaitForExit();
            Assert.Equal(0, kill.ExitCode);
        }

        var wait = Stopwatch.StartNew();
        while (!signal.Requested && wait.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(10);
        }

        Assert.True(signal.Requested);                      // and this test process was not ended by it
    }
}
