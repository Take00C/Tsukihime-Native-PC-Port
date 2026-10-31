using Tsuki.Engine.Contracts;
using Tsuki.Ui.Screens;

namespace Tsuki.Ui;

/// <summary>
/// Builds a menu screen in a known state for the screenshot tool and the tests: a <see cref="UiRoot"/> over a game data source and a
/// <see cref="PreviewServices"/>, with the named screen open and stepped until its fades have settled.
/// </summary>
public static class UiPreview
{
    private static readonly string[] Names =
    [
        "boot",
        "title",
        "title-nosave",
        "title-press",
        "title-cursor",
        "title-exit",
        "caution",
        "caution1",
        "caution2",
        "menu",
        "save",
        "load",
        "config-system",
        "config-sound",
        "config-controls",
        "config-controls-cursor",
        "config-controls-bottom",
        "config-display",
        "config-display-full",
        "config-port",
        "config-port-half",
        "config-sound-port",
        "toast",
        "confirm",
        "confirm-quickload",
        "popup",
        "backlog",
        "mode",
        "banner",
        "fills",
        "guides",
        "guide-cells",
        "gallery-cg",
        "gallery-cg-group",
        "gallery-cg-view",
        "gallery-cg-strip",
        "gallery-music",
        "gallery-music-play",
        "gallery-movies",
        "gallery-endings",
        "gallery-confirm",
        "gallery-locked",
        "flowchart-gallery",
        "flowchart-game",
        "config-system-drag",
        "save-drag",
        "gallery-music-drag",
        "flowchart-gallery-drag",
        "story-prompt-save",
        "story-prompt-cursor",
        "story-prompt-system",
        "story-prompt-fade",
    ];

    /// <summary>The names <see cref="Open"/> understands.</summary>
    public static IReadOnlyList<string> Screens => Names;

