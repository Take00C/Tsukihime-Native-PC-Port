// v0.1.9, FIXES D13 S-7: the Windows launcher's window steps aside for the game (MainWindow minimises it when the model says GameStarted) and only the model's
// GameExited brings it back. The model says GameExited after it has read the profile and the game folder again, and that refresh can throw (the monitors, the
// adapters, the achievements, an icon): the window then stayed in the task bar behind the error box. GameExited is now said in a finally around the refresh,
// as the Linux and macOS model says it since D11 A-3 (tested there on the real model, tests/Tsuki.Tests.Avalonia); what the refresh threw still leaves the
// method and reaches the handler of the window's thread (launcher_crash.log and the box), as before. The WPF view model cannot be compiled here, so: a game's
// end with the shape of LauncherModel.OnGameExited on a stand-in for the window's thread, and the pin in LauncherModel.cs that it has that shape.
using System.Text.RegularExpressions;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Synthetic.Launcher;

public sealed class WindowsGameEndTests
{
    /// <summary>What the stand-in model did and said, and what reached the handler of its thread, in their order.</summary>
    private readonly List<string> _said = [];

    /// <summary>
    /// A game's end with the shape of LauncherModel.OnGameExited (pinned in its source by the last test of this file): the play state, the refresh, the window
    /// told in a finally, then the launcher's own toast for a game that did not end well.
    /// </summary>
    private async void OnGameExited(int code, Func<Task> refresh)
    {
        _said.Add("play state");
        try
        {
            await refresh();
        }
        finally
        {
            _said.Add($"GameExited({code})");
        }

        if (LauncherTexts.GameExited(code) is { } toast)
        {
            _said.Add("toast: " + toast);
        }
    }

