// The scripted input of the verification mode (package K6): a list of "frame:action" entries that press keys, click, scroll, take screenshots and print the
// state, so an agent can boot, go through the title, play, save, load and rewind with no one at the keyboard. Frames are logic steps (60 per second).
//
//   60:Enter            press Enter (held for 2 steps)
//   200:Backspace*30    hold Backspace for 30 steps
//   300:Esc+Down        two keys at once
//   400:click=960,540   left click at canvas pixel (960, 540); rclick=x,y for the right button, mclick=x,y for the wheel button; move=x,y only moves the pointer; wheel=1 / wheel=-1
//   450:drag=1850,200,1850,700,30   press the left button at (1850, 200), run the pointer to (1850, 700) over 30 steps, release (a scroll bar drag, a pan)
//   500:shot=menu       write <shot-dir>/menu.png after step 500 has been drawn
//   600:log             print the state of the game (script, offset, scene, line)
//   650:lang=ja         switch the language as the config screen does (ja, en, zc, zt)
//   680:plate=Ciel      fill the name plate (retail never does; verifies its draw path); plate= empties it
//   690:scene=4000      start a story scene by its scene id, as the title's New Game does (package K15: the staff roll is 4000)
//   695:jumpnode=300    the flowchart's jump to a node (a story's scene starts as the chart's Jump does it); 696:replay=1000 a gallery replay of a scene (package K15)
//   650:holdchoice      the autoplay leaves an open choice list unanswered until releasechoice (package K16), so a script can pick the line itself (keys, pointer, pad)
//   660:pad=South*2     a gamepad button for 2 steps (South, East, West, North, L1, R1, L2, R2, Start, Back, Up, Down, Left, Right), through the mapper like a real pad (package K16)
//   700:quit            end the run
//   800:autopause       stop the autoplay's clicks (autoresume starts them again)
//   900:holdprompt      the autoplay leaves the story's yes / no prompts (_SELA, _SELS) unanswered, so a script can answer them with keys, the pointer or a screenshot (releaseprompt
//                       lets it answer them again: No for a _SELA, Yes for a _SELS)
//   950:screenshot      the screenshot key (F12): the next presented frame goes to --screenshot-dir as a PNG at the window's size, then the toast (package K21)
//   960:close           the window's close button / Alt+F4, through the event pump: the quit question opens, a second close while it is up quits (package K21)
//   970:blur / focus    the window loses / regains the keyboard focus (what Mute in Background listens to; a hidden window never has the focus) (package K21)
using System.Globalization;
using Tsuki.Engine.Contracts;
using Tsuki.Platform.Input;

namespace Tsuki.App.Play;

/// <summary>One entry of a script.</summary>
internal sealed record ScriptEntry(long Frame, PcKey? Key, int Hold, string? Verb, string? Argument);

/// <summary>A parsed script.</summary>
internal sealed class InputScript
{
    private static readonly char[] ScriptSeparators = [';', ' ', (char)10, (char)13, (char)9];
    private readonly List<ScriptEntry> _entries = [];

    public IReadOnlyList<ScriptEntry> Entries => _entries;

