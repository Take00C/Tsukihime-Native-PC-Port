using System.Diagnostics;
using System.Globalization;
using Tsuki.Cli.Play;
using Tsuki.Data;
using Tsuki.Data.Eboot;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Stage;
using Tsuki.Platform;
using Tsuki.Render.Compositor;
using Tsuki.Render.Gl;
using Tsuki.Render.Grade;
using Tsuki.Render.Textures;

namespace Tsuki.Cli.Commands;

/// <summary>
/// <c>Tsuki.Cli effect-shot &lt;effect&gt; [--script K] [--lang ja|en|zc|zt] [--game DIR] [--out DIR] [--shots N] [--every M] [--max-frames N]</c> (package K7):
/// plays a real scene headless (skip mode, no sound) until the effect shows, then renders the frames through the GPU compositor in a hidden window and
/// writes 1920 x 1080 PNGs to <c>scratch/K7</c>. Effects: <c>noise</c> (STNS), <c>haze</c> (STRA), <c>rule</c> (STTR), <c>mask</c> (STCH mask layers),
/// <c>grade</c> (STEC, drawn graded and plain), <c>focus</c> (FCLN), <c>ghost</c> (FADE 40), <c>choice</c> (the choice glow). The window is closed on return.
/// </summary>
public sealed class EffectShotCommand : ICliCommand
{
    private static readonly Dictionary<string, int[]> DefaultScripts = new()
    {
        ["noise"] = [54, 71, 76, 77, 79, 88, 92, 112, 137, 143],
        ["haze"] = [1, 54, 69, 70, 71, 77, 82, 93, 95, 106],
        ["rule"] = [49, 76, 91, 98, 99, 106, 116, 138],
        ["mask"] = [1, 4, 6, 7, 8, 10, 12],
        ["grade"] = [1, 3, 4, 5, 6],
        ["focus"] = [173, 189, 250, 277, 494, 497],
        ["ghost"] = [1, 35, 54, 99, 136],
        ["choice"] = [3, 4, 6, 7, 8],
    };

    /// <inheritdoc/>
    public string Name => "effect-shot";

    /// <inheritdoc/>
    public string Summary => "render a real scene with a screen effect to PNGs through the GPU compositor; effect-shot <noise|haze|rule|mask|grade|focus|ghost|choice> [--script K] [--lang en] [--shots N] [--every M]";

    /// <inheritdoc/>
    public int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? effect = null;
        string? game = null;
        string? outDir = null;
        int? script = null;
        int shots = 3, every = 8, maxFrames = 40_000;
        bool off = false;
        Language lang = Language.En;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--game" when i + 1 < args.Length:
                    game = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    outDir = args[++i];
                    break;
                case "--script" when i + 1 < args.Length:
                    script = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--shots" when i + 1 < args.Length:
                    shots = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--every" when i + 1 < args.Length:
                    every = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--max-frames" when i + 1 < args.Length:
                    maxFrames = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--off":
                    off = true;         // the same scene with the K7 passes switched off (K2's plain emission), for comparison
                    break;
                case "--lang" when i + 1 < args.Length:
                    lang = args[++i].ToLowerInvariant() switch
                    {
                        "ja" => Language.Ja,
                        "en" => Language.En,
                        "zc" => Language.Zc,
                        "zt" => Language.Zt,
                        var other => throw new InvalidOperationException($"unknown language '{other}'"),
                    };
                    break;
                default:
                    if (args[i].StartsWith('-') || effect is not null)
                    {
                        error.WriteLine($"effect-shot: unexpected argument '{args[i]}'");
                        return ExitCode.Usage;
                    }