    /// <summary>Opens screen <paramref name="name"/> and steps it <paramref name="frames"/> frames (a default per screen when negative).</summary>
    /// <param name="name">The screen name (see <see cref="Screens"/>).</param>
    /// <param name="data">The game data.</param>
    /// <param name="services">The preview game; set its language and state before the call.</param>
    /// <param name="frames">Frames to step after opening, or -1 for the screen's default.</param>
    /// <param name="parts">The parts archive, when the caller has it (the boot notice text).</param>
    public static UiRoot Open(string name, IGameData data, PreviewServices services, int frames = -1, IUiPictureSource? parts = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(services);
        var root = new UiRoot(data, services, parts);
        int settle = 90;

        // package K12: "<screen>-drag" is the screen with the left button down on its scroll bar's thumb, which has been dragged a little way down
        bool drag = name.EndsWith("-drag", StringComparison.Ordinal);
        if (drag)
        {
            name = name[..^5];
        }

        switch (name)
        {
            case "boot":
                root.Push(new BootScreen());
                settle = 60;
                break;
            case "title":
                root.Context.TitleIntroShown = true;
                root.Push(new TitleScreen());
                settle = 200;
                break;
            case "title-nosave":
                // package K28: a story was started but no slot is written: the PS4's five-row menu (NEW GAME .. GALLERY) with the port's EXIT as its fifth row
                root.Context.TitleIntroShown = true;
                services.Title = services.Title with { ContinueSlot = -1 };
                root.Push(new TitleScreen());
                settle = 200;
                break;
            case "title-cursor":
                root.Context.TitleIntroShown = true;
                root.Push(new TitleScreen());
                Run(root, 200);
                Run(root, 1, down: true);
                settle = 30;
                break;
            case "title-exit":
                // package K33: the cursor on EXIT (Up from the first item wraps to the last), for the composed cell in its cursor look
                root.Context.TitleIntroShown = true;
                root.Push(new TitleScreen());
                Run(root, 200);
                root.Step(new InputFrame { Frame = root.Context.Frame + 1, Pressed = PadButtons.Up, Repeated = PadButtons.Up, Held = PadButtons.Up });
                settle = 30;
                break;
            case "title-press":
                services.Title = services.Title with { StoryStarted = false, ContinueSlot = -1 };
                root.Push(new TitleScreen());
                settle = 160;
                break;
            case "caution":
            case "caution1":
                services.Title = services.Title with { StoryStarted = false, ContinueSlot = -1 };
                root.Push(new TitleScreen());
                Run(root, 140);
                root.Step(new InputFrame { Frame = root.Context.Frame + 1, Pressed = PadButtons.Decide });
                settle = 200;
                if (name == "caution1")
                {
                    Run(root, 260);
                    root.Step(new InputFrame { Frame = root.Context.Frame + 1, Pressed = PadButtons.Decide });
                    settle = 200;
                }

                break;
            case "caution2":
                root.Push(new TitleScreen());
                Run(root, 66);
                services.IntroMoviePlaying = false;
                settle = 100;
                break;
            case "menu":
                root.Push(new SystemMenuScreen());
                break;
            case "save":
            case "load":
                root.Push(new SystemMenuScreen());
                Run(root, 40);
                root.Push(new SaveLoadScreen(name == "save"));
                settle = 80;
                break;
            case "config-system":
            case "config-sound":
            case "config-controls":
            case "config-controls-cursor":
            case "config-controls-bottom":
            case "config-display":
            case "config-display-full":
            case "config-port":
            case "config-port-half":
            case "config-sound-port":
                root.Push(new SystemMenuScreen());
                Run(root, 40);
                foreach (int word in data.Eboot.Ui.VoiceRowWords)
                {
                    services.SetGlobal(word, 7);
                }

                var config = root.Push(new ConfigScreen());
                if (name is "config-sound" or "config-sound-port" or "config-controls" or "config-controls-cursor" or "config-controls-bottom")
                {
                    config.Change(new Ctl(CtlKind.Tab, name is "config-sound" or "config-sound-port" ? 1 : 2), 0);
                }

                settle = 120;
                if (name == "config-controls-cursor")
                {
                    Run(root, 60);
                    Press(root, PadButtons.Down);
                    Press(root, PadButtons.Right);
                    settle = 30;
                }
                else if (name == "config-controls-bottom")
                {
                    Run(root, 60);
                    config.ScrollNow(config.ScrollMaximum);
                    settle = 20;
                }
                else if (name is "config-display" or "config-display-full")
                {
                    // package K19: the System tab's bottom, the port's Display Mode and Window Size rows; "-full" is the game in fullscreen with the cursor on that pill
                    Run(root, 60);
                    if (services.Display is { } display && name == "config-display-full")
                    {
                        display.Fullscreen = true;
                    }

                    config.ScrollNow(config.ScrollMaximum);
                    config.CursorTo(new Ctl(CtlKind.Display, name == "config-display-full" ? 1 : 0));
                    settle = 20;
                }
                else if (name is "config-port" or "config-port-half")
                {
                    // package K21: the System tab's bottom with the Copy Text to Clipboard row on and the Text Window Opacity slider under the cursor (at 100 %, or at 50 % for "-half");
                    // package K23: the Text Hooker Output (on by default) and Text WebSocket (switched on here) rows between them; package K24: the Screenshot Notice row (on by
                    // default) above them
                    Run(root, 60);
                    if (services.Port is { } port)
                    {
                        port.CopyTextToClipboard = true;
                        port.TextWebSocket = true;
                    }

                    if (name == "config-port-half")
                    {
                        config.WindowOpacityPercent = 50;
                    }

                    config.ScrollNow(config.ScrollMaximum);
                    config.CursorTo(new Ctl(CtlKind.Opacity));
                    settle = 20;
                }
                else if (name == "config-sound-port")
                {
                    // package K21: the Sound tab with the Mute in Background row (On) under the cursor
                    Run(root, 60);
                    if (services.Port is { } port)
                    {
                        port.MuteInBackground = true;
                    }

                    config.CursorTo(new Ctl(CtlKind.BackgroundMute, 1));
                    settle = 20;
                }

                break;
            case "toast":
                // package K21: the screenshot toast with the mode panel up, fully up (package K24: the short notice in the bottom left corner, no file name)
                root.Push(new ModePanelScreen());
                Run(root, 30);
                root.ShowToast(PortText.Get(PortString.ScreenshotSaved, services.Language));
                settle = UiToast.FadeInFrames + 4;
                break;
            case "mode":
                root.Push(new ModePanelScreen());
                settle = 30;
                break;
            case "banner":
                root.Push(new ModePanelScreen());
                Run(root, 30);
                root.ShowBanner(BannerKind.QuickSave);
                settle = 22;
                break;
            case "fills":
                root.Push(new FillTestScreen());
                break;
            case "guides":
                root.Push(new GuidesScreen());
                break;
            case "guide-cells":
                root.Push(new GuideCellsScreen());
                break;
            case var g when g.StartsWith("gallery-", StringComparison.Ordinal) || g.StartsWith("flowchart-", StringComparison.Ordinal):
                settle = OpenGallery(name, root, data, services);
                break;
            case "backlog":
                services.SetBacklog(SamplePages(data, services.Language));
                root.Push(new BacklogScreen());
                settle = 40;
                break;
            case "confirm":
                root.Push(new SystemMenuScreen());
                Run(root, 40);
                root.Push(new SaveLoadScreen(true));
                Run(root, 40);
                root.Push(new ConfirmDialog(ConfirmKind.Overwrite, _ => { }));
                settle = 40;
                break;
            case "confirm-quickload":
                // package R1: the mode panel's Q.LOAD dialog with the quick save's thumbnail, whose frame hangs lower under the two-line English message
                root.Push(new ModePanelScreen());
                Run(root, 30);
                root.Push(new ConfirmDialog(ConfirmKind.QuickLoad, _ => { }, thumbnailSlot: 139));
                settle = 40;
                break;
            case "story-prompt-save":
            case "story-prompt-cursor":
            case "story-prompt-system":
            case "story-prompt-fade":
                // package K13: the story's two-button prompt over nothing (the shot tool's black frame): _SELA's message 216, nothing selected / Yes selected;
                // _SELS's message 1446 + F27 (here F27 = 3) with No selected; and a frame in the middle of the fade-in (alpha 0.5, the window popped to 1.025)
                services.StoryPrompt = name switch
                {
                    "story-prompt-cursor" => new UiStoryPrompt(216, 1, 1f, 1f),
                    "story-prompt-system" => new UiStoryPrompt(1449, 0, 1f, 1f),
                    "story-prompt-fade" => new UiStoryPrompt(216, -1, 0.5f, 1.025f),
                    _ => new UiStoryPrompt(216, -1, 1f, 1f),
                };
                settle = 2;
                break;
            case "popup":
                root.Push(new SystemMenuScreen());
                Run(root, 40);
                root.Push(new MessagePopup(root.Context.Sysmes(193), 600f));
                settle = 40;
                break;
            default:
                throw new ArgumentException($"unknown screen '{name}'; the screens are {string.Join(", ", Names)}", nameof(name));
        }

        Run(root, frames < 0 ? settle : frames);
        if (drag)
        {
            float y = UiScrollBar.ThumbTop(0f, 608f) + 140f;
            root.Step(UiInput.PointerFrame(1855f, y, PointerButtons.Left, PointerButtons.Left, 0, root.Context.Frame + 1));
            for (int i = 1; i <= 12; i++)
            {
                root.Step(UiInput.PointerFrame(1855f, y + (i * 12f), PointerButtons.Left, PointerButtons.None, 0, root.Context.Frame + 1));
            }
        }

        return root;
    }

