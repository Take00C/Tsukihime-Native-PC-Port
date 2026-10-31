// The autoplay of the verification mode (package K6): clicks through the boot notice and the title, advances the story, answers choices by a policy and
// can hold the fast-forward key, so a run reaches a scene without a person. It only produces keys and answers choices; the app runs as it would for a player.
using System.Globalization;
using Tsuki.Engine.Game;
using Tsuki.Platform.Input;
using Tsuki.Ui.Screens;

namespace Tsuki.App.Play;

/// <summary>How an open choice is answered.</summary>
internal sealed class ChoicePolicy
{
    private readonly string _kind;
    private readonly Random _rng;
    private readonly int[] _list;
    private int _next;

    private ChoicePolicy(string kind, int seed, int[] list)
    {
        _kind = kind;
        _rng = new Random(seed);
        _list = list;
    }

    /// <summary>Parses <c>first</c>, <c>last</c>, <c>random[:seed]</c> or <c>list:1,0,2</c> (line indices in the order the choices come).</summary>
    public static ChoicePolicy Parse(string text)
    {
        string[] p = text.Split(':', 2);
        return p[0].ToLowerInvariant() switch
        {
            "first" => new ChoicePolicy("first", 1, []),
            "last" => new ChoicePolicy("last", 1, []),
            "random" => new ChoicePolicy("random", p.Length > 1 ? int.Parse(p[1], CultureInfo.InvariantCulture) : 1, []),
            "list" => new ChoicePolicy("list", 1, p.Length > 1 ? p[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray() : []),
            _ => throw new ArgumentException($"unknown choice policy '{text}' (first, last, random[:seed], list:i,j,...)"),
        };
    }

    /// <summary>The line to pick among the unlocked ones of the open choice.</summary>
    public int Pick(GameHost host)
    {
        var unlocked = new List<int>();
        for (int i = 0; i < host.ChoiceLines.Count; i++)
        {
            if (!host.ChoiceLines[i].Locked)
            {
                unlocked.Add(i);
            }
        }

        if (unlocked.Count == 0)
        {
            return 0;
        }

        return _kind switch
        {
            "last" => unlocked[^1],
            "random" => unlocked[_rng.Next(unlocked.Count)],
            "list" => unlocked.Contains(_next < _list.Length ? _list[_next] : 0) ? _list[_next++] : unlocked[0],
            _ => unlocked[0],
        };
    }
}

/// <summary>Autoplay.</summary>
internal sealed class AutoPlayer(string mode, ChoicePolicy policy)
{
    /// <summary>Choices answered so far.</summary>
    public int ChoicesTaken { get; private set; }

    /// <summary>Raised when a choice was answered: point, line, line count.</summary>
    public event Action<int, int, int>? Chose;

    /// <summary>While true autoplay presses nothing and answers nothing (set by the script verbs <c>autopause</c> and <c>autoresume</c>).</summary>
    public bool Paused { get; set; }

    /// <summary>While true an open choice is left unanswered (the verification run takes its screenshot first).</summary>
    public bool HoldChoice { get; set; }

    /// <summary>While true the story's yes / no prompts (<c>_SELA</c>, <c>_SELS</c>) are left unanswered; lists are still answered (the script verbs <c>holdprompt</c> and <c>releaseprompt</c>).</summary>
    public bool HoldPrompt { get; set; }

    /// <summary>True when the last step found a choice open and selectable (the run's "first choice" event).</summary>
    public bool ChoiceSeen { get; private set; }

