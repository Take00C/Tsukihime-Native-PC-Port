using System.Globalization;
using Tsuki.Data;
using Tsuki.Engine.Contracts;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Cli.Commands;

/// <summary>
/// <c>Tsuki.Cli exit-art [--lang ja|en|zc|zt] [--game DIR] [--out DIR]</c>: composes the title's EXIT cell from the user's TITLE_PARTS sheet the way the game does
/// (<see cref="TitleExitArt"/>, package K33) and writes it for inspection: the two 296 x 72 cells as they are (straight alpha), and one strip per look with the
/// five items of the sheet and EXIT under them over a dark fill, at 1:1 and at 3x. It prints what the composer measured. The pictures are the user's art: they
/// go to <c>scratch/</c> (git-ignored) by default and belong nowhere else.
/// </summary>
public sealed class UiExitArtCommand : ICliCommand
{
    /// <inheritdoc/>
    public string Name => "exit-art";

    /// <inheritdoc/>
    public string Summary => "compose the title's EXIT cell from the UI sheet and write it as PNG for inspection; exit-art [--lang ja|en|zc|zt] [--game DIR] [--out DIR]";

    /// <inheritdoc/>
    public int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? game = null;
        string? outDir = null;
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
                    error.WriteLine($"exit-art: unexpected argument '{args[i]}'");
                    return ExitCode.Usage;
            }
        }

        string? folder = GameFolderLocator.Resolve(game);
        if (folder is null || !Directory.Exists(folder))
        {
            error.WriteLine("exit-art: no game folder; pass --game <dir> or set TSUKI_GAME_DIR to the CUSA47999 folder");
            return ExitCode.Usage;
        }

        outDir ??= Path.Combine(FindRoot(), "scratch", "exit-art");
        Directory.CreateDirectory(outDir);
        using GameFolderSource data = GameFolderSource.Open(folder);
        Picture sheet = data.UiSheet(UiSheets.TitleMenu.Set, lang, UiSheets.TitleMenu.Child);
        byte[] rgba = UiComposed.Assemble(sheet);
        string suffix = lang.ToString().ToLowerInvariant();
        bool ok = true;
        for (int column = 0; column < 2; column++)
        {
            string look = column == 0 ? "normal" : "cursor";
            byte[]? cell = TitleExitArt.ComposeState(rgba, column, out ExitLayout layout);
            if (cell is null)
            {
                output.WriteLine($"{look}: the sheet is not the art the composer expects; the title falls back to the glyph font");
                ok = false;
                continue;
            }

            string cellFile = Path.Combine(outDir, $"exit_{suffix}_{look}.png");
            PngWriter.Write(cellFile, TitleExitArt.CellWidth, TitleExitArt.CellHeight, cell);
            output.WriteLine(
                $"{look}: gap {layout.Gap}, centre {layout.Centre.ToString("0.0", CultureInfo.InvariantCulture)}, rows {layout.RowTop}..{layout.RowBottom}, " +
                $"letters {string.Join(" ", layout.Letters.Select(l => $"{l.Left}..{l.Right}"))}, E from {layout.ESource}, I from {layout.ISource}, glow sum {layout.GlowSum.ToString("0.00", CultureInfo.InvariantCulture)} -> {cellFile}");

            // the strip: the five cells of this column over the title's dark blue, EXIT last, at 1:1 and at 3x
            int x0 = 24 + (312 * column);
            int[] tops = [376, 24, 112, 200, 288];                 // CONTINUE, NEW GAME, LOAD GAME, CONFIG, GALLERY (the order of the menu)
            byte[] strip = new byte[TitleExitArt.CellWidth * TitleExitArt.CellHeight * 6 * 4];
            for (int k = 0; k < 6; k++)
            {
                for (int y = 0; y < TitleExitArt.CellHeight; y++)
                {
                    for (int x = 0; x < TitleExitArt.CellWidth; x++)
                    {
                        int src = k < 5 ? ((((tops[k] + y) * TitleExitArt.SheetWidth) + x0 + x) * 4) : (((y * TitleExitArt.CellWidth) + x) * 4);
                        ReadOnlySpan<byte> p = k < 5 ? rgba.AsSpan(src, 4) : cell.AsSpan(src, 4);
                        int o = ((((k * TitleExitArt.CellHeight) + y) * TitleExitArt.CellWidth) + x) * 4;
                        float a = p[3] / 255f;
                        strip[o] = (byte)MathF.Round((p[0] * a) + (24 * (1 - a)));
                        strip[o + 1] = (byte)MathF.Round((p[1] * a) + (30 * (1 - a)));
                        strip[o + 2] = (byte)MathF.Round((p[2] * a) + (60 * (1 - a)));
                        strip[o + 3] = 255;
                    }
                }
            }

            // the five source cells of this column as they are (straight alpha), stacked, for the measurements that compare EXIT with them
            byte[] cells = new byte[TitleExitArt.CellWidth * TitleExitArt.CellHeight * 5 * 4];
            for (int k = 0; k < 5; k++)
            {
                for (int y = 0; y < TitleExitArt.CellHeight; y++)
                {
                    rgba.AsSpan((((tops[k] + y) * TitleExitArt.SheetWidth) + x0) * 4, TitleExitArt.CellWidth * 4).CopyTo(cells.AsSpan((((k * TitleExitArt.CellHeight) + y) * TitleExitArt.CellWidth) * 4));
                }
            }

            PngWriter.Write(Path.Combine(outDir, $"cells_{suffix}_{look}.png"), TitleExitArt.CellWidth, TitleExitArt.CellHeight * 5, cells);

            // the check of the glow model: each word's own glow repainted from its letter boxes against the sheet's cell, outside the boxes
            string[] names = ["NEW GAME", "LOAD GAME", "CONFIG", "GALLERY", "CONTINUE"];
            int[] wordTops = [24, 112, 200, 288, 376];
            for (int word = 0; word < 5; word++)
            {
                byte[]? painted = TitleExitArt.RepaintGlow(rgba, column, word);
                if (painted is null)
                {
                    continue;
                }

                byte[] source = new byte[TitleExitArt.CellWidth * TitleExitArt.CellHeight * 4];
                for (int y = 0; y < TitleExitArt.CellHeight; y++)
                {
                    rgba.AsSpan((((wordTops[word] + y) * TitleExitArt.SheetWidth) + x0) * 4, TitleExitArt.CellWidth * 4).CopyTo(source.AsSpan(y * TitleExitArt.CellWidth * 4));
                }

                LetterSpan[] letters = TitleExitArt.Segment(source, out int top, out int bottom);
                long sum = 0;
                int n = 0, max = 0, over8 = 0;
                for (int y = 0; y < TitleExitArt.CellHeight; y++)
                {
                    for (int x = 0; x < TitleExitArt.CellWidth; x++)
                    {
                        bool inBox = y >= top - 1 && y <= bottom + 1 && letters.Any(l => x >= l.Left - 1 && x <= l.Right + 1);
                        if (inBox)
                        {
                            continue;
                        }

                        int o = (((y * TitleExitArt.CellWidth) + x) * 4) + 3;
                        int diff = Math.Abs(painted[o] - source[o]);
                        sum += diff;
                        n++;
                        max = Math.Max(max, diff);
                        over8 += diff > 8 ? 1 : 0;
                    }
                }

                output.WriteLine($"{look}: glow model on {names[word],-9}: mean alpha error {(sum / (double)n).ToString("0.00", CultureInfo.InvariantCulture)}, max {max}, {over8} of {n} pixels off by more than 8");
            }

            string stripFile = Path.Combine(outDir, $"exit_strip_{suffix}_{look}.png");
            PngWriter.Write(stripFile, TitleExitArt.CellWidth, TitleExitArt.CellHeight * 6, strip);
            PngWriter.Write(Path.Combine(outDir, $"exit_strip_{suffix}_{look}_3x.png"), TitleExitArt.CellWidth * 3, TitleExitArt.CellHeight * 6 * 3, Scale(strip, TitleExitArt.CellWidth, TitleExitArt.CellHeight * 6, 3));
            output.WriteLine($"{look}: strip -> {stripFile} (and _3x)");
        }

        return ok ? ExitCode.Ok : ExitCode.Failed;
    }

    private static byte[] Scale(byte[] rgba, int width, int height, int k)
    {
        byte[] big = new byte[width * k * height * k * 4];
        for (int y = 0; y < height * k; y++)
        {
            for (int x = 0; x < width * k; x++)
            {
                rgba.AsSpan(((((y / k) * width) + (x / k)) * 4), 4).CopyTo(big.AsSpan((((y * width * k) + x) * 4), 4));
            }
        }

        return big;
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
