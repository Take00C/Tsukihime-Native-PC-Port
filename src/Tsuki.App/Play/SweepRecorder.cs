// The route sweep of the verification mode (package K15): a long autoplayed run writes small screenshots at the moments that show whether the game looks right (a
// scene starts, a choice opens, a movie plays, a story ends, the committed pictures change, a steady interval) and a manifest that says what each one is, so
// a person or an agent can page through a whole route as contact sheets. It only reads the host and the frame buffer; it changes nothing in the game.
using System.Globalization;
using System.Text;
using Tsuki.Engine.Game;
using Tsuki.Engine.Stage;

namespace Tsuki.App.Play;

/// <summary>Writes the sweep's screenshots and manifest.</summary>
internal sealed class SweepRecorder : IDisposable
{
    private readonly AppArgs _args;
    private readonly string _dir;
    private readonly StreamWriter _manifest;
    private readonly Func<(int Width, int Height, byte[] Rgba)> _grab;
    private readonly Func<int, string> _scriptName;
    private readonly List<(long Due, string Tag)> _pending = [];
    private long _lastPeriodic;
    private long _lastChange = long.MinValue / 2;
    private int _signature;
    private bool _choiceWas;
    private long _movieStart;
    private int _movieShots;
    private int _seq;
    private long _messages;
    private long _messageShot;

    /// <summary>Opens the sweep folder.</summary>
    public SweepRecorder(AppArgs args, Func<(int Width, int Height, byte[] Rgba)> grab, Func<int, string> scriptName)
    {
        _args = args;
        _dir = Path.GetFullPath(args.SweepDir!);
        _grab = grab;
        _scriptName = scriptName;
        Directory.CreateDirectory(Path.Combine(_dir, "shots"));
        _manifest = new StreamWriter(Path.Combine(_dir, "manifest.tsv"), append: true, new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>Shots written so far.</summary>
    public int Count => _seq;

    /// <summary>A scene started (a script entered).</summary>
    public void SceneEntered(long step)
    {
        if (_args.SweepScene > 0)
        {
            _pending.Add((step + _args.SweepScene, "scene"));
        }
    }

    /// <summary>A message started (its page text is laid out; the shot is taken once it is fully shown).</summary>
    public void MessageStarted() => _messages++;

    /// <summary>A movie started.</summary>
    public void MovieStarted(long step)
    {
        _movieStart = step;
        _movieShots = 0;
        _pending.Add((step + 45, "movie"));
    }

    /// <summary>A story ended: the last frame shown is the ending's.</summary>
    public void StoryEnded(long step) => _pending.Add((step, "end"));

    /// <summary>Called after every drawn frame.</summary>
    public void AfterDraw(long step, GameHost host, bool storyRunning, string ui, int missingTextures = 0)
    {
        if (_seq >= _args.SweepMax)
        {
            return;
        }

        string? tag = null;
        for (int i = 0; i < _pending.Count; i++)
        {
            if (_pending[i].Due <= step)
            {
                tag = _pending[i].Tag;
                _pending.RemoveAt(i);
                break;
            }
        }

        if (storyRunning)
        {
            bool choice = host.ChoiceSelectable && host.ChoiceKind == ChoiceKind.List;
            if (tag is null && choice && !_choiceWas)
            {
                tag = "choice";
            }

            _choiceWas = choice;
            bool movie = host.MovieActive;
            if (tag is null && movie && step - _movieStart >= 240 * (_movieShots + 1) && _movieShots < 3)
            {
                tag = "movie";
                _movieShots++;
            }

            if (_args.SweepChanges > 0)
            {
                int sig = Signature(host.Stage);
                if (sig != _signature)
                {
                    _signature = sig;
                    if (step - _lastChange >= _args.SweepChanges)
                    {
                        _lastChange = step;
                        _pending.Add((step + 8, "pic"));
                    }
                }
            }

            if (tag is null && _args.SweepMessages > 0 && _messages != _messageShot && _messages % _args.SweepMessages == 0 && TextSettled(host))
            {
                _messageShot = _messages;
                tag = "m";
            }

            if (tag is null && _args.SweepEvery > 0 && step - _lastPeriodic >= _args.SweepEvery)
            {
                tag = "t";
            }
        }

        if (tag is not null)
        {
            if (tag == "t" || _lastPeriodic == 0)
            {
                _lastPeriodic = step;
            }

            Shoot(step, missingTextures > 0 ? tag + "_MISS" : tag, host, ui);
        }
    }

    // the message on the page is fully revealed with its window and text faded in: the picture a reader sees while deciding to click
    private static bool TextSettled(GameHost host) =>
        host.CurrentMessage is { } m && !m.Block.Empty && host.Wait.WindowAlpha >= 0.99f && host.TextFade >= 0.99f && !host.ChoiceOpen
        && WaitModel.RevealDone(m.Records, host.Wait.Head, host.Wait.Tail);

    // the committed pictures: which pictures are on which layers
    private static int Signature(StageRuntime stage)
    {
        var h = new HashCode();
        LayerRecord[] layers = stage.Committed;
        for (int i = 0; i < layers.Length; i++)
        {
            LayerRecord l = layers[i];
            if (l.Used != 0 && l.Pic >= 0 && l.Alpha > 0.02f)
            {
                h.Add(i);
                h.Add(l.Pic);
                h.Add(l.Face);
            }
        }

        return h.ToHashCode();
    }

    private void Shoot(long step, string tag, GameHost host, string ui)
    {
        (int w, int h, byte[] rgba) = _grab();
        int d = Math.Max(1, _args.SweepScale);
        int sw = w / d;
        int sh = h / d;
        var small = new byte[sw * sh * 4];
        int area = d * d;
        for (int y = 0; y < sh; y++)
        {
            for (int x = 0; x < sw; x++)
            {
                int r = 0;
                int g = 0;
                int b = 0;
                for (int dy = 0; dy < d; dy++)
                {
                    int row = (((y * d) + dy) * w + (x * d)) * 4;
                    for (int dx = 0; dx < d; dx++)
                    {
                        int p = row + (dx * 4);
                        r += rgba[p];
                        g += rgba[p + 1];
                        b += rgba[p + 2];
                    }
                }

                int o = ((y * sw) + x) * 4;
                small[o] = (byte)(r / area);
                small[o + 1] = (byte)(g / area);
                small[o + 2] = (byte)(b / area);
                small[o + 3] = 255;
            }
        }

        _seq++;
        string file = Path.Combine("shots", $"{_seq:D6}_{step:D8}_{tag}.png");
        PngWriter.Write(Path.Combine(_dir, file), sw, sh, small);
        string text = host.CurrentMessage is { } m ? Encoding.UTF8.GetString(m.Text).Replace('\n', '|').Replace('\t', ' ') : "-";
        if (text.Length > 70)
        {
            text = text[..70];
        }

        string name = host.Phase == GamePhase.Running ? _scriptName(host.Script) : "-";
        _manifest.WriteLine(string.Join('\t', _seq.ToString(CultureInfo.InvariantCulture), file.Replace('\\', '/'), step.ToString(CultureInfo.InvariantCulture), tag, name, host.Phase == GamePhase.Running ? host.SceneId.ToString(CultureInfo.InvariantCulture) : "-", ui, text));
    }

    /// <inheritdoc/>
    public void Dispose() => _manifest.Dispose();
}
