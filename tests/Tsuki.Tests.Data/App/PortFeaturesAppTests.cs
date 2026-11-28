using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using Tsuki.App.Services;
using Tsuki.Engine.Trophies;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Data.App;

/// <summary>
/// Package K21 on the real game, in the real program (hidden, muted, a temporary profile, the pictures in temp folders): the screenshot key writes a PNG of the
/// window's size without the toast and shows the toast afterwards; the window's close request asks the quit question and quits on Yes or on a second request;
/// Copy Text to Clipboard writes each message (to the file the verification runs use instead of the desktop clipboard); Mute in Background fades the mixer out on a
/// lost focus and back in; the Text Window Opacity slider writes the PS4's byte. A fresh profile goes PRESS ANY KEY, cautions, story, so each test first lets the
/// autoplay run into the story once (the title then shows its menu).
/// v0.1.9, the achievements: the notice at the top right once the player has turned it on (one after the other, never in the screenshot key's picture, not at all
/// with the switch off or unset), a chapter finished for real earns its achievement and writes trophies.ini, the next start finds in the save what the record lacks
/// and shows it on the title one achievement at a time, a run that writes pictures on its own schedule shows no notice, and the chime is said in the log.
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

    private (Process Process, string LogPath) Start(string saveDir, params string[] more) => Start(null, saveDir, more);

    private (Process Process, string LogPath) Start(IReadOnlyDictionary<string, string>? environment, string saveDir, params string[] more)
    {
        string game = GameFolder.Require();
        string app = Path.Combine(AppContext.BaseDirectory, GameLauncher.AppFileName);
        string log = Path.Combine(_root, "run-" + Guid.NewGuid().ToString("N") + ".log");
        var info = new ProcessStartInfo(app) { WorkingDirectory = Path.GetDirectoryName(app)!, UseShellExecute = false };
        info.Environment["SDL_AUDIO_DRIVER"] = "dummy";
        foreach ((string key, string value) in environment ?? new Dictionary<string, string>())
        {
            info.Environment[key] = value;
        }

        foreach (string a in new[] { "--game", game, "--log", log, "--hidden", "--mute", "--fast", "--save-dir", saveDir, "--windowed" }.Concat(more))
        {
            info.ArgumentList.Add(a);
        }

        return (Process.Start(info)!, log);
    }

    private (int ExitCode, string Log) Run(string saveDir, params string[] more) => Run(null, saveDir, more);

    private (int ExitCode, string Log) Run(IReadOnlyDictionary<string, string>? environment, string saveDir, params string[] more)
    {
        (Process process, string log) = Start(environment, saveDir, more);
        using (process)
        {
            WaitOrStop(process);
            return (process.ExitCode, File.ReadAllText(log));
        }
    }

    // a game that does not exit within the wait is stopped (the process this test started and its children, nothing else) before the test fails: a hidden, muted
    // game left running on the machine could only be found by its process id
    private static void WaitOrStop(Process process)
    {
        if (!process.WaitForExit(240_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
            Assert.Fail("the game did not exit");
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
        Assert.DoesNotContain("(no message", log, StringComparison.Ordinal);

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
        // package K24: Config > System > Screenshot Notify = Off (settings.ini screenshot_notice=off): the PNG is written, the log says so, no toast at all
        string save = StartedProfile("profile-shot-quiet");
        string folder = Path.Combine(save, "pictures");
        AppSettings.Save(Path.Combine(save, AppSettings.FileName), new AppSettingsData(ScreenshotNotice: false));
        (int code, string log) = Run(save, "--script", $"{ToConfig};1000:screenshot;1040:log;1120:quit", "--screenshot-dir", folder);
        Assert.Equal(0, code);
        Assert.Contains("screenshot requested (script)", log, StringComparison.Ordinal);
        Assert.Contains("(1280 x 720) (no message: Screenshot Notify is off)", log, StringComparison.Ordinal);
        Assert.Contains("screenshot saved: ", log, StringComparison.Ordinal);
        Assert.Contains("toast -", log, StringComparison.Ordinal);                                                // the state line at 1040: no toast
        Assert.DoesNotContain("toast \"Screenshot", log, StringComparison.Ordinal);
        string[] files = Directory.GetFiles(folder, "Tsukihime_*.png");
        Assert.Single(files);
        Assert.Equal((1280, 720), (ReadPng(files[0]).Width, ReadPng(files[0]).Height));
    }

    // ---- v0.1.9: the achievements ---------------------------------------------------------------------------------------------------------------------

    // the state line's field of the achievement notice at a step: <id> "<name>" <icon|emblem|waiting> a <alpha> +<waiting>, or "-"
    private static string TrophyField(string log, int step)
    {
        string line = Assert.Single(log.Split('\n'), l => l.StartsWith($"[{step,7}] state:", StringComparison.Ordinal));
        int at = line.IndexOf(", trophy ", StringComparison.Ordinal);
        Assert.True(at >= 0, $"the state line of step {step} has no trophy field");
        int end = line.IndexOf(", master gain", at, StringComparison.Ordinal);
        Assert.True(end > at);
        return line[(at + ", trophy ".Length)..end];
    }

    private static int Count(string log, string marker) => log.Split(marker).Length - 1;

    // the player's switches of the notice and its sound in the profile's settings.ini (v0.1.9: both off until the player turns them on)
    private static void TurnOn(string save, bool notice = true, bool sound = false) =>
        AppSettings.Save(Path.Combine(save, AppSettings.FileName), new AppSettingsData(TrophyNotice: notice, TrophySound: sound));

    [GameDataFact]
    public void TheAchievementNoticeShowsAtTheTopRightOneAfterTheOtherAndIsNeverInTheScreenshotKeysPicture()
    {
        // the script verb trophy=<id> goes through the path an earned achievement takes (the switch, the log line, the icon's worker, the queue) and records
        // nothing. On the config screen (a still): a frame shot before anything, a gold achievement, the platinum and a silver one behind it, a frame shot and the
        // screenshot key with the first up, the others in their turn (a frame shot of the third), a frame shot and the key again when all have gone
        string save = StartedProfile("profile-trophy");
        string folder = Path.Combine(save, "pictures");
        TurnOn(save);
        (int code, string log) = Run(save, "--lang", "en", "--script", $"{ToConfig};990:shot=before;1000:trophy=14;1001:trophy=0;1002:trophy=5;1040:log;1040:shot=notice;1050:screenshot;1210:log;1400:log;1400:shot=third;1670:shot=after;1672:log;1672:screenshot;1710:quit", "--shot-dir", save, "--screenshot-dir", folder);
        Assert.Equal(0, code);
        Assert.Contains("ui Title [TitleScreen>ConfigScreen]", log, StringComparison.Ordinal);
        Assert.DoesNotContain("achievement cups", log, StringComparison.Ordinal);                                    // no picture of the PS4's trophy file is read or shown
        Assert.Contains("[   1000] trophy 14 earned", log, StringComparison.Ordinal);
        Assert.Contains("[   1001] trophy 0 earned", log, StringComparison.Ordinal);
        Assert.Contains("[   1002] trophy 5 earned", log, StringComparison.Ordinal);
        Assert.DoesNotContain("(no message", log, StringComparison.Ordinal);

        // each of the three has its own icon, cut from the player's own pictures by a worker when its notice was asked for (a picture of the story for the two
        // chapters, the title's moon for the platinum), at the size the notice draws it
        foreach (int id in new[] { 14, 0, 5 })
        {
            Assert.Equal(1, Count(log, $"] achievement icon {id}: 72 px from the game's pictures in "));
        }

        Assert.Equal(3, Count(log, "] achievement icon "));

        // the first: its name is the game's own title of the chapter it finishes (not the day number in front of it, not the port's stand-in), the picture is its
        // icon (not the gold emblem), two more wait
        string first = TrophyField(log, 1040);
        Assert.StartsWith("14 \"", first, StringComparison.Ordinal);
        Assert.EndsWith("\" icon a 1.00 +2", first, StringComparison.Ordinal);
        string name = first.Split('"')[1];
        Assert.True(name.Length >= 2 && !char.IsDigit(name[0]) && !name.Contains("14", StringComparison.Ordinal), "the first notice's name is not a chapter title");
        // then the platinum, with the port's own name and its icon, then the silver chapter with its icon, and nothing after it
        Assert.Equal("0 \"All Achievements\" icon a 1.00 +1", TrophyField(log, 1210));
        string third = TrophyField(log, 1400);
        Assert.StartsWith("5 \"", third, StringComparison.Ordinal);
        Assert.EndsWith("\" icon a 1.00 +0", third, StringComparison.Ordinal);
        Assert.NotEqual(name, third.Split('"')[1]);
        Assert.Equal("-", TrophyField(log, 1672));

        // nothing was recorded: the verb only shows
        Assert.False(File.Exists(Path.Combine(save, TrophyLog.FileName)));

        // the notice's box is at the top right (canvas x .. 1880, y 18 .. 102; at least 300 wide): a frame with a notice differs from the one before there, and
        // nowhere else (the config screen's backdrop moves a little: about one pixel in a hundred); the frame after the notices is the one before again
        (int Width, int Height, byte[] Rgba) before = ReadPng(Path.Combine(save, "before.png"));
        (int Width, int Height, byte[] Rgba) after = ReadPng(Path.Combine(save, "after.png"));
        foreach (string shot in new[] { "notice", "third" })
        {
            (int Width, int Height, byte[] Rgba) notice = ReadPng(Path.Combine(save, shot + ".png"));
            Assert.Equal((1920, 1080), (notice.Width, notice.Height));
            double inBox = DifferentShare(before, notice, 1584, 22, 1876, 98);
            Assert.True(inBox > 0.9, $"{shot}: the frame shows no notice at the top right: {inBox:P1} of the box's pixels differ");
            Assert.True(DifferentShare(before, notice, 1300, 106, 1920, 170) < 0.05, $"{shot}: the notice reaches below y 104 (the first line of the page text)");
            Assert.True(DifferentShare(before, notice, 1884, 0, 1920, 110) < 0.05, $"{shot}: the notice reaches past its right edge");
            Assert.True(DifferentShare(before, notice, 1300, 0, 1920, 15) < 0.05, $"{shot}: the notice reaches above its top edge");
            Assert.True(DifferentShare(before, notice, 0, 0, 1180, 110) < 0.05, $"{shot}: the notice reaches into the left of the top strip (the mode banner's place)");
            Assert.True(DifferentShare(before, notice, 0, 110, 1920, 1080) < 0.05, $"{shot}: the frame with the notice differs below the top strip");
        }

        double gone = DifferentShare(before, after, 1584, 22, 1876, 98);
        Assert.True(gone < 0.05, $"the notice is still in the frame after all three have gone: {gone:P1}");

        // the screenshot key's pictures (the window's 1280 x 720: the box is at x .. 1253, y 12 .. 68): the one taken with the notice up shows none
        string[] files = Directory.GetFiles(folder, "Tsukihime_*.png").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(2, files.Length);
        (int Width, int Height, byte[] Rgba) withNotice = ReadPng(files[0]);
        (int Width, int Height, byte[] Rgba) without = ReadPng(files[1]);
        Assert.Equal((1280, 720), (withNotice.Width, withNotice.Height));
        double captured = DifferentShare(withNotice, without, 1056, 15, 1250, 65);
        Assert.True(captured < 0.05, $"the screenshot taken while the notice was up shows it: {captured:P1} of the box's pixels differ from the screenshot without one");
    }

    [GameDataFact]
    public void ANoticeWhoseIconCannotBeMadeShowsItsGradesEmblemInTheIconsPlace()
    {
        // TSUKI_TROPHY_ICONS=none makes the icon maker answer "none", as a game folder without the achievement's picture does: the worker runs and says so, and the
        // notice shows the emblem of its grade at once (no trophy file is read for it); the frame shows the notice at the top right as with an icon
        string save = StartedProfile("profile-trophy-emblem");
        TurnOn(save);
        var noIcons = new Dictionary<string, string> { ["TSUKI_TROPHY_ICONS"] = "none" };
        (int code, string log) = Run(noIcons, save, "--lang", "en", "--script", $"{ToConfig};990:shot=before;1000:trophy=14;1040:log;1040:shot=emblem;1100:quit", "--shot-dir", save);
        Assert.Equal(0, code);
        Assert.Contains("achievement icons: none (TSUKI_TROPHY_ICONS=none): every message shows its grade's emblem", log, StringComparison.Ordinal);
        Assert.Contains("[   1000] trophy 14 earned", log, StringComparison.Ordinal);
        Assert.Equal(1, Count(log, "] achievement icon 14: not in this game folder's pictures; the message shows the grade's emblem"));
        Assert.DoesNotContain("achievement cups", log, StringComparison.Ordinal);
        string field = TrophyField(log, 1040);
        Assert.StartsWith("14 \"", field, StringComparison.Ordinal);
        Assert.EndsWith("\" emblem a 1.00 +0", field, StringComparison.Ordinal);
        (int Width, int Height, byte[] Rgba) before = ReadPng(Path.Combine(save, "before.png"));
        (int Width, int Height, byte[] Rgba) emblem = ReadPng(Path.Combine(save, "emblem.png"));
        double inBox = DifferentShare(before, emblem, 1584, 22, 1876, 98);
        Assert.True(inBox > 0.9, $"the frame shows no notice at the top right: {inBox:P1} of the box's pixels differ");
        Assert.True(DifferentShare(before, emblem, 0, 110, 1920, 1080) < 0.05, "the frame with the notice differs below the top strip");

        // the emblem itself is in the frame, pixel for pixel, in the icon's place: its 72 x 72 six pixels inside the box, so at y 24 and six pixels right of the
        // box's left edge (the box's width follows the name: the place is looked for along the row). The emblem's opaque pixels are the frame's; its transparent
        // ones (the gap inside its ring, the corners) show the box's dark ground; the box's two-pixel border is left of it and the page left of that. An empty place,
        // the emblem of another grade or one drawn anywhere else does not match
        Tsuki.Ui.UiComposedPicture art = Tsuki.Ui.UiAssets.TrophyEmblem(TrophyTable.Grade(14));
        Assert.Equal((Tsuki.Ui.UiTrophyNotice.IconSize, Tsuki.Ui.UiTrophyNotice.IconSize), (art.Width, art.Height));
        const int Inset = (int)(Tsuki.Ui.UiTrophyNotice.Height - Tsuki.Ui.UiTrophyNotice.IconSize) / 2;
        const int Top = (int)Tsuki.Ui.UiTrophyNotice.Top + Inset;
        (int x, double match) = BestMatch(emblem, art, Top, (int)Tsuki.Ui.UiTrophyNotice.LeftLimit + Inset, (int)Tsuki.Ui.UiTrophyNotice.Right - Inset - art.Width);
        Assert.True(match > 0.98, $"the grade's emblem is not in the frame at y {Top}: at best {match:P1} of its opaque pixels are there (x {x})");
        (int ground, int groundMatch) = (0, 0);
        ReadOnlySpan<byte> px = art.Rgba.Span;
        for (int j = 0; j < art.Height; j++)
        {
            for (int i = 0; i < art.Width; i++)
            {
                if (px[(((j * art.Width) + i) * 4) + 3] == 0)
                {
                    ground++;
                    groundMatch += Near(emblem, x + i, Top + j, BoxGround(Pixel(before, x + i, Top + j)), 12) ? 1 : 0;
                }
            }
        }

        Assert.True(ground > 100, $"the emblem has only {ground} transparent pixels");
        Assert.True(groundMatch > 0.98 * ground, $"the emblem's transparent pixels do not show the box's ground: {groundMatch} of {ground}");
        // the rows of the box's straight left side (its corners are rounded; the page under it moves a little, so a few pixels may differ)
        (int border, int page, int rows) = (0, 0, 0);
        for (int y = 34; y <= 86; y++, rows++)
        {
            border += (Near(emblem, x - Inset, y, BoxBorder(Pixel(before, x - Inset, y)), 12) ? 1 : 0) + (Near(emblem, x - Inset + 1, y, BoxBorder(Pixel(before, x - Inset + 1, y)), 12) ? 1 : 0);
            page += Near(emblem, x - Inset - 1, y, Pixel(before, x - Inset - 1, y), 8) ? 1 : 0;
        }

        Assert.True(border >= 0.95 * 2 * rows, $"the box's border is not {Inset} and {Inset - 1} pixels left of the emblem: {border} of {2 * rows} pixels");
        Assert.True(page >= 0.9 * rows, $"the box reaches further left than {Inset} pixels from the emblem: {rows - page} of {rows} pixels left of its border differ from the page");
    }

    // the notice's box (UiTrophyNotice.Draw): a pale blue at 0.8 over the page, inside its two-pixel border a dark blue at 0.9 over that
    private static (int R, int G, int B) BoxBorder((int R, int G, int B) page) => Over((158, 219, 255), 204, page);

    private static (int R, int G, int B) BoxGround((int R, int G, int B) page) => Over((8, 18, 41), 230, BoxBorder(page));

    private static (int R, int G, int B) Over((int R, int G, int B) top, int alpha, (int R, int G, int B) under) =>
        (((top.R * alpha) + (under.R * (255 - alpha)) + 127) / 255, ((top.G * alpha) + (under.G * (255 - alpha)) + 127) / 255, ((top.B * alpha) + (under.B * (255 - alpha)) + 127) / 255);

    private static (int R, int G, int B) Pixel((int Width, int Height, byte[] Rgba) frame, int x, int y)
    {
        int i = ((y * frame.Width) + x) * 4;
        return (frame.Rgba[i], frame.Rgba[i + 1], frame.Rgba[i + 2]);
    }

    private static bool Near((int Width, int Height, byte[] Rgba) frame, int x, int y, (int R, int G, int B) want, int tolerance)
    {
        (int r, int g, int b) = Pixel(frame, x, y);
        return Math.Abs(r - want.R) <= tolerance && Math.Abs(g - want.G) <= tolerance && Math.Abs(b - want.B) <= tolerance;
    }

    // the left edge in x0..x1 where the opaque pixels of a picture, its top at row y, are the frame's (every channel within 8) most often, and that share
    private static (int X, double Match) BestMatch((int Width, int Height, byte[] Rgba) frame, Tsuki.Ui.UiComposedPicture art, int y, int x0, int x1)
    {
        ReadOnlySpan<byte> px = art.Rgba.Span;
        var opaque = new List<(int I, int J, int R, int G, int B)>();
        for (int j = 0; j < art.Height; j++)
        {
            for (int i = 0; i < art.Width; i++)
            {
                int k = ((j * art.Width) + i) * 4;
                if (px[k + 3] == 255)
                {
                    opaque.Add((i, j, px[k], px[k + 1], px[k + 2]));
                }
            }
        }

        Assert.True(opaque.Count > art.Width * art.Height / 2, $"the picture has only {opaque.Count} opaque pixels");
        (int X, int Count) best = (x0, -1);
        for (int x = x0; x <= x1; x++)
        {
            int count = 0;
            foreach ((int i, int j, int r, int g, int b) in opaque)
            {
                count += Near(frame, x + i, y + j, (r, g, b), 8) ? 1 : 0;
            }

            if (count > best.Count)
            {
                best = (x, count);
            }
        }

        return (best.X, best.Count / (double)opaque.Count);
    }

    [GameDataFact]
    public void WithTheAchievementNoticeOffNothingIsShownAndTheLogSaysSo()
    {
        // Config > System > Achievement Notify = Off (settings.ini trophy_notice=off): the same verbs log and show nothing
        string save = StartedProfile("profile-trophy-quiet");
        AppSettings.Save(Path.Combine(save, AppSettings.FileName), new AppSettingsData(TrophyNotice: false));
        (int code, string log) = Run(save, "--lang", "en", "--script", $"{ToConfig};990:shot=before;1000:trophy=14;1001:trophies=20;1040:log;1040:shot=quiet;1100:quit", "--shot-dir", save);
        Assert.Equal(0, code);
        Assert.Contains("[   1000] trophy 14 earned (no message: Notify is off)", log, StringComparison.Ordinal);
        Assert.Contains("[   1001] trophies from the save's progress: 20 (no message: Notify is off)", log, StringComparison.Ordinal);
        Assert.Equal("-", TrophyField(log, 1040));
        Assert.DoesNotContain("achievement icon", log, StringComparison.Ordinal);                                   // no picture is read for a notice nobody sees
        double inBox = DifferentShare(ReadPng(Path.Combine(save, "before.png")), ReadPng(Path.Combine(save, "quiet.png")), 1584, 22, 1876, 98);
        Assert.True(inBox < 0.05, $"a notice is in the frame although the switch is off: {inBox:P1}");
        Assert.Equal(false, AppSettings.Load(Path.Combine(save, AppSettings.FileName)).TrophyNotice);               // the key stays as it was

        // a settings file that does not name the switch (every player's, until they turn it on): the same, and the sound switched on alone sounds nothing
        AppSettings.Save(Path.Combine(save, AppSettings.FileName), new AppSettingsData(TrophySound: true));
        (code, log) = Run(save, "--lang", "en", "--script", $"{ToConfig};990:shot=before;1000:trophy=14;1001:trophies=20;1040:log;1040:shot=quiet;1100:quit", "--shot-dir", save);
        Assert.Equal(0, code);
        Assert.Contains("[   1000] trophy 14 earned (no message: Notify is off)", log, StringComparison.Ordinal);
        Assert.Contains("[   1001] trophies from the save's progress: 20 (no message: Notify is off)", log, StringComparison.Ordinal);
        Assert.Equal("-", TrophyField(log, 1040));
        Assert.DoesNotContain("achievement icon", log, StringComparison.Ordinal);
        Assert.DoesNotContain("achievement chime", log, StringComparison.Ordinal);
        inBox = DifferentShare(ReadPng(Path.Combine(save, "before.png")), ReadPng(Path.Combine(save, "quiet.png")), 1584, 22, 1876, 98);
        Assert.True(inBox < 0.05, $"a notice is in the frame although the switch was never turned on: {inBox:P1}");
        Assert.Null(AppSettings.Load(Path.Combine(save, AppSettings.FileName)).TrophyNotice);
    }

    [GameDataFact]
    public void WithTheSoundOnTheLogSaysTheChimeOnceForANoticeAndOnceForTheTitlesCatchUp()
    {
        // trophy_notice=on and trophy_sound=on: the chime as a notice comes in (the run is muted: the log says so), at the sound-effect volume 6 dB down (a
        // fresh profile's sound effects are at 40: gain 0.200)
        string save = StartedProfile("profile-trophy-chime");
        TurnOn(save, sound: true);
        (int code, string log) = Run(save, "--lang", "en", "--script", $"{ToConfig};1000:trophy=14;1040:log;1100:quit");
        Assert.Equal(0, code);
        Assert.Contains("[   1000] trophy 14 earned", log, StringComparison.Ordinal);
        Assert.Equal(1, Count(log, "] achievement chime for achievement 14 (gain 0.200; silent: this run is muted)"));
        Assert.Equal(1, Count(log, "achievement chime"));
        Assert.EndsWith("\" icon a 1.00 +0", TrophyField(log, 1040), StringComparison.Ordinal);

        // the title's catch-up of three (the script verb takes the start's path): one notice each, one chime for all of them
        (code, log) = Run(save, "--lang", "en", "--script", "60:Enter;300:Enter;400:trophies=3;" + string.Join(";", Enumerable.Range(0, 12).Select(i => $"{500 + (30 * i)}:log")) + ";1000:quit");
        Assert.Equal(0, code);
        Assert.Equal(1, Count(log, "trophies from the save's progress: 3, one message each on the title (1, 2, 3)"));
        Assert.Equal(1, Count(log, "] achievement chime for achievement 1 (gain 0.200; silent: this run is muted)"));
        Assert.Equal(1, Count(log, "achievement chime"));
        string[] fields = [.. log.Split('\n').Where(l => l.Contains("] state:", StringComparison.Ordinal)).Select(l => l[(l.IndexOf(", trophy ", StringComparison.Ordinal) + 9)..l.IndexOf(", master gain", StringComparison.Ordinal)])];
        foreach (int id in new[] { 1, 2, 3 })
        {
            Assert.Contains(fields, f => f.StartsWith($"{id} \"", StringComparison.Ordinal));
        }
    }

    // the flowchart node whose _RDED earns achievement 1 (the last scene of the first chapter), reached by the tour of the verification runs: the skipping autoplay
    // plays it to its end within about 370 steps
    private const int FirstChapterEnd = 335;

    private string TourFile()
    {
        string tour = Path.Combine(_root, "tour.txt");
        File.WriteAllText(tour, FirstChapterEnd.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        return tour;
    }

    [GameDataFact]
    public void FinishingAChapterForRealEarnsItsAchievementAndTheNextStartFindsInTheSaveWhatTheRecordLacks()
    {
        // a fresh profile whose player turned the notice on; the story is played by the skipping autoplay from the start of the chapter's last scene over its _RDED
        // (the game's own trophy scene table)
        string save = Path.Combine(_root, "profile-earn");
        Directory.CreateDirectory(save);
        TurnOn(save);
        string record = Path.Combine(save, TrophyLog.FileName);
        DateTime started = DateTime.UtcNow.AddSeconds(-2);
        (int code, string log) = Run(save, "--lang", "en", "--autoplay", "skip", "--sweep-tour", TourFile(), "--tour-end", "--tour-cap", "120000", "--script", "440:log", "--exit-after", "520");
        Assert.Equal(0, code);
        Assert.Contains($"tour: node {FirstChapterEnd} (scene ", log, StringComparison.Ordinal);
        Assert.Equal(1, Count(log, "] trophy 1 earned"));
        Assert.DoesNotContain("(no message", log, StringComparison.Ordinal);
        Assert.DoesNotContain("achievements file not written", log, StringComparison.Ordinal);
        string field = TrophyField(log, 440);                                                                        // the notice is up over the next chapter's start, with the achievement's own icon
        Assert.StartsWith("1 \"", field, StringComparison.Ordinal);
        Assert.EndsWith("\" icon a 1.00 +0", field, StringComparison.Ordinal);
        Assert.Equal(1, Count(log, "] achievement icon 1: 72 px from the game's pictures in "));

        // the record beside the save file: achievement 1 with the UTC time of this run, nothing else
        TrophyLog earned = TrophyLog.Read(record, out bool damaged);
        Assert.False(damaged);
        Assert.Equal([1], earned.Ids);
        Assert.InRange(earned.EarnedUtc(1)!.Value, started, DateTime.UtcNow.AddSeconds(2));
        Assert.Empty(Directory.GetFiles(save, "*.tmp"));

        // the next start reads it back: no catch-up, no notice on the title (the state is logged once a second from before the title's menu is up: it comes up sooner or later with what the profile has seen)
        string toTitle = "60:Enter;300:Enter;" + string.Join(";", Enumerable.Range(0, 16).Select(i => $"{120 + (60 * i)}:log")) + ";1030:quit";
        (code, log) = Run(save, "--lang", "en", "--script", toTitle);
        Assert.Equal(0, code);
        Assert.DoesNotContain("already earned by the save's progress", log, StringComparison.Ordinal);
        Assert.DoesNotContain("trophies from the save's progress", log, StringComparison.Ordinal);
        Assert.Contains("ui Title [TitleScreen]", log, StringComparison.Ordinal);
        Assert.Equal(16, Count(log, ", trophy -, "));
        Assert.Equal(earned.EarnedUtc(1), TrophyLog.Read(record, out _).EarnedUtc(1));

        // a profile from before achievements were recorded (the record removed, the save's progress kept): the start adds achievement 1 without a date and the
        // title shows its notice, once its menu is up, with its icon, in the catch-up's short time (about 1.5 s)
        File.Delete(record);
        // (the state every ten steps from the boot notice on: where the title's menu comes up depends on what the profile has seen)
        string[] state = [.. Enumerable.Range(0, 95).Select(i => $"{100 + (10 * i)}:log")];
        string toTitleOften = "60:Enter;300:Enter;" + string.Join(";", state) + ";1100:quit";
        (code, log) = Run(save, "--lang", "en", "--script", toTitleOften);
        Assert.Equal(0, code);
        Assert.Contains("achievements: 1 already earned by the save's progress, recorded without a date (1)", log, StringComparison.Ordinal);
        Assert.Equal(1, Count(log, "trophies from the save's progress: 1, one message each on the title (1)"));
        Assert.Equal(0, Count(log, "] trophy 1 earned"));                                                            // not earned again: the notice is the catch-up's
        Assert.Equal(1, Count(log, "] achievement icon 1: 72 px from the game's pictures in "));                     // its own icon, as an earned one has
        string[] states = log.Split('\n').Where(l => l.Contains("] state:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(95, states.Length);
        string[] shown = states.Where(l => l.Contains(", trophy 1 \"", StringComparison.Ordinal) && l.Contains("\" icon a 1.00 +0, ", StringComparison.Ordinal)).ToArray();
        Assert.True(shown.Length is >= 5 and <= 8, $"the catch-up's notice was fully up in {shown.Length} of the state lines ten steps apart (its 70 steps up: 7)");
        Assert.All(shown, l => Assert.Contains("ui Title [TitleScreen]", l, StringComparison.Ordinal));               // on the title, not over what comes before it
        Assert.True(states.Count(l => l.Contains(", trophy 1 \"", StringComparison.Ordinal)) <= 11, "the catch-up's notice stayed longer than its 92 steps");
        TrophyLog caughtUp = TrophyLog.Read(record, out damaged);
        Assert.False(damaged);
        Assert.Equal([1], caughtUp.Ids);
        Assert.Null(caughtUp.EarnedUtc(1));
        Assert.Contains("1=earlier", File.ReadAllText(record), StringComparison.Ordinal);

        // and only once: the start after that says nothing
        (code, log) = Run(save, "--lang", "en", "--script", toTitle);
        Assert.Equal(0, code);
        Assert.DoesNotContain("already earned by the save's progress", log, StringComparison.Ordinal);
        Assert.DoesNotContain("trophies from the save's progress", log, StringComparison.Ordinal);
        Assert.Equal(16, Count(log, ", trophy -, "));

        // the same profile from before achievements with a settings file that does not name the switch: the start records it, the title shows nothing
        File.Delete(record);
        AppSettings.Save(Path.Combine(save, AppSettings.FileName), new AppSettingsData());
        (code, log) = Run(save, "--lang", "en", "--script", toTitle);
        Assert.Equal(0, code);
        Assert.Contains("achievements: 1 already earned by the save's progress, recorded without a date (1)", log, StringComparison.Ordinal);
        Assert.Equal(1, Count(log, "trophies from the save's progress: 1 (no message: Notify is off)"));
        Assert.DoesNotContain("achievement icon", log, StringComparison.Ordinal);
        Assert.Equal(16, Count(log, ", trophy -, "));
        Assert.Equal([1], TrophyLog.Read(record, out _).Ids);
    }

    [GameDataFact]
    public void ARunThatWritesPicturesOnItsOwnScheduleShowsNoNoticeAndItsFramesDoNotDependOnWhatItEarns()
    {
        // the pictures of the verification runs (--shot-at here; the route sweep and --shot-on-choice take the same switch) must not change with what a run earns:
        // the same run on a profile that earns achievement 1 at the chapter's end and on one whose record already holds it writes the same frames, byte for byte
        string tour = TourFile();
        string[] Frames(string name, bool alreadyEarned, out string log)
        {
            string save = Path.Combine(_root, name);
            Directory.CreateDirectory(save);
            TurnOn(save, sound: true);                                    // the player's switches on: the run's own rule is what keeps the notice (and its chime) out
            if (alreadyEarned)
            {
                var record = new TrophyLog();
                record.Add(1, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
                record.Write(Path.Combine(save, TrophyLog.FileName));
            }

            string shots = Path.Combine(save, "shots");
            (int code, log) = Run(save, "--lang", "en", "--autoplay", "skip", "--sweep-tour", tour, "--tour-end", "--tour-cap", "120000", "--shot-at", "300,380,420,480", "--shot-dir", shots, "--script", "420:log", "--exit-after", "500");
            Assert.Equal(0, code);
            string[] files = Directory.GetFiles(shots, "frame_*.png").Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(4, files.Length);
            return files;
        }

        string[] earning = Frames("profile-pictures-earning", alreadyEarned: false, out string earningLog);
        Assert.Contains("] trophy 1 earned (no message: this run writes pictures)", earningLog, StringComparison.Ordinal);
        Assert.Equal("-", TrophyField(earningLog, 420));
        Assert.DoesNotContain("achievement icon", earningLog, StringComparison.Ordinal);
        Assert.DoesNotContain("achievement chime", earningLog, StringComparison.Ordinal);
        Assert.Equal([1], TrophyLog.Read(Path.Combine(_root, "profile-pictures-earning", TrophyLog.FileName), out _).Ids);      // recorded all the same

        string[] holding = Frames("profile-pictures-holding", alreadyEarned: true, out string holdingLog);
        Assert.Equal(0, Count(holdingLog, "] trophy 1 earned"));
        for (int i = 0; i < earning.Length; i++)
        {
            Assert.Equal(Path.GetFileName(earning[i]), Path.GetFileName(holding[i]));
            Assert.True(File.ReadAllBytes(earning[i]).AsSpan().SequenceEqual(File.ReadAllBytes(holding[i])), $"{Path.GetFileName(earning[i])} differs between the run that earned the achievement and the run that already had it");
        }

        // the frames after the earn are not black, so that they could show a notice (the next chapter opens with its save question)
        (int Width, int Height, byte[] Rgba) late = ReadPng(earning[1]);
        Assert.True(late.Rgba.Where((_, i) => i % 4 != 3).Any(v => v > 40), "the frame after the chapter's end is black: it proves nothing");
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
        // the K19 walk to the Display Mode row (16 Downs), eight more Downs: Window Size, Screenshot Notify (package K24), Achievement Notify and Achievement Sound
        // (v0.1.9), Copy Text to Clipboard, Text Hooker Output, Text WebSocket (package K23), Text Window Opacity; Left twice = 90 %; then Up three times and Right,
        // Enter = clipboard On; Up once more, Enter = the achievement sound On; Up once more, Enter = the achievement notice On (both are off until the player
        // turns them on)
        var steps = new List<string> { ToConfig };
        for (int i = 0; i < 24; i++)
        {
            steps.Add($"{800 + (12 * i)}:Down");
        }

        string walk = string.Join(";", steps);
        (int code, string log) = Run(save, "--script", $"{walk};1084:Left;1104:Left;1120:log;1140:Up;1160:Up;1180:Up;1200:Right;1220:Enter;1240:log;1260:Up;1280:Right;1300:Enter;1320:Up;1340:Right;1360:Enter;1380:log;1400:Esc;1460:quit");
        Assert.Equal(0, code);
        Assert.Contains("ui Title [TitleScreen>ConfigScreen]", log, StringComparison.Ordinal);
        Assert.Contains("opacity 230", log, StringComparison.Ordinal);                                               // 90 % of 255, rounded
        Assert.Contains("copy_text on saved to settings.ini", log, StringComparison.Ordinal);
        Assert.Equal(true, AppSettings.Load(Path.Combine(save, AppSettings.FileName)).CopyText);
        Assert.Contains("trophy_sound on saved to settings.ini", log, StringComparison.Ordinal);                    // the row above the clipboard row
        Assert.Contains("trophy_notice on saved to settings.ini", log, StringComparison.Ordinal);                   // and the row above that
        Assert.True(log.IndexOf("trophy_sound on saved", StringComparison.Ordinal) < log.IndexOf("trophy_notice on saved", StringComparison.Ordinal));
        Assert.Equal((true, true), (AppSettings.Load(Path.Combine(save, AppSettings.FileName)).TrophySound, AppSettings.Load(Path.Combine(save, AppSettings.FileName)).TrophyNotice));
        Assert.Null(AppSettings.Load(Path.Combine(save, AppSettings.FileName)).ScreenshotNotice);                    // the other notice's row was not touched
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
            // one: the game needs a moment to reach the step that starts the server, longer on a busy machine (package K22). The connect is waited on synchronously:
            // in a full Data run the thread pool was starved and one 2 s attempt returned after 15 s, past the game's exit (2026-11-27), so the pool is sized first
            ThreadPool.GetMinThreads(out int workers, out int ports);
            ThreadPool.SetMinThreads(Math.Max(workers, 64), ports);
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

            WaitOrStop(process);
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
