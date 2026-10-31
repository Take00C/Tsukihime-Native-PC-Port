using System.Diagnostics;
using Tsuki.Data;
using Tsuki.Data.Containers;
using Tsuki.Data.Images;
using Tsuki.Engine.Contracts;
using Tsuki.Platform;
using Tsuki.Render.Compositor;
using Tsuki.Render.Gl;
using Tsuki.Render.Textures;
using Tsuki.Ui;

namespace Tsuki.Cli.Commands;

/// <summary>
/// <c>Tsuki.Cli ui-shot &lt;screen&gt; [--lang ja|en|zc|zt] [--prompts keyboard|playstation|xbox|auto] [--decide-cross] [--game DIR] [--out DIR] [--frames N] [--all]</c>: builds a menu screen from the user's UI sheets
/// (<see cref="UiPreview"/>), renders its display list through the GPU compositor in a hidden window, and writes a 1920 x 1080 PNG to <c>scratch/K5a</c>
/// (package K5a). The window is closed before the command returns.
/// </summary>
public sealed class UiShotCommand : ICliCommand
{
    /// <inheritdoc/>
    public string Name => "ui-shot";

    /// <inheritdoc/>
    public string Summary => "render a menu screen to a PNG through the GPU compositor; ui-shot <screen|--all> [--lang ja|en|zc|zt] [--prompts keyboard|playstation|xbox|auto] [--decide-cross] [--game DIR] [--out DIR] [--frames N]";

    /// <inheritdoc/>
    public int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? game = null;
        string? screen = null;
        string? outDir = null;
        Language lang = Language.En;
        int frames = -1;
        bool all = false;
        PromptSetting? prompts = null;
        bool decideCross = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--prompts" when i + 1 < args.Length:
                    if (!PromptStyles.TryParse(args[++i], out PromptSetting parsed))
                    {
                        error.WriteLine("ui-shot: --prompts takes keyboard, playstation, xbox or auto");
                        return ExitCode.Usage;
                    }

                    prompts = parsed;
                    break;
                case "--game" when i + 1 < args.Length:
                    game = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    outDir = args[++i];
                    break;
                case "--frames" when i + 1 < args.Length:
                    frames = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
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
                case "--all":
                    all = true;
                    break;
                case "--decide-cross":
                    decideCross = true;
                    break;
                default:
                    if (args[i].StartsWith('-') || screen is not null)
                    {
                        error.WriteLine($"ui-shot: unexpected argument '{args[i]}'");
                        return ExitCode.Usage;
                    }

                    screen = args[i];
                    break;
            }
        }

        if (screen is null && !all)
        {
            error.WriteLine("ui-shot: name a screen: " + string.Join(", ", UiPreview.Screens));
            return ExitCode.Usage;
        }

        string? folder = GameFolderLocator.Resolve(game);
        if (folder is null || !Directory.Exists(folder))
        {
            error.WriteLine("ui-shot: no game folder; pass --game <dir> or set TSUKI_GAME_DIR to the CUSA47999 folder");
            return ExitCode.Usage;
        }

        outDir ??= Path.Combine(FindRoot(), "scratch", "K5a");
        Directory.CreateDirectory(outDir);
        using GameFolderSource data = GameFolderSource.Open(folder);
        IEnumerable<string> names = all ? UiPreview.Screens : [screen!];
        using GameWindow window = GameWindow.Create(new WindowOptions("ui-shot", 320, 180, Hidden: true, Resizable: false, SwapInterval: 0));
        GlDevice device = GlDevice.Create(window.GetProcAddress);
        using var compositor = new GpuCompositor(device.Gl, 1920, 1080);
        using var cache = new TextureCache(device.Gl, 2L << 30);
        var executor = new DisplayListExecutor(compositor, cache);
        foreach (string name in names)
        {
            var sw = Stopwatch.StartNew();
            var services = new PreviewServices(lang);
            if (prompts is { } setting)
            {
                // "auto" plays a player whose last input was the keyboard
                services.PromptSetting = setting;
            }

            services.DecideIsCross = decideCross;
            var parts = new PartsPictureSource(data);
            UiRoot root = UiPreview.Open(name, data, services, frames, parts);
            var list = new DisplayList(2048);
            list.Reset(0);
            root.Draw(list);
            list.Seal();
            var uploader = new UiTextureUploader(data, services, cache, parts, root.Context.Assets);
            int uploaded = uploader.Upload(list);
            compositor.Clear(0, 0, 0, 0, 255);
            ExecuteStats stats = executor.Execute(list);
            byte[] pixels = compositor.Read(compositor.Frame);
            for (int a = 3; a < pixels.Length; a += 4)
            {
                pixels[a] = 255;            // the frame is shown over black: its alpha channel is not part of the picture
            }

            string file = Path.Combine(outDir, $"ui_{name}_{lang.ToString().ToLowerInvariant()}{(prompts is { } p ? "_" + PromptStyles.Name(p) : string.Empty)}.png");
            PngWriter.Write(file, 1920, 1080, pixels);
            output.WriteLine($"{name,-18} {lang}: {list.Count,5} commands, {stats.Draws,5} quads, {stats.MissingTextures} missing textures, {uploaded} textures, {root.Context.Assets.Missing.Count} missing sheets, {sw.ElapsedMilliseconds} ms -> {file}");
        }

        return ExitCode.Ok;
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

