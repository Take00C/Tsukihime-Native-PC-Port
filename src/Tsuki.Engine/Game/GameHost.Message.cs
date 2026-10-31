using Tsuki.Engine.Contracts;
using Tsuki.Engine.Sound;
using Tsuki.Engine.Stage;
using Tsuki.Engine.Text;
using Tsuki.Engine.Vm;

namespace Tsuki.Engine.Game;

/// <summary>One message of the page: the text as the script gave it, its layout and the page variables it recorded.</summary>
public sealed class PageMessage
{
    /// <summary>The copied argument (unexpanded, at most 0x300 bytes; grows with <c>_MSAD</c>).</summary>
    public byte[] Raw { get; set; } = [];

    /// <summary>The expanded text (with its markup).</summary>
    public byte[] Text { get; set; } = [];

    /// <summary>The laid-out block.</summary>
    public TextBlock Block { get; set; } = null!;

    /// <summary>The (flags, counter) pairs of the block's glyph records (the 0xCA070 reveal-done test).</summary>
    public (int Flags, int Ctr)[] Records { get; set; } = [];

    /// <summary>The end mode.</summary>
    public MessageEnd End { get; set; }

    /// <summary>The RGB565 colour word the layout started with.</summary>
    public int Colour { get; set; }

    /// <summary>The message was read before.</summary>
    public bool Read { get; set; }

    /// <summary>C846 as the message saw it: empty lines left before it (textbox.md 5.8).</summary>
    public int Blank { get; set; }

    /// <summary>C835 / 24: the vertical text scale.</summary>
    public double ScaleY { get; set; } = 1.0;

    /// <summary>The message counter F823 when the message started.</summary>
    public int Index { get; set; }

    /// <summary>The window index F1105 (F1109 in English when positive) the message was laid out with.</summary>
    public int Window { get; set; }

    /// <summary>Frames the message has been dimmed (-1: fully dimmed).</summary>
    public int DimTimer { get; set; }

    /// <summary>The dimming limit in frames: <c>(C870 + 128) * 1.2</c>.</summary>
    public float DimLimit { get; set; } = 153.6f;

    /// <summary>The <c>@x</c> continue position the message was laid out with (<see cref="Tsuki.Engine.Text.MessageWindow.ContinueX"/> of the message before it; 0 for the first of a page).</summary>
    public float SavedX { get; set; }
}

public sealed partial class GameHost
{
    private const int MaxMessageBytes = 0x300;
    private const int MaxExpandedBytes = 0x2FC;

    private readonly List<PageMessage> _page = [];
    private PageMessage? _current;
    private byte[] _currentRaw = [];
    private float _textFade = 1f;
    private bool _windowQuick;
    private bool _zmStartedVoice;
    /// <summary>The continue byte (VA 0x16541F4): the message ended in <c>@n</c> and the next <c>_MSAD</c> appends to it.</summary>
    public bool ContinueByte { get; private set; }
    private bool _windowHidden;

    /// <summary>The player hid the message window (action 25); a new message shows it again.</summary>
    public bool WindowHidden => _windowHidden;

    private void InitMessages()
    {
    }

    private void ResetMessages()
    {
        _page.Clear();
        _current = null;
        _currentRaw = [];
        Array.Clear(_namePlate);
        _namePlateBlock = null;
        _textFade = 1f;
        _windowQuick = false;
        _zmStartedVoice = false;
        ContinueByte = false;
        _windowHidden = false;
    }

    /// <summary>The message currently being shown or waiting for a click, or null.</summary>
    public PageMessage? CurrentMessage => _current;

    /// <summary>The messages of the page, oldest first, the current one last.</summary>
    public IReadOnlyList<PageMessage> PageMessages => _page;

    /// <summary>The text fade T (VA 0x86AA1C), 0..1.</summary>
    public float TextFade => _textFade;

    /// <summary>The text of the last message as plain bytes (the markup is kept), for traces.</summary>
    public ReadOnlyMemory<byte> CurrentText => _current?.Text;

    // -- variable access helpers --------------------------------------------------------------------------------------------------

    /// <summary>Signed value of F word <paramref name="n"/>.</summary>
    public int F(int n) => (short)Vars.GetF(n);

    private void SetF(int n, int value) => Vars.SetF(n, unchecked((ushort)value));