    /// <summary>Backlog pages made of real script lines (message text of the user's data), 2 to 4 messages each, some with a voice.</summary>
    public static IEnumerable<BacklogPage> SamplePages(IGameData data, Language language)
    {
        ArgumentNullException.ThrowIfNull(data);
        int id = 3;
        var pages = new List<BacklogPage>();
        for (int p = 0; p < 8; p++)
        {
            var msgs = new List<BacklogMessage>();
            int count = 2 + (p % 3);
            while (msgs.Count < count && id < data.TextLineCount)
            {
                ReadOnlyMemory<byte> line = data.TextLine(language, id++);
                if (line.Length > 4 && line.Span[0] != (byte)'$')
                {
                    msgs.Add(new BacklogMessage(line, 0, msgs.Count == 0 ? 100 + p : -1));
                }
            }

            pages.Add(new BacklogPage(msgs, Jumpable: p != 5));
        }

        return pages;
    }

    // package K5b: the gallery and flowchart scenes, each with the progress of a player a quarter through the story (a full one for the endings)
    private static int OpenGallery(string name, UiRoot root, IGameData data, PreviewServices services)
    {
        services.Believe(data, name switch { "gallery-endings" or "gallery-cg-strip" => "full", "gallery-locked" => "none", _ => "mid" });
        if (name == "flowchart-game")
        {
            root.Push(new SystemMenuScreen());
            Run(root, 40);
            root.Push(new FlowchartScreen(gallery: false));
            return 60;
        }

        root.Push(new GalleryScreen());
        Run(root, 40);
        var gallery = (GalleryScreen)root.Top!;
        switch (name)
        {
            case "gallery-cg":
                Press(root, PadButtons.Right);
                return 80;
            case "gallery-cg-group":
            case "gallery-cg-view":
            {
                Press(root, PadButtons.Right);
                var cg = (GalleryCgPanel)gallery.Panel;
                int pick = Enumerable.Range(0, cg.Count).First(k => cg.Unlocked[GroupAt(cg, k)].Length > (name == "gallery-cg-group" ? 1 : 0));
                MoveTo(root, pick);
                Press(root, PadButtons.Decide);
                Run(root, 40);
                if (name == "gallery-cg-view")
                {
                    Press(root, PadButtons.Decide);
                    Run(root, 40);
                    return 50;
                }

                return 80;
            }

            case "gallery-cg-strip":
            {
                // a picture that stacks one strip per language, opened in the viewer (the language picks the strip)
                Press(root, PadButtons.Right);
                var cg = (GalleryCgPanel)gallery.Panel;
                (int g, int k) = Enumerable.Range(0, cg.Unlocked.Count).SelectMany(g => Enumerable.Range(0, cg.Unlocked[g].Length).Select(k => (g, k)))
                    .First(t => cg.Entry(t.g, t.k).LanguageStrip);
                int[] main = [.. Enumerable.Range(0, cg.Unlocked.Count).Where(i => cg.Unlocked[i].Length > 0)];
                MoveTo(root, Array.IndexOf(main, g));
                Press(root, PadButtons.Decide);
                if (cg.Sub is not null)
                {
                    Run(root, 40);
                    MoveTo(root, k);
                    Press(root, PadButtons.Decide);
                }

                Run(root, 60);
                return 50;
            }

            case "gallery-music":
            case "gallery-music-play":
                Press(root, PadButtons.Down);
                Press(root, PadButtons.Decide);
                Run(root, 100);
                if (name == "gallery-music-play")
                {
                    var music = (GalleryMusicPanel)gallery.Panel;
                    int row = Enumerable.Range(0, music.Unlocked.Count).First(i => music.Unlocked[i] && i > 0);
                    for (int i = 0; i < row; i++)
                    {
                        Press(root, PadButtons.Down);
                    }

                    Press(root, PadButtons.Decide);
                    return 270;
                }

                return 80;
            case "gallery-movies":
                Press(root, PadButtons.Down);
                Press(root, PadButtons.Down);
                Press(root, PadButtons.Down);
                Press(root, PadButtons.Decide);
                return 100;
            case "gallery-endings":
                for (int i = 0; i < 4; i++)
                {
                    Press(root, PadButtons.Down);
                }

                Press(root, PadButtons.Decide);
                Run(root, 100);
                Press(root, PadButtons.Down);
                Press(root, PadButtons.Down);
                return 80;
            case "gallery-confirm":
                for (int i = 0; i < 4; i++)
                {
                    Press(root, PadButtons.Down);
                }

                Press(root, PadButtons.Decide);
                Run(root, 100);
                Press(root, PadButtons.Decide);
                return 40;
            case "gallery-locked":
                Press(root, PadButtons.Down);
                Press(root, PadButtons.Down);
                Press(root, PadButtons.Decide);
                return 40;
            default:
                // the chart opens from the gallery on its Flowchart button
                Press(root, PadButtons.Down);
                Press(root, PadButtons.Down);
                Press(root, PadButtons.Decide);
                return 60;
        }
    }