                    effect = args[i];
                    break;
            }
        }

        if (effect is null || !DefaultScripts.ContainsKey(effect))
        {
            error.WriteLine("effect-shot: name an effect: " + string.Join(", ", DefaultScripts.Keys));
            return ExitCode.Usage;
        }

        string? folder = GameFolderLocator.Resolve(game);
        if (folder is null || !Directory.Exists(folder))
        {
            error.WriteLine("effect-shot: no game folder; pass --game <dir> or set TSUKI_GAME_DIR to the CUSA47999 folder");
            return ExitCode.Usage;
        }

        outDir ??= Path.Combine(FindRoot(), "scratch", "K7");
        Directory.CreateDirectory(outDir);
        using GameFolderSource data = GameFolderSource.Open(folder);
        var grader = TileGrader.FromEboot(EbootReader.Parse(File.ReadAllBytes(Path.Combine(folder, "eboot.bin"))));
        using GameWindow window = GameWindow.Create(new WindowOptions("effect-shot", 320, 180, Hidden: true, Resizable: false, SwapInterval: 0));
        GlDevice device = GlDevice.Create(window.GetProcAddress);
        using var compositor = new GpuCompositor(device.Gl, 1920, 1080);
        using var cache = new TextureCache(device.Gl, 3L << 30) { Grader = grader };
        using var plainCache = new TextureCache(device.Gl, 3L << 30);
        var variants = new List<(string Suffix, DisplayListExecutor Executor, GameTextureProvider Provider)> { (string.Empty, new DisplayListExecutor(compositor, cache), new GameTextureProvider(data, cache)) };
        if (effect == "grade")
        {
            variants.Add(("_plain", new DisplayListExecutor(compositor, plainCache), new GameTextureProvider(data, plainCache)));       // the same frame without the grader
        }
        int[] candidates = script is { } s0 ? [s0] : DefaultScripts[effect];
        foreach (int k in candidates)
        {
            var sw = Stopwatch.StartNew();
            var scripts = new ScriptCache(data);
            var services = new GameServices(data, scripts)
            {
                SeLookup = (name, flags) => data.SoundEffectNames.Lookup(name, flags)?.Index,
                Layouts = new GamePictureLayouts(data),
            };
            var host = new GameHost(services, new GameOptions { Language = lang, Sound = false, VirtualMovies = true, RandomSeed = 7 });
            host.NewGame(data.Eboot.GameFlow.SceneId(k));
            var player = new AutoPlayer(new FirstPolicy(), PlayMode.Skip);
            Func<GameHost, bool> found = Condition(effect);
            long frames = 0;
            while (frames < maxFrames && host.Phase == GamePhase.Running && !found(host))
            {
                host.Step(player.Next(host));
                frames++;
            }

            if (!found(host))
            {
                output.WriteLine($"{effect,-7} script {k}: not reached in {frames} frames ({sw.ElapsedMilliseconds} ms), phase {host.Phase}");
                continue;
            }

            output.WriteLine($"{effect,-7} script {k}: reached at frame {frames} ({sw.ElapsedMilliseconds} ms)");
            for (int l = 0; l < host.Stage.Drawn.Length; l++)
            {
                LayerRecord r = host.Stage.Drawn[l];
                if (r.Used != 0 && r.Pic >= 0 && r.ZOrder > 0)
                {
                    output.WriteLine($"    layer {l,2} z {r.ZOrder,4} pic {data.Eboot.Pictures.Name(r.Pic),-22} alpha {r.Alpha:F2} mask {r.Mask,2} crop {string.Join(',', r.Crop)} blend {r.Blend} sttr {r.Sttr} stra {r.Stra} stns {r.Stns} grade {r.GradeKey}");
                }
            }

            RenderShots(effect, k, host, compositor, variants, outDir, shots, every, off, output);
            return ExitCode.Ok;
        }

        error.WriteLine($"effect-shot: {effect} was not reached in any of the scripts tried");
        return ExitCode.Failed;
    }

    private static Func<GameHost, bool> Condition(string effect) => effect switch
    {
        "noise" => h => AnyLayer(h, r => r.Stns is >= 1 and <= 12 && r.Alpha > 0.1f),
        "haze" => h => AnyLayer(h, r => r.Stra > 0 && r.Alpha > 0.5f),
        "rule" => h => AnyLayer(h, r => r.Sttr >= 0 && r.Alpha > 0.05f),
        "mask" => h => AnyLayer(h, r => r.Mask >= 0 && r.Alpha > 0.5f && h.Stage.Drawn[r.Mask].Used != 0 && h.Stage.Drawn[r.Mask].Alpha > 0.5f),
        "grade" => h => AnyLayer(h, r => r.GradeKey != 0 && r.Alpha > 0.5f && h.Stage.Drawn.Count(x => x.Used != 0 && x.Alpha > 0.5f) >= 2),
        "focus" => h => h.FocusState == FocusMode.On,
        "ghost" => h => h.GhostMode == 4 && h.GhostAlpha >= 0.9f,
        "choice" => h => h.ChoiceSelectable,
        _ => throw new ArgumentException(effect, nameof(effect)),
    };

    private static bool AnyLayer(GameHost host, Func<LayerRecord, bool> predicate)
    {
        foreach (LayerRecord r in host.Stage.Drawn)
        {
            if (r.Used != 0 && r.Pic >= 0 && r.ZOrder > 0 && predicate(r))
            {
                return true;
            }
        }

        return false;
    }

    private static void RenderShots(
        string effect, int script, GameHost host, GpuCompositor compositor, List<(string Suffix, DisplayListExecutor Executor, GameTextureProvider Provider)> variants,
        string outDir, int shots, int every, bool off, TextWriter output)
    {
        host.SetSkipLatch(false);
        if (off)
        {
            host.Ghost.Reset();
            host.Focus.Reset();
            host.Stage.EffectPasses = false;
        }

        host.SetAutoMode(false);
        long n = 0;
        int shot = 0;
        var list = new DisplayList(4096);
        while (shot < shots && n < 20_000)
        {
            InputFrame input = new() { Frame = ++n };
            if (effect == "choice")
            {
                // the options appear, then the cursor moves down, then the decision
                input = shot switch
                {
                    1 when n % every == 1 => input with { Pressed = PadButtons.Down, Repeated = PadButtons.Down, Held = PadButtons.Down },
                    2 when n % every == 1 => input with { Pressed = PadButtons.Decide, Repeated = PadButtons.Decide, Held = PadButtons.Decide },
                    _ => input,
                };
            }

            list.Reset(n);
            host.Step(input, list);
            list.Seal();
            bool take = n % every == 0;
            foreach ((string suffix, DisplayListExecutor executor, GameTextureProvider provider) in variants)
            {
                provider.Upload(list);
                if (n == 1)
                {
                    compositor.Clear(0, 0, 0, 0, 255);          // the frame is not cleared afterwards: a transition captures the previous one
                }

                ExecuteStats stats = executor.Execute(list);
                if (!take)
                {
                    break;      // only the first variant keeps the compositor state frame by frame
                }

                byte[] pixels = compositor.Read(compositor.Frame);
                for (int a = 3; a < pixels.Length; a += 4)
                {
                    pixels[a] = 255;
                }

                string file = Path.Combine(outDir, $"{effect}_{script:D3}_{shot}{suffix}{(off ? "_off" : string.Empty)}.png");
                PngWriter.Write(file, 1920, 1080, pixels);
                output.WriteLine($"  shot {shot}{suffix}: frame {host.Frame}, {list.Count} commands, {stats.Draws} quads, {stats.MissingTextures} missing textures, {provider.Missing.Count} unresolved -> {file}");
            }

            if (take)
            {
                shot++;
            }
        }
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Tsuki.slnx")))
            {
                return dir.FullName;
            }
        }

        return Directory.GetCurrentDirectory();
    }
}