    /// <summary>Loads the F words the wait functions read into <see cref="Wait"/>.</summary>
    private void SyncWaitFromVars()
    {
        Wait.F799 = F(799);
        Wait.F823 = F(823);
        Wait.F988 = F(988);
        Wait.F1103 = F(1103);
        Wait.F1123 = F(1123);
    }

    // -- text -----------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The message argument of a <c>_ZM</c> / <c>_MSAD</c> statement starting at <paramref name="start"/> of its arguments: the copy loop of
    /// VA 0x92458 stops at <c>)</c>, at <c>@n</c> or <c>@e</c> (which are dropped) or at 0x300 bytes (wait_model.md 4.1).
    /// </summary>
    internal static (byte[] Raw, char Terminator) CopyMessage(ReadOnlySpan<byte> args, int start)
    {
        var raw = new List<byte>(64);
        char term = ')';
        for (int i = start; i < args.Length && raw.Count < MaxMessageBytes - 1; i++)
        {
            byte c = args[i];
            if (c == (byte)')')
            {
                break;
            }

            if (c == (byte)'@' && i + 1 < args.Length && args[i + 1] is (byte)'n' or (byte)'e')
            {
                term = (char)args[i + 1];
                break;
            }

            if (c == (byte)';' && i + 1 >= args.Length)
            {
                break;
            }

            raw.Add(c);
        }

        return ([.. raw], term);
    }

    /// <summary>
    /// The <c>$NNNNNN</c> expansion of the engine (VA 0x76040..0x76200, script.md 5): exactly six digits name line NNNNNN of the current
    /// language; the result is cut at 0x2FC bytes and at the first NUL.
    /// </summary>
    internal byte[] ExpandMessage(ReadOnlySpan<byte> message)
    {
        var output = new List<byte>(message.Length + 32);
        int i = 0;
        while (i < message.Length && output.Count < MaxExpandedBytes)
        {
            byte c = message[i];
            if (c == 0)
            {
                break;
            }

            if (c == (byte)'$')
            {
                long value = 0;
                for (int k = 1; k <= 6; k++)
                {
                    value = (value * 10) + (i + k < message.Length ? message[i + k] : 0);
                }

                value -= 0x30L * 111111;
                i += 7;
                if (value >= 0 && value < _data.TextLineCount)
                {
                    ReadOnlyMemory<byte> line = _data.TextLine(_language, (int)value);
                    int room = Math.Max(MaxExpandedBytes - output.Count, 0);
                    ReadOnlySpan<byte> l = line.Span[..Math.Min(room, line.Length)];
                    int nul = l.IndexOf((byte)0);
                    output.AddRange((nul >= 0 ? l[..nul] : l).ToArray());
                }

                continue;
            }

            output.Add(c);
            i++;
        }

        return [.. output];
    }

    private int WindowIndex()
    {
        int w = F(1105);
        if (_language == Language.En && F(1109) > 0)
        {
            w = F(1109);
        }

        return w;
    }

    private bool IsPageMode => F(822) == 1;

    private (TextBlock Block, int Colour) LayoutText(byte[] text, bool read, float savedX = 0f)
    {
        int window = WindowIndex();
        int f31 = F(31);
        int f32 = F(32);
        BoxParams bp = MessageWindow.MainBoxParams(_language, window, read, Config.TextColourMode, f31, f32);
        TextBlock block = TextLayout.Layout(text, _language, _fonts, bp.ToLayoutParams(savedX: savedX));
        return (block, bp.Color);
    }

    private static (int, int)[] RecordsOf(TextBlock block)
    {
        var r = new (int, int)[block.Records.Length];
        for (int i = 0; i < r.Length; i++)
        {
            r[i] = (block.Records[i].Flags, block.Records[i].Ctr);
        }

        return r;
    }

    /// <summary>The 0xCA070 reveal-done value of a block (slot + 4 is the current message; the extra blocks are never used in retail).</summary>
    private int RevealDoneValue(int block)
    {
        if (block != 4 || _current is null || _current.Block.Empty)
        {
            return 1;
        }

        return WaitModel.RevealDone(_current.Records, Wait.Head, Wait.Tail) ? 1 : 0;
    }

    // -- _ZM / _MSAD --------------------------------------------------------------------------------------------------------------

    /// <summary>0x75CD0(1): the previous message is dismissed; the next one gets a new slot (head and tail are saved and cleared).</summary>
    private void NewMessageSlot()
    {
        Wait.NewPage = 0;
        Wait.Flags = 0;
        Wait.Head = 0;
        Wait.Tail = 0;
    }