    private static int GroupAt(GalleryCgPanel cg, int k)
    {
        int seen = -1;
        for (int g = 0; g < cg.Unlocked.Count; g++)
        {
            if (cg.Unlocked[g].Length > 0 && ++seen == k)
            {
                return g;
            }
        }

        return 0;
    }

    private static void MoveTo(UiRoot root, int position)
    {
        for (int i = 0; i < position / GalleryCgPanel.Cols; i++)
        {
            Press(root, PadButtons.Down);
        }

        for (int i = 0; i < position % GalleryCgPanel.Cols; i++)
        {
            Press(root, PadButtons.Right);
        }
    }

    /// <summary>One frame with <paramref name="buttons"/> newly pressed, then a quiet frame.</summary>
    public static void Press(UiRoot root, PadButtons buttons)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.Step(new InputFrame { Frame = root.Context.Frame + 1, Pressed = buttons, Repeated = buttons, Held = buttons });
        root.Step(new InputFrame { Frame = root.Context.Frame + 1 });
    }

    /// <summary>Steps the root <paramref name="frames"/> frames with no input.</summary>
    public static void Run(UiRoot root, int frames, bool down = false)
    {
        ArgumentNullException.ThrowIfNull(root);
        for (int i = 0; i < frames; i++)
        {
            PadButtons b = down && i == 0 ? PadButtons.Down : PadButtons.None;
            root.Step(new InputFrame { Frame = root.Context.Frame + 1, Pressed = b, Repeated = b, Held = b });
        }
    }
}

