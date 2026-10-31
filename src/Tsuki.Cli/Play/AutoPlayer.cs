using System.Globalization;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;

namespace Tsuki.Cli.Play;

/// <summary>How the headless player picks a choice line.</summary>
internal abstract class ChoicePolicy
{
    /// <summary>The index of the line to pick (an unlocked line).</summary>
    public abstract int Pick(GameHost host, int point);

    /// <summary>The unlocked line indices of the open choice.</summary>
    protected static List<int> Unlocked(GameHost host)
    {
        var l = new List<int>();
        for (int i = 0; i < host.ChoiceLines.Count; i++)
        {
            if (!host.ChoiceLines[i].Locked)
            {
                l.Add(i);
            }
        }

        if (l.Count == 0)
        {
            for (int i = 0; i < host.ChoiceLines.Count; i++)
            {
                l.Add(i);
            }
        }

        return l;
    }

    /// <summary>Parses <c>first</c>, <c>last</c>, <c>random[:seed]</c>, <c>list:1,0,2</c> (indices of the lines, then the first line).</summary>
    public static ChoicePolicy Parse(string text)
    {
        string[] p = text.Split(':', 2);
        switch (p[0].ToLowerInvariant())
        {
            case "first":
                return new FirstPolicy();
            case "last":
                return new LastPolicy();
            case "random":
                return new RandomPolicy(p.Length > 1 ? int.Parse(p[1], CultureInfo.InvariantCulture) : 1);
            case "list":
                return new ListPolicy(p.Length > 1 ? p[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray() : []);
            default:
                throw new ArgumentException($"unknown choice policy '{text}' (first, last, random[:seed], list:i,j,...)");
        }
    }
}

internal sealed class FirstPolicy : ChoicePolicy
{
    public override int Pick(GameHost host, int point) => Unlocked(host)[0];
}

internal sealed class LastPolicy : ChoicePolicy
{
    public override int Pick(GameHost host, int point) => Unlocked(host)[^1];
}

internal sealed class RandomPolicy(int seed) : ChoicePolicy
{
    private readonly Random _rng = new(seed);

    public override int Pick(GameHost host, int point)
    {
        List<int> u = Unlocked(host);
        return u[_rng.Next(u.Count)];
    }
}

internal sealed class ListPolicy(int[] picks) : ChoicePolicy
{
    private int _next;

    public override int Pick(GameHost host, int point)
    {
        List<int> u = Unlocked(host);
        int want = _next < picks.Length ? picks[_next++] : 0;
        return u.Contains(want) ? want : u[0];
    }
}

/// <summary>
/// The coverage policy: picks the line (by its value) taken least often at this choice point so far, over all playthroughs of the run; ties go to
/// the lowest line. The counts are shared between playthroughs.
/// </summary>
internal sealed class CoveragePolicy : ChoicePolicy
{
    private readonly Dictionary<(int Point, int Value), int> _taken = [];
    private readonly Random _rng;

    public CoveragePolicy(int seed) => _rng = new Random(seed);

    public int Distinct => _taken.Count;

    public override int Pick(GameHost host, int point)
    {
        List<int> u = Unlocked(host);
        int best = u[0];
        int bestCount = int.MaxValue;
        var ties = new List<int>();
        foreach (int i in u)
        {
            int c = _taken.GetValueOrDefault((point, host.ChoiceLines[i].Value));
            if (c < bestCount)
            {
                bestCount = c;
                ties.Clear();
                ties.Add(i);
            }
            else if (c == bestCount)
            {
                ties.Add(i);
            }
        }

        best = ties[ties.Count == 1 ? 0 : _rng.Next(ties.Count)];
        (int, int) key = (point, host.ChoiceLines[best].Value);
        _taken[key] = _taken.GetValueOrDefault(key) + 1;
        return best;
    }
}

/// <summary>
/// Aims at one scene: follows the static path of the scene graph (at each choice it picks the option whose jump leads to the next script of the
/// path) and chooses by the coverage rule everywhere else.
/// </summary>
internal sealed class TargetPolicy(SceneGraph graph, List<int> path, ChoicePolicy fallback) : ChoicePolicy
{
    public override int Pick(GameHost host, int point)
    {
        int s = host.Script;
        int at = path.IndexOf(s);
        if (at >= 0 && at + 1 < path.Count)
        {
            ChoiceBlock? block = graph.BlockAt(s, host.Machine.Cursor);
            if (block is not null)
            {
                for (int i = 0; i < host.ChoiceLines.Count; i++)
                {
                    ChoiceLine l = host.ChoiceLines[i];
                    if (!l.Locked && block.NextScriptByValue.TryGetValue(l.Value, out int next) && next == path[at + 1])
                    {
                        return i;
                    }
                }
            }
        }

        return fallback.Pick(host, point);
    }
}

/// <summary>How the headless player advances the text.</summary>
internal enum PlayMode
{
    /// <summary>Presses decide every other frame (the click of a player); timers and reveals run in real frames.</summary>
    Click,

    /// <summary>Latches skip mode with the read-only rule off: 64 times faster timers, one message per frame.</summary>
    Skip,

    /// <summary>Turns auto mode on and presses nothing.</summary>
    Auto,
}

/// <summary>Produces the input of each frame of a headless playthrough and answers the choices.</summary>
internal sealed class AutoPlayer(ChoicePolicy policy, PlayMode mode, int dialogAnswer = 1)
{
    private PadButtons _held;
    private long _frame;

    public int ChoicesTaken { get; private set; }

    /// <summary>Raised for each decided choice: point, line index, value, line count.</summary>
    public event Action<int, int, int, int>? Chose;

    /// <summary>The input of the next frame; answers an open choice first.</summary>
    public InputFrame Next(GameHost host)
    {
        _frame++;
        if (host.ChoiceSelectable)
        {
            int point = host.ChoicePoint;
            if (host.ChoiceKind == ChoiceKind.List)
            {
                int pick = policy.Pick(host, point);
                int value = host.ChoiceLines[pick].Value;
                int count = host.ChoiceLines.Count;
                host.Choose(pick);
                ChoicesTaken++;
                Chose?.Invoke(point, pick, value, count);
            }
            else
            {
                // buttons: 0 No, 1 Yes. SELA (save prompt): No, which skips the save screen; SELS (yes / no system prompt): dialogAnswer, Yes by default
                int answer = host.ChoiceKind == ChoiceKind.SavePrompt ? 0 : dialogAnswer;
                host.Choose(answer);
                ChoicesTaken++;
                Chose?.Invoke(point, answer, answer, 2);
            }
        }

        PadButtons held = PadButtons.None;
        switch (mode)
        {
            case PlayMode.Click:
                held = (_frame & 1) == 0 ? PadButtons.Decide : PadButtons.None;
                break;
            case PlayMode.Skip:
                held = (_frame & 1) == 0 ? PadButtons.Decide : PadButtons.None;
                if (!host.MovieActive)
                {
                    host.SetSkipLatch(true);
                }

                break;
            case PlayMode.Auto:
                host.SetAutoMode(true);
                break;
        }

        PadButtons pressed = held & ~_held;
        _held = held;
        return new InputFrame { Frame = _frame, Held = held, Pressed = pressed, Repeated = pressed };
    }
}