    private StepResult CmdMessage(in Statement st)
    {
        CommitLayerChange(st);
        _windowHidden = false;
        Push(HistoryKind.Message, st);
        if (Wait.NewPage != 0)
        {
            NewMessageSlot();
        }

        // 1. text fade (textbox.md 3.1)
        if (!(Wait.TextFadeStep > 0f))
        {
            if (_textFade < 1f || F(1103) != 0)
            {
                _textFade = 0f;
                Wait.TextFadeStep = 1f / 18f;
            }
            else
            {
                _textFade = 1f;
                Wait.TextFadeStep = 0f;
            }
        }

        SetF(1103, 0);
        Wait.F1103 = 0;

        // 2. copy, end mode, expansion
        (byte[] raw, char term) = CopyMessage(st.Args.Span, ArgumentList.StartIn(st));
        byte[] text = ExpandMessage(raw);
        bool read = Markers.IsRead;
        if (SkipScene && (F(998) != 0 || (!read && Config.FastForwardReadOnly != 0)))
        {
            EndSceneSkip();                          // the next-scene skip stops at a message that is unread (or when F998 is set), adv_state.md 5.2
        }

        int f823 = F(823);
        if (f823 <= 1)
        {
            Wait.PageLatch = (read ? Config.PageEndInstantSeen : Config.PageEndInstantUnseen) == 1;
        }

        MessageEnd mode = term switch
        {
            'n' => MessageEnd.Continue,
            'e' => MessageEnd.NoWait,
            _ => Wait.PageLatch && F(799) != 1 ? MessageEnd.NoWait : MessageEnd.Click,
        };

        // 3. the page: a message with F823 <= 1 starts a new page
        if (f823 <= 1 || !IsPageMode)
        {
            _page.Clear();
        }

        // an @x message continues where the message before it on the page ended (_ZM: 0x7C340(1) then 0xC6BB0(saved_x), VA 0x927CE..0x927D3); the first message of a page has none
        float savedX = _page.Count > 0 ? MessageWindow.ContinueX(_page[^1].Block) : 0f;
        (TextBlock block, int colour) = LayoutText(text, read, savedX);
        LayoutNamePlate();
        if (IsPageMode)
        {
            int k = Math.Clamp(f823, 0, 11);
            SetF(799 + k, colour >> 8);
            SetF(810 + k, colour & 0xFF);
            SetF(824 + k, F(31));
            SetF(835 + k, F(32));
        }

        int f32 = F(32);
        var msg = new PageMessage
        {
            Raw = raw,
            Text = text,
            Block = block,
            Records = RecordsOf(block),
            End = mode,
            Colour = colour,
            Read = read,
            Blank = F(846 + f823),
            ScaleY = f32 > 0 ? f32 / 24.0 : 1.0,
            Index = f823,
            Window = WindowIndex(),
            DimLimit = (F(870 + f823) + 128) * 1.2f,
            SavedX = savedX,
        };
        _page.Add(msg);
        _current = msg;
        _currentRaw = raw;

        // 4. the wait state (tsuki.wait_model.zm_apply)
        Wait.ReadSel = read ? 1 : 0;
        Wait.Head = 0f;
        Wait.Tail = 0f;
        ContinueByte = false;
        _zmStartedVoice = Sound.StartVoiceAtMessage();
        Wait.VoiceState = Sound.VoiceState;
        Sink.OnMessageShown();
        Sink.SetMessageText(text);
        if (_zmStartedVoice)
        {
            int character = Sound.PendingVoiceCharacter;
            Sink.OnVoiceStarted(Sound.LastVoice, (short)character, (sbyte)VoiceGroups.SlotOf(character));
        }

        Sink.UpdateStaging();
        if (_zmStartedVoice && mode != MessageEnd.Continue)
        {
            Wait.Flags |= WaitBits.Voice;
        }

        Wait.Flags |= WaitBits.Reveal;
        if (mode == MessageEnd.Continue)
        {
            ContinueByte = true;
        }

        Wait.Bits |= (mode == MessageEnd.Click ? WaitBits.Click : 0) | WaitBits.Message;
        if (Wait.Flags != 0)
        {
            Wait.Bits |= Wait.Flags;
        }

        MessageStarted?.Invoke(st.Script, st.Offset, read);
        return StepResult.Yield;
    }

    /// <summary>Raised when a message starts: script, statement offset, already read.</summary>
    public event Action<int, int, bool>? MessageStarted;

