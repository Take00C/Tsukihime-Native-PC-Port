// "Tsuki.App --smoke" (ROADMAP M0 scope 7 and acceptance 3; PACE 7.2 #1, H1): FFmpeg from the app
// folder with the atrac9 / h264 / aac decoders, an SDL3 window with a GL 3.3 core context, one
// shader compiled, linked and drawn as GLSL 3.30 and as GLSL ES 3.00, a virtual gamepad hot-plug,
// Alt+Enter fullscreen through the event path, and one second of the fixed 60 Hz loop. Exits 0
// only when every check passes.
using System.Globalization;
using System.Text;
using Tsuki.Media.Ffmpeg;
using Tsuki.Platform;
using Tsuki.Platform.Timing;
using Tsuki.Render.Gl;

namespace Tsuki.App;

internal static class Smoke
{
    private const int LoopSteps = 60;
    private const long FirstToggleFrame = 10;
    private const long SecondToggleFrame = 40;

    public static int Run(bool visible, string? reportPath)
    {
        var report = new SmokeReport();
        report.Info("app", $"Tsuki.App {typeof(Smoke).Assembly.GetName().Version}; .NET {Environment.Version}; {Environment.OSVersion}; folder {AppContext.BaseDirectory}");
        CheckFfmpeg(report);
        try
        {
            CheckWindowAndLoop(report, visible);
        }
        catch (Exception e)
        {
            report.Check("platform", false, e.GetType().Name + ": " + e.Message);
        }

        CheckAudio(report);
        report.Info("folder_dialog", "not opened by --smoke (it needs a person); exercised by --pick-folder");
        int failed = report.Failed;
        report.Line(failed == 0 ? $"SMOKE PASS ({report.Passed} checks)" : $"SMOKE FAIL ({failed} of {report.Passed + failed} checks failed)");
        if (reportPath is not null)
        {
            File.WriteAllText(reportPath, report.Text, new UTF8Encoding(false));
        }

        return failed == 0 ? 0 : 1;
    }

    private static void CheckFfmpeg(SmokeReport report)
    {
        try
        {
            FfmpegVersion v = FfmpegLibrary.Load();
            report.Check("ffmpeg.load", true, $"FFmpeg {v.Build}, libavcodec {v.Avcodec}, libavutil {v.Avutil}, license \"{v.License}\"");
            IReadOnlyList<string> modules = FfmpegLibrary.LoadedModulePaths();
            bool local = modules.Count > 0 && modules.All(m => InFolder(m, FfmpegLibrary.RootPath));
            report.Check("ffmpeg.app_folder", local, $"{modules.Count} FFmpeg modules loaded: {string.Join(", ", modules)}");
            foreach (string name in FfmpegLibrary.RequiredDecoders)
            {
                DecoderInfo? d = FfmpegLibrary.FindDecoder(name);
                report.Check("ffmpeg.decoder." + name, d is not null && d.Name == name, d is null ? "not found" : $"{d.Name} ({d.LongName}), {d.MediaType}, codec id {d.CodecId}");
            }
        }
        catch (Exception e)
        {
            report.Check("ffmpeg.load", false, e.GetType().Name + ": " + e.Message);
        }
    }

