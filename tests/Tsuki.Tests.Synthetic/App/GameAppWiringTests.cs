using System.Text.RegularExpressions;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// The call sites in GameApp that no run of the game reaches in a test (every test start is --hidden, and a hidden window never goes fullscreen), read from the
/// source: when Windows asks for fullscreen (at once only for --fullscreen, else once the game is set up, as v0.1.6 did), what SDL's fullscreen events and
/// Alt+Enter / F11 are told to, the Steam Deck's request when the window is shown, the loop's SIGTERM, and the order of the guard and the default log file.
/// </summary>
public sealed partial class GameAppWiringTests
{
    private static readonly Lazy<string> Code = new(() =>
    {
        string text = File.ReadAllText(Path.Combine(Repo.Src, "Tsuki.App", "Play", "GameApp.cs"));
        return Whitespace().Replace(LineComment().Replace(text, "$1"), string.Empty);
    });

    // the text between two markers of the whitespace-free source
    private static string Between(string from, string to)
    {
        string code = Code.Value;
        int start = code.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"GameApp.cs no longer has {from}");
        int end = code.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"GameApp.cs has no {to} after {from}");
        return code[start..end];
    }

    [Fact]
    public void TheWindowAsksForFullscreenAtShowOnlyForTheCommandLineAndTheSteamDeck()
    {
        string show = Between("if(!_hidden){Placementplaced=_startup.PlaceWindow(_window);", "_device=GlDevice.Create(");
        Assert.Contains("_window.Show();if(_startMode.FullscreenAtShow){boolaccepted=_window.SetFullscreen(true);", show, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRememberedFullscreenIsAskedForOnceTheGameIsSetUp()
    {
        string setUp = Between("privatevoidSetUpInput()", "privateintLoop()");
        Assert.Contains("if(!_hidden&&_startMode.FullscreenAfterSetUp){boolaccepted=_window.SetFullscreen(true);", setUp, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(Code.Value, Regex.Escape("SetFullscreen(true)")));      // and nowhere else
    }

    [Fact]
    public void SdlsFullscreenEventsGoToTheEventRuleAndTheHotkeysAreRemembered()
    {
        string loop = Between("privateintLoop()", "privatevoidRequestScreenshot(");
        Assert.Contains("casePlatformEventKind.EnteredFullscreen:casePlatformEventKind.LeftFullscreen:_display?.NoteFullscreenEvent(e);", loop, StringComparison.Ordinal);
        Assert.Contains("casePlatformEventKind.FullscreenToggled:_display?.NoteFullscreen(e.Fullscreen);", loop, StringComparison.Ordinal);
        Assert.DoesNotContain("NoteFullscreen(e.Kind", loop, StringComparison.Ordinal);
    }

    [Fact]
    public void ASigtermEndsTheLoopBeforeTheLastSaveAndIsHeldBackOnlyUntilThen()
    {
        string loop = Between("privateintLoop()", "privatevoidRequestScreenshot(");
        int listen = loop.IndexOf("_terminate=TerminationSignal.Listen();", StringComparison.Ordinal);
        int body = loop.IndexOf("while(running){", StringComparison.Ordinal);
        int check = loop.IndexOf("if(running&&_terminate.Requested){running=false;stop=\"SIGTERM\";}", StringComparison.Ordinal);
        int save = loop.IndexOf("_services.Commit();_terminate.Dispose();", StringComparison.Ordinal);
        Assert.True(listen >= 0 && listen < body, "the loop listens for SIGTERM before it runs");
        Assert.True(check > body && check < save, "the loop ends on SIGTERM and saves");
    }

    [Fact]
    public void TheDefaultLogFileIsOpenedOnlyByTheGameThatHoldsTheProfile()
    {
        string run = Between("publicstaticintRun(AppArgsargs)", "internalstaticstringPlayerMessage(");
        int guard = run.IndexOf("SingleInstance.TryAcquire(dataDir);", StringComparison.Ordinal);
        int refused = run.IndexOf("returnExitAlreadyRunning;", StringComparison.Ordinal);
        int defaultLog = run.IndexOf("DefaultLogFile(args,dataDir,OperatingSystem.IsWindows())", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < refused && refused < defaultLog, "a second game is refused before it opens last_run.log");
    }

    [Fact]
    public void TheExtraRunLogLinesAndBoxesAreLinuxAndMacOSOnly()
    {
        // every line that writes one of them sits in a block whose condition keeps Windows out (its log stays v0.1.6's)
        string[] lines = File.ReadAllLines(Path.Combine(Repo.Src, "Tsuki.App", "Play", "GameApp.cs"));
        string[] marks = ["RunReport.Sdl(", "RunReport.Steam(", "RunReport.WindowState(", "RunReport.PadAtStart(", "RunReport.AudioDevice(", "audioWatch.Note(", "presentWatch.Note(",
            "window: entered fullscreen", "window: fullscreen refused by SDL", "window mode: SDL refused", "pixels, fullscreen", "the --game folder", "CommandLineFolderMessage(_args"];
        int found = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!marks.Any(m => lines[i].Contains(m, StringComparison.Ordinal)) || lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) || lines[i].Contains("internal static", StringComparison.Ordinal))
            {
                continue;
            }

            found++;
            int indent = Indent(lines[i]);
            bool gated = WindowsGate().IsMatch(lines[i]);
            for (int j = i - 1; j > 0 && indent > 0 && !gated; j--)
            {
                if (lines[j].Trim().Length == 0 || Indent(lines[j]) >= indent)
                {
                    continue;
                }

                // an enclosing line: the block's brace (whose header is the line above it) or a case label
                indent = Indent(lines[j]);
                gated = WindowsGate().IsMatch(lines[j]) || (lines[j].Trim() == "{" && WindowsGate().IsMatch(lines[j - 1]));
            }

            Assert.True(gated, $"GameApp.cs line {i + 1} is not behind a Linux/macOS condition: {lines[i].Trim()}");
        }

        Assert.Equal(14, found);
    }

    [Fact]
    public void ACommandLineTheGameCannotReadIsRefusedThroughTheReport()
    {
        string main = Whitespace().Replace(LineComment().Replace(File.ReadAllText(Path.Combine(Repo.Src, "Tsuki.App", "Program.cs")), "$1"), string.Empty);
        Assert.Contains("if(!AppArgs.TryParse(args,outAppArgs?a,outstring?error)){Console.Error.WriteLine(error);Console.Error.WriteLine(AppArgs.Usage);"
            + "RefuseCommandLine(args,error??\"?\",hidden,OperatingSystem.IsWindows());returnExitUsage;}", main, StringComparison.Ordinal);
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    [GeneratedRegex(@"RunReport\.Enabled|OperatingSystem\.IsLinux\(\)|!OperatingSystem\.IsWindows\(\)|\breportSize\b|case PlatformEventKind\.AudioDevice")]
    private static partial Regex WindowsGate();

    [GeneratedRegex(@"(^|\s)//[^\n]*", RegexOptions.Multiline)]
    private static partial Regex LineComment();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