    private StepResult CmdMsad(in Statement st)
    {
        CommitLayerChange(st);
        SetF(1103, 0);
        Wait.F1103 = 0;
        (byte[] add, char term) = CopyMessage(st.Args.Span, ArgumentList.StartIn(st));
        PageMessage? cur = _current;
        if (cur is null)
        {
            // no message to append to (the engine appends to an empty buffer): treat as a message of its own text
            cur = new PageMessage { Index = F(823), Window = WindowIndex() };
            _page.Add(cur);
            _current = cur;
        }

        int room = Math.Max(0, MaxMessageBytes - cur.Raw.Length);
        byte[] raw = [.. cur.Raw, .. add.AsSpan(0, Math.Min(add.Length, room)).ToArray()];
        byte[] text = ExpandMessage(raw);
        MessageEnd mode = term switch
        {
            'n' => MessageEnd.Continue,
            'e' => MessageEnd.NoWait,
            _ => Wait.PageLatch && F(799) != 1 ? MessageEnd.NoWait : MessageEnd.Click,
        };

        float oldEnd = 0f;
        TextBlock old = cur.Block;
        if (old is not null && old.Records.Length > 0)
        {
            int max = 0;
            for (int i = Math.Max(0, old.Records.Length - 8); i < old.Records.Length; i++)
            {
                max = Math.Max(max, old.Records[i].Ctr);
            }

            oldEnd = max + 1;
        }

        (TextBlock block, int colour) = LayoutText(text, cur.Read, cur.SavedX);
        cur.Raw = raw;
        cur.Text = text;
        cur.Block = block;
        cur.Records = RecordsOf(block);
        cur.Colour = colour;
        cur.End = mode;
        _currentRaw = raw;
        Sink.SetMessageText(text);
        Sink.UpdateStaging();

        Wait.Head = oldEnd;
        Wait.Tail = oldEnd;
        ContinueByte = false;
        bool started = false;
        if (Sound.PendingVoice >= 0)
        {
            started = Sound.StartVoiceAtMessage();
            Wait.VoiceState = Sound.VoiceState;
        }

        if (mode != MessageEnd.Continue && (started || _zmStartedVoice))
        {
            Wait.Flags |= WaitBits.Voice;
        }

        Wait.Flags |= WaitBits.Reveal;
        if (mode == MessageEnd.Continue)
        {
            ContinueByte = true;
        }

        Wait.Bits |= (mode == MessageEnd.Click ? WaitBits.Click : 0) | WaitBits.Message;
        if (Wait.Flags != 0)
        {
            Wait.Bits |= Wait.Flags;
        }

        return StepResult.Yield;
    }

    private StepResult CmdWnty(in Statement st)
    {
        SetF(799, InlineReader.ReadInt16(st, 0) != 0 ? 1 : 0);
        return StepResult.Continue;
    }

    private StepResult CmdPgst(in Statement st)
    {
        SetF(1122, InlineReader.ReadInt16(st, 0));
        return StepResult.Continue;
    }

    // -- the window commands (textbox.md 8.2) ---------------------------------------------------------------------------------------

    private StepResult CmdClo2()
    {
        // VA 0x9A887: 0x75CD0(1) (a new page), the window target closes, the fade is 0 at once, the text length is 0, F1103 = 1
        NewMessageSlot();
        _page.Clear();
        _current = null;
        Wait.WindowTarget = 0;
        Wait.WindowAlpha = 0f;
        SetF(1103, 1);
        return StepResult.Yield;
    }

    private StepResult CmdClo3()
    {
        // VA 0x9A8B8: a window that is open closes with the fast fade (-0.2604 per frame) and F1103 = 1
        if (Wait.WindowTarget != 0)
        {
            Wait.WindowTarget = 0;
            _windowQuick = true;
            SetF(1103, 1);
        }

        return StepResult.Yield;
    }

    private const float FadeUnit = 16.66666603088379f;

    private StepResult CmdClo4(in Statement st)
    {
        // VA 0x9A8E7: the text fades out over ms (step -16.6667 / ms); at once when ms <= 0 or while fast-forwarding
        var a = new StageArgs(st);
        int ms = a.Has(0) ? a.Expr(0) : 0;
        if (ms > 0 && Mode.SkipActive == 0)
        {
            if (Wait.TextFadeStep > 0f)
            {
                _textFade = 1f;
                Wait.TextFadeStep = -FadeUnit / ms;
            }
            else if (_textFade == 1f)
            {
                Wait.TextFadeStep = -FadeUnit / ms;
            }
            else
            {
                _textFade = 0f;
                Wait.TextFadeStep = 0f;
            }
        }
        else
        {
            _textFade = 0f;
            Wait.TextFadeStep = 0f;
        }

        return StepResult.Yield;
    }