/// <summary>A test screen with flat fills of known colour and alpha (checks the compositor's fill path).</summary>
internal sealed class FillTestScreen : UiScreen
{
    public override void Step(in UiInput input)
    {
    }

    public override void Draw(UiPainter painter)
    {
        float[] alphas = [1f, 0.75f, 0.5f, 0.25f];
        float[][] colours = [[0f, 0f, 0f], [1f, 1f, 1f], [1f, 0f, 0f], [0f, 0f, 1f]];
        for (int r = 0; r < colours.Length; r++)
        {
            for (int c = 0; c < alphas.Length; c++)
            {
                painter.Fill(new RectF(100 + (c * 200), 100 + (r * 200), 150, 150), colours[r][0], colours[r][1], colours[r][2], alphas[c]);
            }
        }
    }
}


/// <summary>Draws every cell of the guide icon sheet and the guide word sheet with its index (a reading aid for the guide tables).</summary>
internal sealed class GuideCellsScreen : UiScreen
{
    public override void Step(in UiInput input)
    {
    }

    public override void Draw(UiPainter painter)
    {
        painter.Fill(UiMetrics.Screen, 0.1f, 0.2f, 0.35f, 1f);
        UiAssets assets = painter.Assets;
        TextStyle style = TextStyle.Menu(0.4);
        IReadOnlyList<SheetCell> icons = assets.Tables.GuideIconCells;
        for (int i = 0; i < icons.Count; i++)
        {
            SheetCell c = icons[i];
            float x = 60 + ((i % 8) * 220);
            float y = 40 + ((i / 8) * 130);
            painter.Sprite(UiSheets.GuideIcons, RectF.FromCorners(c.X0, c.Y0, c.X1, c.Y1), new RectF(x, y, c.Width * 2, c.Height * 2));
            Ctx.Text.Draw(painter, i.ToString(System.Globalization.CultureInfo.InvariantCulture), style, x / UiMetrics.UnitX, (y + 80) / UiMetrics.UnitY);
        }

        IReadOnlyList<SheetCell> words = assets.Tables.GuideWordCells(assets.Language);
        for (int i = 0; i < words.Count; i++)
        {
            SheetCell c = words[i];
            float x = 60 + ((i % 3) * 600);
            float y = 440 + ((i / 3) * 70);
            painter.Sprite(UiSheets.GuideWords, RectF.FromCorners(c.X0, c.Y0, c.X1, c.Y1), new RectF(x + 60, y, c.Width * 1.5f, c.Height * 1.5f));
            Ctx.Text.Draw(painter, i.ToString(System.Globalization.CultureInfo.InvariantCulture), style, x / UiMetrics.UnitX, y / UiMetrics.UnitY);
        }
    }
}


/// <summary>Draws the twelve button guides one above the other, each with its index on the left (checks the prompts of every guide in the current prompt style).</summary>
internal sealed class GuidesScreen : UiScreen
{
    public override void Step(in UiInput input)
    {
    }

    public override void Draw(UiPainter painter)
    {
        painter.Fill(UiMetrics.Screen, 0.08f, 0.14f, 0.26f, 1f);
        TextStyle style = TextStyle.Menu(0.4);
        for (int g = 0; g < 12; g++)
        {
            float dy = -(11 - g) * 27f;
            UiWidgets.Guide(painter, g, dy, 1f, Ctx.Services.DecideIsCross);
            Ctx.Text.Draw(painter, g.ToString(System.Globalization.CultureInfo.InvariantCulture), style, 4f, 424.36f + dy + 2f);
        }
    }
}