    /// <summary>The keys autoplay holds in step <paramref name="frame"/>; answers an open choice through the host.</summary>
    public KeySet Keys(AppRuntime runtime, long frame, bool answerChoices = true)
    {
        KeySet keys = KeySet.None;
        if (Paused)
        {
            return keys;
        }

        GameHost host = runtime.Host;
        bool press = (frame & 1) == 0;
        if (runtime.Ui.Active)
        {
            // the boot notice, the title and the cautions: any button moves them on
            if (runtime.Ui.Stack.Count > 0 && runtime.Ui.Stack[0] is BootScreen or TitleScreen && (frame % 10) < 3)
            {
                if (runtime.Ui.Stack[0] is TitleScreen && runtime.Services.Title.ContinueSlot >= 0)
                {
                    // a profile with a save puts the cursor on Continue; the autoplay wants the next playthrough, so it does what New Game does (package K15)
                    if (!runtime.Services.StoryActive && frame % 60 == 0)
                    {
                        runtime.Services.StartScene(host.NewGameSceneId());
                    }
                }
                else
                {
                    keys = keys.With(PcKey.Enter);
                }
            }

            return keys;
        }

        if (!runtime.Services.StoryActive || host.Phase != GamePhase.Running)
        {
            return keys;
        }

        ChoiceSeen = false;
        if (host.ChoiceSelectable)
        {
            ChoiceSeen = true;
            if (answerChoices && !HoldChoice && !(HoldPrompt && host.PromptOpen))
            {
                int point = host.ChoicePoint;
                if (host.ChoiceKind == ChoiceKind.List)
                {
                    int pick = policy.Pick(host);
                    int count = host.ChoiceLines.Count;
                    host.Choose(pick);
                    ChoicesTaken++;
                    Chose?.Invoke(point, pick, count);
                }
                else
                {
                    // the two-button prompts: button 0 is No, 1 is Yes. The save prompt (_SELA) is answered No, so the script goes on without the save screen; the
                    // yes / no system prompt (_SELS) is answered Yes
                    int answer = host.ChoiceKind == ChoiceKind.SavePrompt ? 0 : 1;
                    host.Choose(answer);
                    ChoicesTaken++;
                    Chose?.Invoke(point, answer, 2);
                }
            }

            return keys;
        }

        if (press)
        {
            keys = keys.With(PcKey.Enter);
        }

        if (mode == "skip" && !host.MovieActive)
        {
            keys = keys.With(PcKey.LeftCtrl);
        }
        else if (mode == "auto" && !host.AutoMode)
        {
            host.SetAutoMode(true);
        }

        return keys;
    }
}

/// <summary>The scripted and autoplayed input: keys and pointer from a script, plus autoplay, through the input mapper.</summary>
internal sealed class ScriptedInput(InputScript? script, AutoPlayer? auto) : IInputSource
{
    private readonly InputMapper _mapper = new();
    private PointerSample _pointer = new(0, 0, false, Tsuki.Engine.Contracts.PointerButtons.None, 0);

    /// <summary>The runtime autoplay looks at; set once the runtime exists.</summary>
    public AppRuntime? Runtime { get; set; }

    /// <inheritdoc/>
    public Tsuki.Engine.Contracts.InputFrame Sample(long frame)
    {
        KeySet keys = script?.KeysAt(frame) ?? KeySet.None;
        if (auto is not null && script is not null)
        {
            if (script.VerbsAt(frame, "autopause").Any())
            {
                auto.Paused = true;
            }

            if (script.VerbsAt(frame, "autoresume").Any())
            {
                auto.Paused = false;
            }

            if (script.VerbsAt(frame, "holdprompt").Any())
            {
                auto.HoldPrompt = true;
            }

            if (script.VerbsAt(frame, "releaseprompt").Any())
            {
                auto.HoldPrompt = false;
            }

            if (script.VerbsAt(frame, "holdchoice").Any())
            {
                auto.HoldChoice = true;
            }

            if (script.VerbsAt(frame, "releasechoice").Any())
            {
                auto.HoldChoice = false;
            }
        }

        if (auto is not null && Runtime is not null)
        {
            KeySet a = auto.Keys(Runtime, frame);
            keys = new KeySet(keys.Bits | a.Bits);
        }

        _pointer = script?.PointerAt(frame, _pointer) ?? _pointer;
        return _mapper.Map(frame, keys, script?.PadAt(frame) ?? default, _pointer);
    }
}