    private StepResult CmdClo5(in Statement st)
    {
        // VA 0x9A939: the text fades in over ms (step +16.6667 / ms) unless it is already visible; the window opens at full alpha now
        var a = new StageArgs(st);
        int ms = a.Has(0) ? a.Expr(0) : 0;
        if (ms > 0 && Mode.SkipActive == 0 && (Wait.TextFadeStep < 0f || _textFade == 0f || F(1103) == 1))
        {
            _textFade = 0f;
            Wait.TextFadeStep = FadeUnit / ms;
        }
        else
        {
            _textFade = 1f;
            Wait.TextFadeStep = 0f;
        }

        SetF(1103, 0);
        Wait.WindowAlpha = 1f;
        Wait.WindowTarget = 1;
        return StepResult.Yield;
    }

    // -- _WTTM / _WTSV --------------------------------------------------------------------------------------------------------------

    private StepResult CmdWttm(in Statement st)
    {
        CommitLayerChange(st);
        int ms = InlineReader.ReadInt32(st, 0);
        Wait.Timer = unchecked((short)WaitModel.WaitFrames(ms));
        Wait.TimerNoClick = InlineReader.IsPresent(st, 1) && InlineReader.ReadInt16(st, 1) != 0 ? 1 : 0;
        WaitStarted?.Invoke("WTTM", ms);
        return StepResult.Yield;
    }

    private StepResult CmdWtsv(in Statement st)
    {
        var a = new StageArgs(st);
        Wait.Timer = a.Has(0) ? unchecked((short)WaitModel.WaitFrames(a.Expr(0))) : 6;
        Wait.TimerNoClick = a.Has(1) ? (a.Expr(1) != 0 ? 1 : 0) : 1;
        WaitStarted?.Invoke("WTSV", Wait.Timer);
        return StepResult.Yield;
    }

    /// <summary>Raised when a timed wait starts: kind and milliseconds (or frames for <c>_WTSV</c>).</summary>
    public event Action<string, int>? WaitStarted;

    // -- per-frame fades (VA 0x534A0) -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The window fade and the text fade of one frame (VA 0x534A0; <c>tsuki.wait_model.Simulator._fade_update</c>): the window alpha moves +0.06 per frame
    /// while its target is open, -0.06 while it is closed and -0.2604 after <c>_CLO3</c>; the text alpha follows its step and the step ends when the alpha
    /// leaves 0..1. Both advance <paramref name="k"/> times as fast while skipping.
    /// </summary>
    private void UpdateWindowFades(int k)
    {
        float a = Wait.WindowAlpha;
        if (_windowQuick && Wait.WindowTarget == 0)
        {
            if (a > 0f)
            {
                a = (k * -0.2604166567325592f) + a;
                if (!(0f < a))
                {
                    a = 0f;
                    _windowQuick = false;
                }
            }
            else
            {
                _windowQuick = false;
            }
        }
        else
        {
            if (Wait.WindowTarget != 0 && Mode.LogShown == 0)
            {
                if (1f > a)
                {
                    a = (k * 0.06f) + a;
                    if (a > 1f)
                    {
                        a = 1f;
                    }
                }
            }
            else if (a > 0f)
            {
                a -= k * 0.06f;
                if (0f > a)
                {
                    a = 0f;
                }
            }

            _windowQuick = false;
        }

        Wait.WindowAlpha = a;
        if (Wait.TextFadeStep != 0f)
        {
            float t = (Wait.TextFadeStep * k) + _textFade;
            if (0f > t)
            {
                t = 0f;
                Wait.TextFadeStep = 0f;
            }
            else if (t > 1f)
            {
                t = 1f;
                Wait.TextFadeStep = 0f;
            }

            _textFade = t;
        }

        // dimming of the earlier messages of the page (textbox.md 5.8)
        if (_textFade >= 1f)
        {
            for (int j = 0; j < _page.Count - 1; j++)
            {
                PageMessage m = _page[j];
                if (m.DimTimer >= 0)
                {
                    m.DimTimer += k;
                    if (m.DimTimer >= m.DimLimit || Config.SeenTextAnimations == 1)
                    {
                        m.DimTimer = -1;
                    }
                }
            }
        }
    }
}