    private static void CheckWindowAndLoop(SmokeReport report, bool visible)
    {
        using GameWindow window = GameWindow.Create(new WindowOptions("Tsuki smoke (closes itself)", 640, 360, Hidden: !visible, Resizable: false, SwapInterval: 1));
        report.Info("sdl", $"SDL {Sdl.Version}, video driver {Sdl.VideoDriver}, window {(visible ? "visible" : "hidden")}, {window.PixelSize.Width}x{window.PixelSize.Height} pixels");
        string? sdlPath = Sdl.LibraryPath;
        bool sdlLocal = sdlPath is not null && Unprefixed(sdlPath).StartsWith(Unprefixed(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);
        report.Check("sdl.app_folder", sdlLocal,
            sdlPath is null ? "SDL3.dll came from the runtime's default probing, not the app folder" : "SDL3.dll loaded from " + sdlPath);
        GlDevice device = GlDevice.Create(window.GetProcAddress);
        GlContextInfo c = window.Context;
        bool sdlSays33Core = c.CoreProfile && c.Major == 3 && c.Minor >= 3;
        report.Check("gl.context", sdlSays33Core && device.Info.IsGl33Core, $"{c}; {device.Info}");

        var renders = new List<SmokeRender>();
        foreach (GlslDialect dialect in ShaderCompiler.AllDialects)
        {
            try
            {
                SmokeRender r = SmokeShader.Render(device, dialect);
                renders.Add(r);
                report.Check("shader." + dialect, r.Mismatches == 0,
                    $"compiled + linked + drawn {SmokeShader.Width}x{SmokeShader.Height}; {r.Mismatches} of {r.Pixels.Length} bytes differ from the expected pattern" +
                    (r.Log.Length > 0 ? "; log: " + r.Log.ReplaceLineEndings(" ") : ""));
            }
            catch (Exception e)
            {
                report.Check("shader." + dialect, false, e.GetType().Name + ": " + e.Message);
            }
        }

        if (renders.Count == 2)
        {
            report.Check("shader.dialects_agree", renders[0].Pixels.AsSpan().SequenceEqual(renders[1].Pixels), "GLSL 3.30 and ES 3.00 pixels identical");
        }

        using GamepadSet pads = GamepadSet.Create();
        report.Info("gamepad.at_start", pads.Connected.Count == 0 ? "no gamepad connected" : string.Join("; ", pads.Connected.Select(Describe)));
        var pump = new EventPump(window, pads);
        CheckHotPlug(report, pump);
        CheckLoopAndFullscreen(report, window, device, pump, visible);
    }

    private static void CheckHotPlug(SmokeReport report, EventPump pump)
    {
        var events = new List<PlatformEvent>();
        uint id = VirtualGamepad.Attach();
        PlatformEvent? added = PumpUntil(pump, events, e => e.Kind == PlatformEventKind.GamepadAdded && e.Gamepad?.InstanceId == id);
        report.Check("gamepad.hotplug_added", added is not null, added is { Gamepad: { } g } ? "virtual pad " + Describe(g) : "no SDL_EVENT_GAMEPAD_ADDED within 2 s");
        VirtualGamepad.Detach(id);
        PlatformEvent? removed = PumpUntil(pump, events, e => e.Kind == PlatformEventKind.GamepadRemoved && e.Gamepad?.InstanceId == id);
        report.Check("gamepad.hotplug_removed", removed is not null, removed is not null ? $"instance {id} closed" : "no SDL_EVENT_GAMEPAD_REMOVED within 2 s");
    }

    private static void CheckLoopAndFullscreen(SmokeReport report, GameWindow window, GlDevice device, EventPump pump, bool visible)
    {
        var toggles = new List<PlatformEvent>();
        int entered = 0, left = 0;
        var loop = new MainLoop(window, device, pump);
        LoopStats stats = loop.Run(
            tick =>
            {
                if (tick.Frame == FirstToggleFrame || tick.Frame == SecondToggleFrame)
                {
                    EventPump.PushFullscreenHotkey(window); // handled by the next pump, like a key press
                }

                return tick.Frame + 1 < LoopSteps;
            },
            e =>
            {
                if (e.Kind == PlatformEventKind.FullscreenToggled)
                {
                    toggles.Add(e);
                    window.Sync();
                }

                entered += e.Kind == PlatformEventKind.EnteredFullscreen ? 1 : 0;
                left += e.Kind == PlatformEventKind.LeftFullscreen ? 1 : 0;
                return true;
            },
            maxSeconds: 10);

        // late window events of the second toggle
        window.Sync();
        var tail = new List<PlatformEvent>();
        pump.Pump(tail);
        entered += tail.Count(e => e.Kind == PlatformEventKind.EnteredFullscreen);
        left += tail.Count(e => e.Kind == PlatformEventKind.LeftFullscreen);

        string? glError = device.TakeError();
        report.Check("loop.fixed_60hz", stats.Steps == LoopSteps && glError is null, stats + (glError is null ? "" : "; GL error " + glError));
        bool togglesOk = toggles.Count == 2 && toggles[0] is { Fullscreen: true, Accepted: true } && toggles[1] is { Fullscreen: false, Accepted: true };
        string detail = $"Alt+Enter x2 -> {string.Join(", ", toggles.Select(t => $"{(t.Fullscreen ? "on" : "off")}/{(t.Accepted ? "accepted" : "refused")}"))}; " +
                        $"window events: entered {entered}, left {left}; fullscreen now {window.IsFullscreen}";
        report.Check("window.alt_enter", togglesOk && (!visible || (entered >= 1 && left >= 1)) && !window.IsFullscreen, detail);
    }

    private static void CheckAudio(SmokeReport report)
    {
        // pull mode: a ramp through SDL's float32 stereo 48 kHz stream must come out bit for bit
        try
        {
            const int Frames = 4800;
            long next = 0;
            using var pull = AudioOutput.CreatePullOnly(buf =>
            {
                for (int i = 0; i < buf.Length; i++)
                {
                    buf[i] = next++ / 1048576f;
                }
            });
            var got = new float[Frames * AudioOutput.Channels];
            int n = pull.Pull(got);
            int bad = 0;
            for (int i = 0; i < got.Length; i++)
            {
                bad += got[i] == i / 1048576f ? 0 : 1;
            }

            report.Check("audio.pull_stream", n == Frames && bad == 0 && pull.CallbackError is null, $"{n} frames pulled, {bad} samples differ, {pull.Callbacks} callbacks");
        }
        catch (Exception e)
        {
            report.Check("audio.pull_stream", false, e.GetType().Name + ": " + e.Message);
        }

        // the default playback device, 250 ms of silence (information only: a PC may have no output device)
        try
        {
            using var device = AudioOutput.OpenDevice(buf => buf.Clear());
            device.Start();
            Thread.Sleep(250);
            device.Pause();
            report.Info("audio.device", $"driver {device.Driver}, \"{device.DeviceName}\", device {device.DeviceRate} Hz {device.DeviceChannels} ch, buffer {device.DeviceBufferFrames} frames; " +
                                        $"{device.Callbacks} callbacks, {device.FramesRendered} frames in 250 ms{(device.CallbackError is null ? "" : "; error " + device.CallbackError)}");
        }
        catch (Exception e)
        {
            report.Info("audio.device", "not opened: " + e.Message);
        }
    }

    /// <summary>True when <paramref name="file"/> sits directly in <paramref name="folder"/>; \\?\ prefixes (paths over MAX_PATH) are ignored.</summary>
    private static bool InFolder(string file, string folder) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Unprefixed(Path.GetDirectoryName(file) ?? "")),
            Path.TrimEndingDirectorySeparator(Unprefixed(folder)),
            StringComparison.OrdinalIgnoreCase);

    private static string Unprefixed(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    private static PlatformEvent? PumpUntil(EventPump pump, List<PlatformEvent> events, Func<PlatformEvent, bool> match)
    {
        long deadline = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline)
        {
            events.Clear();
            pump.Pump(events);
            foreach (PlatformEvent e in events)
            {
                if (match(e))
                {
                    return e;
                }
            }

            Thread.Sleep(5);
        }

        return null;
    }

    private static string Describe(GamepadInfo g) =>
        string.Create(CultureInfo.InvariantCulture, $"#{g.InstanceId} \"{g.Name}\" type {g.Type}, {g.VendorId:X4}:{g.ProductId:X4}, south {g.SouthLabel}, east {g.EastLabel}");

    private sealed class SmokeReport
    {
        private readonly StringBuilder _text = new();

        public int Passed { get; private set; }

        public int Failed { get; private set; }

        public string Text => _text.ToString();

        public void Check(string name, bool ok, string detail)
        {
            if (ok)
            {
                Passed++;
            }
            else
            {
                Failed++;
            }

            Line($"{(ok ? "PASS" : "FAIL")} {name}: {detail}");
        }

        public void Info(string name, string detail) => Line($"INFO {name}: {detail}");

        public void Line(string line)
        {
            _text.Append(line).Append('\n');
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }
}