    /// <summary>Parses the text, or the contents of the file when it starts with <c>@</c>.</summary>
    public static InputScript Parse(string text)
    {
        if (text.StartsWith('@'))
        {
            text = File.ReadAllText(text[1..]);
        }

        var script = new InputScript();
        foreach (string raw in text.Split(ScriptSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0 || !long.TryParse(raw[..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out long frame))
            {
                // "click=960" and "shot=x" never contain a colon before their frame: a bare token without a frame is a mistake
                throw new FormatException($"script entry '{raw}' is not frame:action");
            }

            foreach (string action in raw[(colon + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                script._entries.Add(ParseAction(frame, action));
            }
        }

        return script;
    }

    private static ScriptEntry ParseAction(long frame, string action)
    {
        int eq = action.IndexOf('=');
        if (eq > 0)
        {
            return new ScriptEntry(frame, null, 2, action[..eq].ToLowerInvariant(), action[(eq + 1)..]);
        }

        if (action.Equals("log", StringComparison.OrdinalIgnoreCase) || action.Equals("quit", StringComparison.OrdinalIgnoreCase)
            || action.Equals("autopause", StringComparison.OrdinalIgnoreCase) || action.Equals("autoresume", StringComparison.OrdinalIgnoreCase)
            || action.Equals("holdprompt", StringComparison.OrdinalIgnoreCase) || action.Equals("releaseprompt", StringComparison.OrdinalIgnoreCase)
            || action.Equals("holdchoice", StringComparison.OrdinalIgnoreCase) || action.Equals("releasechoice", StringComparison.OrdinalIgnoreCase)
            || action.Equals("screenshot", StringComparison.OrdinalIgnoreCase) || action.Equals("close", StringComparison.OrdinalIgnoreCase)
            || action.Equals("blur", StringComparison.OrdinalIgnoreCase) || action.Equals("focus", StringComparison.OrdinalIgnoreCase))
        {
            return new ScriptEntry(frame, null, 0, action.ToLowerInvariant(), null);
        }

        int hold = 2;
        string name = action;
        int star = action.IndexOf('*');
        if (star > 0)
        {
            name = action[..star];
            hold = int.Parse(action[(star + 1)..], CultureInfo.InvariantCulture);
        }

        return new ScriptEntry(frame, ParseKey(name), hold, null, null);
    }

    private static PcKey ParseKey(string name)
    {
        string n = name.ToLowerInvariant() switch
        {
            "esc" => "escape",
            "return" => "enter",
            "ctrl" => "leftctrl",
            "pgup" => "pageup",
            "pgdn" => "pagedown",
            "del" => "delete",
            "bs" => "backspace",
            "decide" => "enter",
            var other => other,
        };
        foreach (PcKey k in Enum.GetValues<PcKey>())
        {
            if (k.ToString().Equals(n, StringComparison.OrdinalIgnoreCase))
            {
                return k;
            }
        }

        throw new FormatException($"unknown key '{name}' in the script");
    }

    /// <summary>The keys held at <paramref name="frame"/>.</summary>
    public KeySet KeysAt(long frame)
    {
        KeySet set = KeySet.None;
        foreach (ScriptEntry e in _entries)
        {
            if (e.Key is { } k && frame >= e.Frame && frame < e.Frame + e.Hold)
            {
                set = set.With(k);
            }
        }

        return set;
    }

    /// <summary>
    /// The pointer state scripted for <paramref name="frame"/>: the last move, click or drag position (in canvas pixels, kept exact in
    /// <see cref="PointerSample.CanvasX"/>), the buttons held, the wheel of this step.
    /// </summary>
    public PointerSample PointerAt(long frame, PointerSample previous)
    {
        float cx = float.IsNaN(previous.CanvasX) ? (previous.X + 0.5f) * Viewport.CanvasWidth / Viewport.PointerWidth : previous.CanvasX;
        float cy = float.IsNaN(previous.CanvasY) ? (previous.Y + 0.5f) * Viewport.CanvasHeight / Viewport.PointerHeight : previous.CanvasY;
        PointerButtons held = PointerButtons.None;
        int wheel = 0;
        bool inside = previous.Inside;
        foreach (ScriptEntry e in _entries)
        {
            if (e.Verb is null)
            {
                continue;
            }

            if (e.Verb is "click" or "rclick" or "mclick" or "move")
            {
                if (frame < e.Frame)
                {
                    continue;
                }

                string[] p = e.Argument!.Split(',');
                cx = float.Parse(p[0], CultureInfo.InvariantCulture);
                cy = float.Parse(p[1], CultureInfo.InvariantCulture);
                inside = true;
                if (e.Verb != "move" && frame < e.Frame + e.Hold)
                {
                    held |= e.Verb == "click" ? PointerButtons.Left : e.Verb == "rclick" ? PointerButtons.Right : PointerButtons.Middle;
                }
            }
            else if (e.Verb == "drag")
            {
                // drag=x0,y0,x1,y1[,steps]: the left button goes down at (x0, y0) at the entry's frame, the pointer runs to (x1, y1) in a straight line over
                // `steps` steps (30 when absent) and the button comes up one step later; the pointer stays at (x1, y1)
                if (frame < e.Frame)
                {
                    continue;
                }

                string[] p = e.Argument!.Split(',');
                float x0 = float.Parse(p[0], CultureInfo.InvariantCulture);
                float y0 = float.Parse(p[1], CultureInfo.InvariantCulture);
                float x1 = float.Parse(p[2], CultureInfo.InvariantCulture);
                float y1 = float.Parse(p[3], CultureInfo.InvariantCulture);
                int steps = p.Length > 4 ? Math.Max(1, int.Parse(p[4], CultureInfo.InvariantCulture)) : 30;
                float t = Math.Clamp((frame - e.Frame) / (float)steps, 0f, 1f);
                cx = x0 + ((x1 - x0) * t);
                cy = y0 + ((y1 - y0) * t);
                inside = true;
                if (frame <= e.Frame + steps)
                {
                    held |= PointerButtons.Left;
                }
            }
            else if (e.Verb == "wheel" && frame == e.Frame)
            {
                wheel += int.Parse(e.Argument!, CultureInfo.InvariantCulture);
            }
        }

        short x = (short)Math.Clamp((int)(cx * Viewport.PointerWidth / Viewport.CanvasWidth), 0, Viewport.PointerWidth - 1);
        short y = (short)Math.Clamp((int)(cy * Viewport.PointerHeight / Viewport.CanvasHeight), 0, Viewport.PointerHeight - 1);
        return new PointerSample(x, y, inside, held, wheel, cx, cy);
    }

    /// <summary>The gamepad scripted for <paramref name="frame"/> (<c>pad=Button[*steps]</c>, package K16): the buttons of every entry that is still held.</summary>
    public PadSnapshot PadAt(long frame)
    {
        PadSnapshot pad = default;
        foreach (ScriptEntry e in _entries)
        {
            if (e.Verb != "pad")
            {
                continue;
            }

            string[] parts = e.Argument!.Split('*');
            int hold = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 2;
            if (frame < e.Frame || frame >= e.Frame + hold)
            {
                continue;
            }

            pad = pad.Merge(parts[0].ToLowerInvariant() switch
            {
                "south" or "cross" or "a" => new PadSnapshot(South: true),
                "east" or "circle" or "b" => new PadSnapshot(East: true),
                "west" or "square" or "x" => new PadSnapshot(West: true),
                "north" or "triangle" or "y" => new PadSnapshot(North: true),
                "l1" => new PadSnapshot(LeftShoulder: true),
                "r1" => new PadSnapshot(RightShoulder: true),
                "l2" => new PadSnapshot(LeftTrigger: short.MaxValue),
                "r2" => new PadSnapshot(RightTrigger: short.MaxValue),
                "start" => new PadSnapshot(Start: true),
                "back" => new PadSnapshot(Back: true),
                "up" => new PadSnapshot(Up: true),
                "down" => new PadSnapshot(Down: true),
                "left" => new PadSnapshot(Left: true),
                "right" => new PadSnapshot(Right: true),
                _ => throw new FormatException($"unknown pad button '{parts[0]}' in the script"),
            });
        }

        return pad;
    }

    /// <summary>The entries with the verb <paramref name="verb"/> due at <paramref name="frame"/>.</summary>
    public IEnumerable<ScriptEntry> VerbsAt(long frame, string verb) => _entries.Where(e => e.Frame == frame && e.Verb == verb);

    /// <summary>True when a <c>log</c> entry is due at <paramref name="frame"/>.</summary>
    public bool LogAt(long frame) => _entries.Any(e => e.Frame == frame && e.Verb == "log");

    /// <summary>True when a <c>quit</c> entry is due at <paramref name="frame"/>.</summary>
    public bool QuitAt(long frame) => _entries.Any(e => e.Frame == frame && e.Verb == "quit");
}