/// <summary>Uploads every texture a display list names (UI tiles, glyph pages, slot thumbnails, the menus' composed pictures) into a texture cache, decoding each sheet once.</summary>
internal sealed class UiTextureUploader(GameFolderSource data, PreviewServices services, TextureCache cache, PartsPictureSource parts, UiAssets assets)
{
    private readonly Dictionary<(UiSet, Language, int), Picture> _sheets = [];
    private readonly Dictionary<(Language, int), FontPage> _fonts = [];
    private readonly Dictionary<(int, int), Picture?> _pictures = [];

    public int Upload(DisplayList list)
    {
        int n = 0;
        foreach (ref readonly DrawCmd c in list.Commands)
        {
            if (c.Kind != DrawKind.TexturedQuad || cache.Contains(c.Texture))
            {
                continue;
            }

            TextureRef t = c.Texture;
            switch (t.Kind)
            {
                case TextureKind.UiTile:
                {
                    var key = ((UiSet)t.A, (Language)t.B, t.C);
                    if (!_sheets.TryGetValue(key, out Picture? pic))
                    {
                        pic = data.UiSheet(key.Item1, key.Item2, key.C);
                        _sheets[key] = pic;
                    }

                    PictureTile tile = pic.Tiles.First(x => x.Index == t.D);
                    cache.Upload(t, tile.StoredWidth, tile.StoredHeight, tile.Rgba.Span);
                    n++;
                    break;
                }

                case TextureKind.GlyphPage:
                {
                    var key = ((Language)t.A, t.B);
                    if (!_fonts.TryGetValue(key, out FontPage? page))
                    {
                        page = data.FontPage(key.Item1, key.B);
                        _fonts[key] = page;
                    }

                    cache.Upload(t, page.Width, page.Height, page.Rgba.Span);
                    n++;
                    break;
                }

                case TextureKind.PictureTile:
                {
                    var key = (t.A, t.B);
                    if (!_pictures.TryGetValue(key, out Picture? pic))
                    {
                        pic = t.A == UiPictureArchive.Parts ? parts.Parts(t.B) : data.Picture(t.A, t.B);
                        _pictures[key] = pic;
                    }

                    PictureTile? tile = pic?.Tiles.FirstOrDefault(x => x.Index == t.C);
                    if (tile is not null)
                    {
                        cache.Upload(t, tile.StoredWidth, tile.StoredHeight, tile.Rgba.Span);
                        n++;
                    }

                    break;
                }

                case TextureKind.Thumbnail:
                {
                    UiThumbnail? thumb = services.Thumbnail(t.A);
                    if (thumb is not null)
                    {
                        cache.Upload(t, thumb.Width, thumb.Height, thumb.Rgba.Span);
                        n++;
                    }

                    break;
                }

                case TextureKind.UiComposed:
                {
                    // the title's EXIT cell, composed from the player's own sheet (package K33)
                    UiComposedPicture? pic = assets.Composed(t.A, (Language)t.B);
                    if (pic is not null)
                    {
                        cache.Upload(t, pic.Width, pic.Height, pic.Rgba.Span);
                        n++;
                    }

                    break;
                }

                default:
                    break;
            }
        }

        return n;
    }
}

/// <summary>The pictures of the <c>parts</c> archive for the boot notice, read from the game folder (the data contract serves only the six image archives).</summary>
internal sealed class PartsPictureSource(GameFolderSource data) : IUiPictureSource
{
    /// <inheritdoc/>
    public Picture? Parts(int index)
    {
        MrgArchive archive = data.Archive("parts");
        return (uint)index < (uint)archive.Count ? ImageMzp.Open(archive.Memory(index)).ToPicture(1) : null;
    }
}
