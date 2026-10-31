using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using Tsuki.App.Services;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Data.App;

/// <summary>
/// Package K21 on the real game, in the real program (hidden, muted, a temporary profile, the pictures in temp folders): the screenshot key writes a PNG of the
/// window's size without the toast and shows the toast afterwards; the window's close request asks the quit question and quits on Yes or on a second request;
/// Copy Text to Clipboard writes each message (to the file the verification runs use instead of the desktop clipboard); Mute in Background fades the mixer out on a
/// lost focus and back in; the Text Window Opacity slider writes the PS4's byte. A fresh profile goes PRESS ANY KEY, cautions, story, so each test first lets the
/// autoplay run into the story once (the title then shows its menu).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class PortFeaturesAppTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k21-port-" + Guid.NewGuid().ToString("N"));

    public PortFeaturesAppTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (Process Process, string LogPath) Start(string saveDir, params string[] more)
    {
        string game = GameFolder.Require();
        string app = Path.Combine(AppContext.BaseDirectory, GameLauncher.AppFileName);
        string log = Path.Combine(_root, "run-" + Guid.NewGuid().ToString("N") + ".log");
        var info = new ProcessStartInfo(app) { WorkingDirectory = Path.GetDirectoryName(app)!, UseShellExecute = false };
        info.Environment["SDL_AUDIO_DRIVER"] = "dummy";
        foreach (string a in new[] { "--game", game, "--log", log, "--hidden", "--mute", "--fast", "--save-dir", saveDir, "--windowed" }.Concat(more))
        {
            info.ArgumentList.Add(a);
        }

        return (Process.Start(info)!, log);
    }

    private (int ExitCode, string Log) Run(string saveDir, params string[] more)
    {
        (Process process, string log) = Start(saveDir, more);
        using (process)
        {
            Assert.True(process.WaitForExit(240_000), "the game did not exit");
            return (process.ExitCode, File.ReadAllText(log));
        }
    }

    // a profile whose story was started: the skipping autoplay reads the prologue (ends at step 494), presses PRESS ANY KEY, sits out the cautions and starts the story
    private string StartedProfile(string name)
    {
        string save = Path.Combine(_root, name);
        Directory.CreateDirectory(save);
        (int code, string log) = Run(save, "--autoplay", "skip", "--exit-after", "2600");
        Assert.Equal(0, code);
        Assert.Contains("story ended: ReturnKeepBgm", log, StringComparison.Ordinal);
        return save;
    }

    // the returning start (K19's measurement with --fast): boot notice, the cautions skipped by Enter at 60 and 300, the title's menu up and taking keys by step 500;
    // Up three times from NEW GAME wraps to EXIT, GALLERY, CONFIG, Enter opens the config screen (a still picture, apart from the pulsing cursor)
    private const string ToConfig = "60:Enter;300:Enter;600:Up;620:Up;640:Up;660:Enter";

    /// <summary>The size and pixels of a PNG the program wrote (one IDAT, zlib, filter 0 on every row, as both of its writers do).</summary>
    private static (int Width, int Height, byte[] Rgba) ReadPng(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        int pos = 8;
        int width = 0;
        int height = 0;
        using var idat = new MemoryStream();
        while (pos + 8 <= file.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(file, pos + 4, 4);
            if (type == "IHDR")
            {
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos + 8));
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos + 12));
            }
            else if (type == "IDAT")
            {
                idat.Write(file, pos + 8, len);
            }

            pos += 12 + len;
        }

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new byte[height * ((width * 4) + 1)];
        z.ReadExactly(raw);
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            Array.Copy(raw, (y * ((width * 4) + 1)) + 1, rgba, y * width * 4, width * 4);
        }

        return (width, height, rgba);
    }

    /// <summary>The share of the pixels of a rectangle that differ (any channel by more than 8) between two pictures of the same size.</summary>
    private static double DifferentShare((int Width, int Height, byte[] Rgba) a, (int Width, int Height, byte[] Rgba) b, int x0, int y0, int x1, int y1)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        int changed = 0;
        int total = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = ((y * a.Width) + x) * 4;
                total++;
                if (Math.Abs(a.Rgba[i] - b.Rgba[i]) > 8 || Math.Abs(a.Rgba[i + 1] - b.Rgba[i + 1]) > 8 || Math.Abs(a.Rgba[i + 2] - b.Rgba[i + 2]) > 8)
                {
                    changed++;
                }
            }
        }

        return changed / (double)total;
    }

    [GameDataFact]
    public void TheScreenshotKeyWritesThePresentedFrameAtTheWindowsSizeWithoutTheToastAndShowsTheToastAfterwards()
    {
        string save = StartedProfile("profile-shot");
        string folder = Path.Combine(save, "pictures");
        // on the config screen (a still): a frame shot before anything, the key, the toast up at 1040, the key again (its capture must not show the first toast), a frame shot with the toast up
        (int code, string log) = Run(save, "--script", $"{ToConfig};990:shot=before;1000:screenshot;1040:log;1050:screenshot;1062:shot=after;1100:log;1120:quit", "--shot-dir", save, "--screenshot-dir", folder);
        Assert.Equal(0, code);
        Assert.Contains("ui Title [TitleScreen>ConfigScreen]", log, StringComparison.Ordinal);
        Assert.Contains("screenshot requested (script)", log, StringComparison.Ordinal);
        Assert.Contains("[   1000] screenshot: writing ", log, StringComparison.Ordinal);                             // the frame of step 1000 is the one captured
        Assert.Contains("(1280 x 720)", log, StringComparison.Ordinal);                                              // the hidden window's size
        Assert.Equal(2, log.Split("screenshot saved: ").Length - 1);
        Assert.Contains("toast \"Screenshot saved\" a 1.00", log, StringComparison.Ordinal);                        // the state line at 1040 and 1100: the short toast on show, in English (package K24: no file name)
        Assert.DoesNotContain("Tsukihime_", log.Split("toast \"")[1].Split('"')[0], StringComparison.Ordinal);
        Assert.DoesNotContain("screenshot failed", log, StringComparison.Ordinal);
        Assert.DoesNotContain("(notice off)", log, StringComparison.Ordinal);

        string[] files = Directory.GetFiles(folder, "Tsukihime_*.png").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(2, files.Length);
        Assert.Matches(@"Tsukihime_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}(-\d+)?\.png$", Path.GetFileName(files[0]));
        (int Width, int Height, byte[] Rgba) first = ReadPng(files[0]);
        (int Width, int Height, byte[] Rgba) second = ReadPng(files[1]);
        Assert.Equal((1280, 720), (first.Width, first.Height));
        Assert.Equal((1280, 720), (second.Width, second.Height));
        Assert.Equal(255, first.Rgba[3]);
        Assert.True(first.Rgba.Where((_, i) => i % 4 != 3).Any(v => v > 40), "the picture is not black");

        // the toast sits in the bottom left corner (package K24: canvas x 40..~240, y 968..1008, left of the page and above the guide bar -> two thirds of that in the 1280 x 720
        // picture): the two captures agree there like anywhere else, while the frame drawn with the toast differs from the one before it in that region far more than elsewhere
        double keyToast = DifferentShare(first, second, 30, 648, 150, 669);
        double keyElsewhere = DifferentShare(first, second, 270, 200, 740, 470);
        (int Width, int Height, byte[] Rgba) before = ReadPng(Path.Combine(save, "before.png"));
        (int Width, int Height, byte[] Rgba) after = ReadPng(Path.Combine(save, "after.png"));
        double frameToast = DifferentShare(before, after, 44, 972, 224, 1004);
        double frameElsewhere = DifferentShare(before, after, 400, 300, 1100, 700);
        double frameBeside = DifferentShare(before, after, 44, 900, 224, 960);                                     // the same backdrop just above the toast, without it
        Assert.True(keyToast < 0.1, $"the second capture shows the first toast: {keyToast:P1} of its pixels differ (elsewhere {keyElsewhere:P1})");
        Assert.True(frameToast > 0.3, $"the frame after the capture shows no toast: {frameToast:P1} of the pixels differ (elsewhere {frameElsewhere:P1}, just above the toast {frameBeside:P1})");
        Assert.True(frameElsewhere < 0.1, $"the config screen is not still enough for this check: {frameElsewhere:P1}");
        Assert.True(frameBeside < 0.1, $"the backdrop above the toast is not still enough for this check: {frameBeside:P1}");
        // the toast is small: nothing of it reaches the page (x >= 407) or the guide bar (y >= 1023)
        Assert.True(DifferentShare(before, after, 407, 940, 700, 1023) < 0.05, "the toast reaches into the page");
        Assert.True(DifferentShare(before, after, 0, 1023, 400, 1080) < 0.05, "the toast reaches into the guide bar");
    }

    [GameDataFact]
    public void WithTheScreenshotNoticeOffTheKeyStillSavesThePictureAndShowsNoToast()
    {
        // package K24: Config > System > Screenshot Notice = Off (settings.ini screenshot_notice=off): the PNG is written, the log says so, no toast at all
        string save = StartedProfile("profile-shot-quiet");
        string folder = Path.Combine(save, "pictures");
        AppSettings.Save(Path.Combine(save, AppSettings.FileName), new AppSettingsData(ScreenshotNotice: false));
        (int code, string log) = Run(save, "--script", $"{ToConfig};1000:screenshot;1040:log;1120:quit", "--screenshot-dir", folder);
        Assert.Equal(0, code);
        Assert.Contains("screenshot requested (script)", log, StringComparison.Ordinal);
        Assert.Contains("(notice off)", log, StringComparison.Ordinal);
        Assert.Contains("screenshot saved: ", log, StringComparison.Ordinal);
        Assert.Contains("toast -", log, StringComparison.Ordinal);                                                // the state line at 1040: no toast
        Assert.DoesNotContain("toast \"Screenshot", log, StringComparison.Ordinal);
        string[] files = Directory.GetFiles(folder, "Tsukihime_*.png");
        Assert.Single(files);
        Assert.Equal((1280, 720), (ReadPng(files[0]).Width, ReadPng(files[0]).Height));
    }

    [GameDataFact]
    public void TheWindowsCloseRequestAsksTheQuitQuestionAndQuitsOnYesOrOnASecondRequest()
    {
        string save = StartedProfile("profile-close");
        // on the title: close, the question is up (the state line names the dialog), a second close quits at once
        (int code, string log) = Run(save, "--script", "60:Enter;300:Enter;700:close;740:log;760:close;900:log;1000:quit");
        Assert.Equal(0, code);
        Assert.Contains("close requested: asking the quit question", log, StringComparison.Ordinal);
        Assert.Contains("[TitleScreen>ConfirmDialog]", log, StringComparison.Ordinal);
        Assert.Contains("close requested again: quitting", log, StringComparison.Ordinal);
        Assert.Contains("quit requested (the window's close, confirmed)", log, StringComparison.Ordinal);
        Assert.DoesNotContain("[    900] state", log, StringComparison.Ordinal);                                     // the run ended at the second close
        Assert.DoesNotContain("script quit", log, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(save, "save.dat")));
        Assert.Empty(Directory.GetFiles(save, "*.tmp"));
        Assert.Empty(Directory.GetFiles(save, "*.broken*"));

        // No (the cursor starts on it) returns to the title and the run goes on; later Left, Enter is Yes and the run ends with the system data written
        (code, log) = Run(save, "--script", "60:Enter;300:Enter;700:close;760:Enter;840:log;900:close;960:Left;980:Enter;1100:log;1200:quit");
        Assert.Equal(0, code);
        Assert.Contains("[    840] state: ui Title [TitleScreen]", log, StringComparison.Ordinal);                 // the dialog is gone, the title is back
        Assert.Contains("quit requested (the window's close, confirmed)", log, StringComparison.Ordinal);
        Assert.DoesNotContain("[   1100] state", log, StringComparison.Ordinal);
        Assert.DoesNotContain("close requested again", log, StringComparison.Ordinal);
        Assert.True(SaveTools.Summarize(Path.Combine(save, "save.dat")).Slots >= 0);                              // the file reads back after the quit

        // in the story (CONTINUE at the title): the question opens over the page and the second close quits
        (code, log) = Run(save, "--script", "60:Enter;300:Enter;700:Enter;1200:log;1300:close;1340:log;1360:close;1500:quit", "--movies", "virtual");
        Assert.Equal(0, code);
        Assert.Contains("[   1200] state: ui Story []", log, StringComparison.Ordinal);
        Assert.Contains("[   1340] state: ui Overlay [ConfirmDialog]", log, StringComparison.Ordinal);
        Assert.Contains("quit requested (the window's close, confirmed)", log, StringComparison.Ordinal);
    }

    [GameDataFact]
    public void CopyTextToClipboardWritesEachMessageOnceAsPlainTextWhenItIsOn()
    {
        string save = StartedProfile("profile-clip");
        string clip = Path.Combine(save, "clipboard.txt");
        // off (the default): nothing, although messages are shown; the hook point for text hookers (package K23) is on by default and sees the same lines
        (int code, string log) = Run(save, "--autoplay", "click", "--exit-after", "900", "--clipboard-file", clip, "--script", "890:log");
        Assert.Equal(0, code);
        Assert.False(File.Exists(clip));
        Assert.DoesNotContain("clipboard:", log, StringComparison.Ordinal);
        int hooked = log.Split("hooker: message, ").Length - 1;
        Assert.True(hooked >= 3, $"{hooked} lines went through the hook point");
        Assert.Contains("hooked " + hooked, log.Split('\n').Last(l => l.Contains("state:", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("websocket off", log, StringComparison.Ordinal);
        Assert.DoesNotContain("hooker: the message text could not be passed", log, StringComparison.Ordinal);

        // on, through settings.ini: every message the story shows goes to the file, once, without markup; the hook point switched off sees nothing
        AppSettings.Save(Path.Combine(save, AppSettings.FileName), new AppSettingsData(CopyText: true, VSync: false, TextHooker: false));
        (code, log) = Run(save, "--autoplay", "click", "--exit-after", "900", "--clipboard-file", clip, "--script", "890:log");
        Assert.Equal(0, code);
        Assert.DoesNotContain("hooker: message", log, StringComparison.Ordinal);
        Assert.Contains("hooked 0", log, StringComparison.Ordinal);
        Assert.True(File.Exists(clip));
        string[] texts = File.ReadAllText(clip).Split("\n---\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.True(texts.Length >= 3, $"{texts.Length} texts copied");
        Assert.Equal(texts.Length, texts.Distinct(StringComparer.Ordinal).Count());
        Assert.All(texts, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t));
            Assert.DoesNotContain('^', t);
            Assert.DoesNotContain("@w", t, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', t);
        });
        int messages = log.Split("clipboard: message, ").Length - 1;
        Assert.Equal(texts.Length, messages);
        Assert.Contains("copies " + texts.Length, log.Split('\n').Last(l => l.Contains("state:", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Equal(1, log.Split("clipboard: the first text of the run was copied (toast shown)").Length - 1);    // the toast once, at the first copy (gone again by step 890)
        Assert.Equal(false, AppSettings.Load(Path.Combine(save, AppSettings.FileName)).VSync);                     // the other key stays
    }

    [GameDataFact]
    public void MuteInBackgroundFadesTheMixOutWhenTheFocusGoesAndBackInWhenItReturns()
    {
        string save = StartedProfile("profile-mute");
        // off: a lost focus changes nothing
        (int code, string log) = Run(save, "--script", "300:blur;400:log;500:quit");
        Assert.Equal(0, code);
        Assert.DoesNotContain("audio: fading", log, StringComparison.Ordinal);
        Assert.Contains("master gain 1.00 -> 1", log, StringComparison.Ordinal);

        // on: the master gain glides to 0 after the focus is lost (the muted run pulls 800 mixer frames a step: the half-second fade is over within 30 steps) and back to 1 after it returns
        AppSettings.Save(Path.Combine(save, AppSettings.FileName), new AppSettingsData(MuteInBackground: true));
        (code, log) = Run(save, "--script", "300:blur;310:log;400:log;500:focus;510:log;600:log;700:quit");
        Assert.Equal(0, code);
        Assert.Contains("[    300] audio: fading out (the window is in the background)", log, StringComparison.Ordinal);
        Assert.Contains("[    500] audio: fading in (the window has the focus)", log, StringComparison.Ordinal);
        string[] states = log.Split('\n').Where(l => l.Contains("state: ui", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, states.Length);
        Assert.Contains("-> 0", states[0], StringComparison.Ordinal);                                               // 310: on its way down
        Assert.Contains("master gain 0.00 -> 0", states[1], StringComparison.Ordinal);                               // 400: silent
        Assert.Contains("-> 1", states[2], StringComparison.Ordinal);                                               // 510: on its way up
        Assert.Contains("master gain 1.00 -> 1", states[3], StringComparison.Ordinal);                               // 600: back
    }

    [GameDataFact]
    public void TheOpacitySliderWritesThePs4WindowOpacityByteAndTheClipboardRowTheSettingsKey()
    {
        string save = StartedProfile("profile-opacity");
        // the K19 walk to the Display Mode row (16 Downs), six more Downs: Window Size, Screenshot Notice (package K24), Copy Text to Clipboard, Text Hooker Output, Text WebSocket
        // (package K23), Text Window Opacity; Left twice = 90 %; then Up three times and Right, Enter = clipboard On
        var steps = new List<string> { ToConfig };
        for (int i = 0; i < 22; i++)
        {
            steps.Add($"{800 + (12 * i)}:Down");
        }

        string walk = string.Join(";", steps);
        (int code, string log) = Run(save, "--script", $"{walk};1064:Left;1084:Left;1100:log;1120:Up;1140:Up;1160:Up;1180:Right;1200:Enter;1220:log;1240:Esc;1300:quit");
        Assert.Equal(0, code);
        Assert.Contains("ui Title [TitleScreen>ConfigScreen]", log, StringComparison.Ordinal);
        Assert.Contains("opacity 230", log, StringComparison.Ordinal);                                               // 90 % of 255, rounded
        Assert.Contains("copy_text on saved to settings.ini", log, StringComparison.Ordinal);
        Assert.Equal(true, AppSettings.Load(Path.Combine(save, AppSettings.FileName)).CopyText);
        Assert.Equal(230, Tsuki.Engine.Save.SaveFile.ReadWithFallback(Path.Combine(save, "save.dat"), out _)!.SystemData.Config.Hud10);   // written when the screen closed
    }

    [GameDataFact]
    public void TheTextWebSocketServesEachLineToAConnectedPageWhenItIsOn()
    {
        // package K23: a free port of this machine, the switch and the port in settings.ini, the game autoplaying; a client connects while it runs (as texthooker-ui would)
        string save = StartedProfile("profile-ws");
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        AppSettings.Save(Path.Combine(save, AppSettings.FileName), new AppSettingsData(TextWebSocket: true, TextWebSocketPort: port));
        (Process process, string logPath) = Start(save, "--autoplay", "click", "--exit-after", "1500", "--script", "1490:log");
        var received = new List<string>();
        using (process)
        {
            // a ClientWebSocket whose connect failed is aborted and cannot connect again (the retry would throw ObjectDisposedException), so every attempt takes a fresh
            // one: the game needs a moment to reach the step that starts the server, longer on a busy machine (package K22)
            System.Net.WebSockets.ClientWebSocket? connectedClient = null;
            for (int attempt = 0; attempt < 300 && connectedClient is null && !process.HasExited; attempt++)
            {
                var candidate = new System.Net.WebSockets.ClientWebSocket();
                try
                {
                    using var cts = new CancellationTokenSource(2000);
                    candidate.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), cts.Token).GetAwaiter().GetResult();
                    connectedClient = candidate;
                }
                catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or OperationCanceledException)
                {
                    candidate.Dispose();
                    Thread.Sleep(100);
                }
            }

            Assert.True(connectedClient is not null, "the game's WebSocket never answered");
            using System.Net.WebSockets.ClientWebSocket client = connectedClient;
            var buffer = new byte[64 * 1024];
            try
            {
                while (client.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    using var cts = new CancellationTokenSource(120_000);
                    var text = new System.Text.StringBuilder();
                    System.Net.WebSockets.WebSocketReceiveResult result;
                    do
                    {
                        result = client.ReceiveAsync(buffer, cts.Token).GetAwaiter().GetResult();
                        if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                        {
                            break;
                        }

                        text.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    {
                        break;
                    }

                    received.Add(text.ToString());
                }
            }
            catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or OperationCanceledException or IOException)
            {
                // the game ended and took the socket with it
            }

            Assert.True(process.WaitForExit(240_000), "the game did not exit");
            Assert.Equal(0, process.ExitCode);
        }

        string log = File.ReadAllText(logPath);
        Assert.Contains($"websocket: listening on ws://127.0.0.1:{port}", log, StringComparison.Ordinal);
        Assert.Contains("websocket: client connected (1 connected)", log, StringComparison.Ordinal);
        Assert.True(received.Count >= 2, $"{received.Count} frames received");
        Assert.All(received, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t));
            Assert.DoesNotContain('^', t);
            Assert.DoesNotContain("@w", t, StringComparison.Ordinal);
        });
        int sent = log.Split("websocket: message, ").Length - 1;
        Assert.True(sent >= received.Count - 1, $"{sent} broadcasts for {received.Count} frames");                    // a late join gets the latest line once more
        Assert.Contains($"websocket port {port} (", log, StringComparison.Ordinal);                                  // the state line at 1490
        Assert.Contains("hooker: message, ", log, StringComparison.Ordinal);                                         // the other output ran beside it
    }

    [GameDataFact]
    public void TheArcueidIconIsBuiltIntoTheProfileOnTheFirstStartAndReusedOnTheNext()
    {
        // package K23: a fresh profile builds icons\tsukihime.ico from the game's own sprite on a worker thread and the window takes it; the next start finds it
        string save = Path.Combine(_root, "profile-icon");
        Directory.CreateDirectory(save);
        (int code, string log) = Run(save, "--autoplay", "skip", "--exit-after", "900");
        Assert.Equal(0, code);
        Assert.Contains("face icon: built from ARU_A_00_00_N00.MZP", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("window icon: Arcueid from " + Path.Combine(save, "icons", "tsukihime.ico"), log, StringComparison.Ordinal);
        Assert.Contains("SDL 256 px set, per-size frames set", log, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(save, "icons", "tsukihime.ico")));
        Assert.True(File.Exists(Path.Combine(save, "icons", "tsukihime.src")));
        Assert.Equal(Tsuki.Data.Icons.FaceIcon.Sizes, Tsuki.Data.Icons.FaceIcon.DecodeIco(File.ReadAllBytes(Path.Combine(save, "icons", "tsukihime.ico"))).Select(f => f.Size));
        Assert.Contains("window icon: the executable's icon group", log, StringComparison.Ordinal);                 // the crescent first, until the folder is open and the face is ready

        (code, log) = Run(save, "--script", "600:quit");
        Assert.Equal(0, code);
        Assert.DoesNotContain("face icon: built", log, StringComparison.Ordinal);
        Assert.Contains("window icon: Arcueid from ", log, StringComparison.Ordinal);
    }
}