    [Fact]
    public void AGameThatEndsBringsTheWindowBackOnceTheRefreshIsDoneAndThenShowsItsToast()
    {
        var window = new WindowThread(e => _said.Add("handler: " + e.Message));
        var refreshed = new TaskCompletionSource();
        window.Run(() => OnGameExited(-5, () =>
        {
            _said.Add("refresh");
            return refreshed.Task;
        }));

        // while the refresh reads, the window is not told yet (it comes back showing the folder and the profile as they are after the game)
        Assert.Equal(["play state", "refresh"], _said);

        window.Run(refreshed.SetResult);
        Assert.Equal(["play state", "refresh", "GameExited(-5)", "toast: The game closed with an error (code -5). Press Log to see why."], _said);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARefreshThatThrowsAfterTheGameStillBringsTheWindowBackAndItsExceptionGoesOnToTheWindowsThread(bool afterAnAwait)
    {
        var thrown = new IOException("the monitors said no");
        var handled = new List<Exception>();
        var window = new WindowThread(e =>
        {
            handled.Add(e);
            _said.Add("handler: " + e.Message);
        });
        var reading = new TaskCompletionSource();
        window.Run(() => OnGameExited(-5, async () =>
        {
            if (afterAnAwait)
            {
                await reading.Task;
            }

            throw thrown;
        }));
        window.Run(reading.SetResult);

        // the window is told once, before the exception arrives where an exception nobody caught arrives on that thread; the exception is the refresh's own, and
        // nothing after the refresh runs (no toast), as before
        Assert.Equal(["play state", "GameExited(-5)", "handler: the monitors said no"], _said);
        Assert.Same(thrown, Assert.Single(handled));
    }

    // -- the Windows launcher's own game end (WPF: pinned in its source) -------------------------------------------------

    [Fact]
    public void TheWindowsPortableSwitchIsAskedAgainWhenTheGameStartsAndWhenItEnds()
    {
        // K53: the portable switch's command may not run while the game runs (its CanExecute reads GameRunning). WPF asks CanExecute again only when the
        // command says CanExecuteChanged, so the model must say it wherever GameRunning changes, as it does for PLAY; the Linux and macOS model does
        // (UnixLauncherModel's play-state change, tested on the real model). The WPF view model cannot be compiled here, so: the pin in LauncherModel.cs.
        string model = File.ReadAllText(Path.Combine(Repo.Src, "Tsuki.Launcher", "LauncherModel.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] lines = [.. WithoutComments(model).Split('\n').Select(line => line.Trim())];
        int[] play = [.. lines.Select((line, i) => (line, i)).Where(x => x.line == "(PlayCommand as RelayCommand)?.Raise();").Select(x => x.i)];
        Assert.Equal(2, play.Length);       // the game's start (Play) and its end (OnGameExited): the two places GameRunning changes
        foreach (int i in play)
        {
            Assert.True(
                i + 1 < lines.Length && lines[i + 1] == "((RelayCommand)PortableSavesCommand).Raise();",
                $"line {i + 1} of LauncherModel.cs tells PLAY that GameRunning changed but not the portable switch");
        }
    }

    [Fact]
    public void TheWindowsLauncherRefusesProfileChangesWhileThePortableCopyRuns()
    {
        // v0.1.11 review: the portable switch asks once, before copying, whether a game runs, and PLAY, Back up, Import and Restore did not wait for the copy.
        // The Linux and macOS model is tested on the real model (Avalonia LauncherHeadlessTests.WhileThePortableSwitchCopiesNothingElseTouchesTheProfile); the
        // WPF view model cannot be compiled here, so the pins in LauncherModel.cs: the guard every profile change asks refuses while the copy runs, PLAY, Back
        // up and Import are disabled then, and the switch asks them again when the copy starts and when it ends
        string model = WithoutComments(File.ReadAllText(Path.Combine(Repo.Src, "Tsuki.Launcher", "LauncherModel.cs")).Replace("\r\n", "\n", StringComparison.Ordinal));
        string Method(string signature)
        {
            int start = model.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature + " is not in LauncherModel.cs");
            return model[start..model.IndexOf("\n    }\n", start, StringComparison.Ordinal)];
        }

        string guard = Method("    private bool ProfileChangeRefused()\n");
        Assert.True(guard.IndexOf("if (_portableBusy)", StringComparison.Ordinal) is >= 0 and var busy && busy < guard.IndexOf("if (!_profileRestartRequired)", StringComparison.Ordinal),
            "ProfileChangeRefused does not refuse while the portable copy runs");
        Assert.Contains("PlayCommand = new RelayCommand(_ => Play(), _ => !GameRunning && !UpdateBusy && !_portableBusy);", model, StringComparison.Ordinal);
        Assert.Contains("BackupNowCommand = new RelayCommand(_ => BackupNow(), _ => !GameRunning && !_portableBusy);", model, StringComparison.Ordinal);
        Assert.Contains("ImportCommand = new RelayCommand(_ => ImportRequested?.Invoke(), _ => !GameRunning && !_portableBusy);", model, StringComparison.Ordinal);
        string[] set = [.. Method("    private async Task SetPortableSavesAsync(bool enable)\n").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)];
        foreach (string state in new[] { "_portableBusy = true;", "_portableBusy = false;" })
        {
            int i = Array.IndexOf(set, state);
            Assert.True(i >= 0 && i + 1 < set.Length && set[i + 1] == "RaiseProfileCommands();", $"SetPortableSavesAsync does not ask the buttons again after {state}");
        }
    }

    [Fact]
    public void TheWindowsLauncherSaysTheGameEndedInAFinallyAroundItsRefresh()
    {
        string launcher = Path.Combine(Repo.Src, "Tsuki.Launcher");
        string model = File.ReadAllText(Path.Combine(launcher, "LauncherModel.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        int start = model.IndexOf("    private async void OnGameExited(int code)\n    {\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "OnGameExited is not in LauncherModel.cs");
        int end = model.IndexOf("\n    }\n", start, StringComparison.Ordinal);

        // the whole method, line by line (comments are not code): the refresh is the one thing in the try, GameExited the one thing in the finally, and there is
        // no catch (what the refresh threw goes on to the dispatcher's handler), no return before the finally and nothing else that is awaited
        string[] lines = [.. WithoutComments(model[start..end]).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)];
        string[] wanted =
        [
            "private async void OnGameExited(int code)",
            "{",
            "_process = null;",
            "Raise(nameof(GameRunning));",
            "Raise(nameof(PlayLabel));",
            "(PlayCommand as RelayCommand)?.Raise();",
            "((RelayCommand)PortableSavesCommand).Raise();",
            "try",
            "{",
            "await RefreshAsync();",
            "}",
            "finally",
            "{",
            "GameExited?.Invoke(code);",
            "}",
            "if (LauncherTexts.GameExited(code) is { } toast)",
            "{",
            "ShowToast(toast);",
            "}",
        ];
        Assert.True(
            wanted.SequenceEqual(lines, StringComparer.Ordinal),
            "OnGameExited no longer says GameExited in a finally around its refresh and nothing else (the window was minimised for the game and only GameExited brings it back). It reads: " + string.Join(" ", lines));

        // every place in the launcher's code that names the event or the method: the model says GameExited in that finally and nowhere else, and the game's end
        // has one caller
        var named = new List<string>();
        foreach (string file in Directory.EnumerateFiles(launcher, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Repo.Relative(file);
            if (relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase) || relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string code = WithoutComments(File.ReadAllText(file));
            foreach (string name in new[] { "GameExited", "OnGameExited" })
            {
                int times = Regex.Matches(code, $@"\b{name}\b").Count;
                if (times > 0)
                {
                    named.Add($"{relative}: {name}, {times}");
                }
            }
        }

        named.Sort(StringComparer.Ordinal);
        Assert.Equal(
            [
                "src/Tsuki.Launcher/Core/LauncherTexts.cs: GameExited, 1",   // the text of the toast
                "src/Tsuki.Launcher/LauncherModel.cs: GameExited, 3",        // the event, the finally, the text of the toast
                "src/Tsuki.Launcher/LauncherModel.cs: OnGameExited, 2",      // the method and its caller
                "src/Tsuki.Launcher/MainWindow.xaml.cs: GameExited, 1",      // the window
            ],
            named);

        // that caller is the game's Exited event, through the dispatcher: the method runs on the window's thread, and what leaves it is the dispatcher's to report
        Assert.Contains("            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnGameExited(code));\n", model, StringComparison.Ordinal);

        // the window: minimised when the game starts, brought back when the model says the game ended
        string window = File.ReadAllText(Path.Combine(launcher, "MainWindow.xaml.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("        model.GameStarted += () =>\n        {\n            before = WindowState;\n            WindowState = WindowState.Minimized;\n        };\n", window, StringComparison.Ordinal);
        Assert.Contains("        model.GameExited += code =>\n        {\n            WindowState = before == WindowState.Minimized ? WindowState.Normal : before;\n", window, StringComparison.Ordinal);
    }

    /// <summary>A source text without its <c>//</c> comments (the launcher's sources have no other kind).</summary>
    private static string WithoutComments(string source) => Regex.Replace(source, @"//[^\n]*", string.Empty);

    /// <summary>
    /// A stand-in for the window's thread, on the thread of the test: what is posted to it runs in its turn, and an exception that leaves a posted piece of work
    /// goes to its handler, as one that nobody caught goes to the dispatcher's.
    /// </summary>
    private sealed class WindowThread(Action<Exception> handler) : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Work, object? State)> _posted = new();

        public override void Post(SendOrPostCallback d, object? state) => _posted.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        /// <summary>Runs <paramref name="work"/> on this thread as the window's, then everything that was posted meanwhile.</summary>
        public void Run(Action work)
        {
            SynchronizationContext? before = Current;
            SetSynchronizationContext(this);
            try
            {
                work();
                while (_posted.TryDequeue(out (SendOrPostCallback Work, object? State) next))
                {
                    try
                    {
                        next.Work(next.State);
                    }
                    catch (Exception e)
                    {
                        handler(e);
                    }
                }
            }
            finally
            {
                SetSynchronizationContext(before);
            }
        }
    }
}
