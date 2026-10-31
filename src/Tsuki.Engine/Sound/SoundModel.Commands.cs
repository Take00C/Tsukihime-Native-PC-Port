using System.Globalization;
using System.Text;
using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Sound;

// The script commands of the sound library and the deferred-SE queue (docs/formats/sound_runtime.md 3 and 5). Each handler is a
// line-by-line port of the Python model, which the research checked against the engine's handlers (VA 0x87250, 0x87940, 0x87660,
// 0x893F0, 0x89E40, 0x89B50, 0x996E1, 0x88160, 0x87D50, 0x87AF0) on the audio statements of every script.
public sealed partial class SoundModel
{
    /// <summary>True for the command names this model runs (<c>MPLY MVOL MFAD MSTP SEPL SEFI SEFD SEVL SESP VPLY VPL2 VCLR</c>).</summary>
    public static bool Handles(CommandName name) => IsSoundName(name.ToString());

    private static bool IsSoundName(string n) =>
        n is "MPLY" or "MVOL" or "MFAD" or "MSTP" or "SEPL" or "SEFI" or "SEFD" or "SEVL" or "SESP" or "VPLY" or "VPL2" or "VCLR";

    /// <summary>
    /// Runs a sound statement of the script VM (<see cref="Statement.Args"/> starts at the separator after the four name bytes).
    /// Returns the handler's wait value: 0 to go on, 1 to wait (<c>_MFAD</c>, see <see cref="Wait16"/>).
    /// </summary>
    /// <exception cref="KeyNotFoundException">The command is not a sound command (<see cref="Handles"/>).</exception>
    public int Execute(in Statement statement) => Execute(statement.Name.ToString(), statement.Args.Span, 0);

    /// <summary>
    /// Runs a statement given as text, for example <c>_SEPL(7,1,MOON_SE03028,,`001:8000,,`017:30);</c> (latin-1 bytes; the name is
    /// the four characters after the first underscore).
    /// </summary>
    public int Run(string statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        byte[] b = Encoding.Latin1.GetBytes(statement);
        int us = Array.IndexOf(b, (byte)'_');
        if (us < 0 || us + 5 > b.Length)
        {
            throw new ArgumentException("not a statement: " + statement, nameof(statement));
        }

        return Execute(statement.Substring(us + 1, 4), b, us + 5);
    }

    private int Execute(string name, ReadOnlySpan<byte> b, int sep) => name switch
    {
        "MPLY" => CmdMply(b, sep),
        "MVOL" => CmdMvol(b, sep),
        "MFAD" => CmdMfad(b, sep),
        "MSTP" => CmdMstp(),
        "SEPL" or "SEFI" => CmdSepl(b, sep),
        "SEFD" => CmdSefd(b, sep),
        "SEVL" => CmdSevl(b, sep),
        "SESP" => CmdSesp(b, sep),
        "VPLY" => CmdVply(b, sep, immediate: false),
        "VPL2" => CmdVply(b, sep, immediate: true),
        "VCLR" => CmdVclr(),
        _ => throw new KeyNotFoundException(name),
    };

    private bool Silent => FastForward || Scan;

    /// <summary>
    /// <c>History_Push</c> (VA 0x7AF90), called by every sound handler: besides recording the SE state it copies the history ring
    /// record's bytes +0x36BC.. back into the script character array 0x1654330 (VA 0x7B3BA..0x7B3C3). No code ever writes those record
    /// bytes, so with a zeroed ring the array is cleared (sound_runtime.md 11.2).
    /// </summary>
    private void HistoryPush() => Array.Clear(_scrSeChar);

    // -- BGM commands ----------------------------------------------------------------------------

    /// <summary>VA 0x87250: <c>_MPLY(track, , `001 fade-in ms, `011 fade-out ms, `017 volume, `049 loop, `069 start sample)</c>.</summary>
    private int CmdMply(ReadOnlySpan<byte> b, int sep)
    {
        HistoryPush();
        SoundTags t = SoundTags.Parse(b, sep);
        int vol, loop, fade, t011, start;
        if (t.Count <= 0)
        {
            vol = 100;
            loop = 1;
            fade = 0;
            t011 = 0;
            start = 0;
        }
        else
        {
            int j = t.Find(SoundTags.Volume);
            vol = j >= 0 ? SoundMath.I16(SoundTags.Int(b, sep, j)) : 100;
            j = t.Find(SoundTags.Loop);
            loop = j >= 0 ? SoundMath.I16(SoundTags.Int(b, sep, j)) : 1;
            j = t.Find(SoundTags.Fade);
            fade = j >= 0 ? SoundMath.I16(SoundTags.Int(b, sep, j)) : 0;
            j = t.Find(SoundTags.Wait);
            t011 = j >= 0 ? SoundMath.I16(SoundTags.Int(b, sep, j)) : 0;
            j = t.Find(SoundTags.Start);
            start = j >= 0 ? SoundTags.Int(b, sep, j, 32) : 0;
        }

        int track = SoundMath.I16(SoundTags.Int(b, sep, 0));
        if (track < 0)
        {
            if (!FastForward && !Scan)
            {
                BgmStop();
            }

            _scrTrack = -1;
            return 0;
        }

        if (!Scan)
        {
            if (!FastForward && _track != track)
            {
                int status = _isPlaying(_slot) ? 3 : 5;
                if (fade == 0 && status == 3)
                {
                    BgmStop();
                }

                BgmSetLoop(loop);
                Emit(AudioCommand.SetStartSample(_slot ^ 1, start));
                float fv = vol;
                if (fade != 0)
                {
                    float newRate = fv * SoundMath.MsPerSecond / SoundMath.Fps / fade;
                    float old = _scrBgmVol * SoundMath.MsPerSecond / SoundMath.Fps;
                    float oldRate = old / (t011 != 0 ? t011 : fade);
                    if (!(oldRate > 0 || float.IsNaN(oldRate)))
                    {
                        oldRate = 100.0f;
                    }

                    BgmFadeOut(oldRate);
                    BgmStartFade(track, vol, newRate);
                }
                else
                {
                    BgmStart(track, fv);
                }
            }

            if ((track & 0xFFFF) <= 0x207)
            {
                int bit = track + 0xC80;
                _musicBits[bit >> 3] = _musicBits.GetValueOrDefault(bit >> 3) | (1 << (track & 7));
            }
        }

        _scrTrack = track;
        _scrBgmVol = SoundMath.I16(vol);
        return 0;
    }

    /// <summary>VA 0x87940: ramp the BGM to <c>`017</c> over <c>`001</c> ms (both default 0).</summary>
    private int CmdMvol(ReadOnlySpan<byte> b, int sep)
    {
        SoundTags t = SoundTags.Parse(b, sep);
        int j = t.Find(SoundTags.Fade);
        int ms = j >= 0 ? SoundMath.I16(SoundTags.Int(b, sep, j)) : 0;
        j = t.Find(SoundTags.Volume);
        int v = j >= 0 ? SoundMath.I16(SoundTags.Int(b, sep, j)) : 0;
        HistoryPush();
        if (!Silent)
        {
            float rate = SoundMath.Rate(Math.Abs(v - _scrBgmVol), ms >= 2 ? ms : 1);
            SoundChannelRecord r = _bgm[_slot];
            if (r.State != 0)                                         // VA 0x9F20
            {
                r.VolumeStep = rate;
                r.VolumeTarget = v;
            }
        }

        _scrBgmVol = v;
        return 0;
    }

    /// <summary>
    /// VA 0x87660: <c>_MFAD(`001 ms)</c> fades the BGM out over <c>`001</c> ms and stops it; returns the wait in 16 ms units
    /// (<see cref="Wait16"/>) unless <c>`011</c> is present and 0.
    /// </summary>
    private int CmdMfad(ReadOnlySpan<byte> b, int sep)
    {
        HistoryPush();
        SoundTags t = SoundTags.Parse(b, sep);
        int jf = t.Find(SoundTags.Fade);
        int jw = t.Find(SoundTags.Wait);
        int ms = jf >= 0 ? SoundMath.I16(SoundTags.Int(b, sep, jf)) : 0;
        if (!Silent)
        {
            if (ms > 0)
            {
                BgmFadeOut(SoundMath.Rate(_scrBgmVol, ms));
            }
            else
            {
                BgmStop();
            }
        }

        _scrTrack = -1;
        if (jw >= 0 && SoundMath.I16(SoundTags.Int(b, sep, jw)) == 0)
        {
            return 0;
        }

        Wait16 = (ms < 0 ? ms + 15 : ms) >> 4;                        // word 0x1653E08
        return 1;
    }

    /// <summary>
    /// <c>_MSTP()</c> (VA 0x978BD; no script uses it): a BGM fade-out (state 2) and track -1. The engine passes the rate in xmm0 and the
    /// Python model leaves it open [H]; knife's Witch build uses 4.1666665 points per frame (0.4 s from full volume), taken here.
    /// </summary>
    private int CmdMstp()
    {
        HistoryPush();
        if (!Silent)
        {
            BgmFadeOut(4.1666665f);
        }

        _scrTrack = -1;
        return 0;
    }

    // -- SE primitives ---------------------------------------------------------------------------

    /// <summary>VA 0xAAB0.</summary>
    private void SeStop(int ch)
    {
        if (ch > 0x13)
        {
            return;
        }

        SoundChannelRecord r = _se[ch];
        r.State = 0;
        if (r.Started != 0)
        {
            StopCall(ch + AudioStreams.FirstSe);
        }
    }

    /// <summary>VA 0xAC50: from the record's current volume to 0 over <paramref name="ms"/>.</summary>
    private void SeFadeOut(int ch, int ms)
    {
        SoundChannelRecord r = _se[ch];
        float rate = ms > 0 ? r.Volume * SoundMath.MsPerSecond / SoundMath.Fps / ms : 100.0f;
        if (r.State != 0)
        {
            r.State = 2;
            r.VolumeStep = rate;
            r.VolumeTarget = 0f;
        }
    }

    /// <summary>VA 0xAC20.</summary>
    private void SeFadeOutRate(int ch, float rate)
    {
        SoundChannelRecord r = _se[ch];
        if (r.State != 0)
        {
            r.State = 2;
            r.VolumeStep = rate;
            r.VolumeTarget = 0f;
        }
    }

    /// <summary>VA 0xACC0: ramp to <paramref name="target"/>; a rate of 100 points in one frame or more jumps.</summary>
    private void SeRamp(int ch, int target, float rate)
    {
        SoundChannelRecord r = _se[ch];
        if (r.State != 0)
        {
            r.VolumeStep = rate;
            r.VolumeTarget = target;
            if (rate >= SoundMath.SeJumpRate)
            {
                r.Volume = target;
            }
        }
    }

    private float SeGain(int ch, float vol)
    {
        SoundChannelRecord r = _se[ch];
        int c = r.Character;
        float x;
        if (c - 1 is >= 0 and <= 0x12)
        {
            if ((Config.CharEnable & (1u << (c - 1))) == 0)
            {
                return 0f;
            }

            x = vol / 100.0f * SoundMath.I16(Config.CharVol[c - 1]);
            x *= SoundMath.I16(Config.Voice);
            x /= 100.0f;
        }
        else
        {
            x = SoundMath.I8(_vol[0xE]) * vol / 100.0f;
        }

        x = SoundMath.MaxSs(0f, x);
        return SoundMath.MinSs(100.0f, x);
    }

    /// <summary>VA 0xA300: start vse <paramref name="entry"/> on channel <paramref name="ch"/> at <paramref name="vol"/> (0..100).</summary>
    private void SePlayNow(int ch, int entry, float vol)
    {
        float g = SeGain(ch, vol);
        int s = ch + AudioStreams.FirstSe;
        Emit(AudioCommand.OpenHandle(s, AudioBank.Se));
        float gv = g * 0.01f;
        Emit(AudioCommand.SetVolume0(s, gv));
        PlayCall(s, AudioBank.Se, entry, _seLoop[ch] != 0 ? 1 : 0, gv);
        SoundChannelRecord r = _se[ch];
        r.State = 1;
        r.VolumeClass = 1;
        r.Entry = unchecked((ushort)entry);
        r.Volume = vol;
        r.VolumeTarget = vol;
        r.VolumeStep = 100.0f;
        ClearPans(r);
        r.Started = 1;
    }

    /// <summary>VA 0xA770: start at volume 0 (the ramp is set up by the caller).</summary>
    private void SePlayFade(int ch, int entry, float rate, int target)
    {
        int s = ch + AudioStreams.FirstSe;
        Emit(AudioCommand.OpenHandle(s, AudioBank.Se));
        Emit(AudioCommand.SetVolume(s, 0f, 0f));
        PlayCall(s, AudioBank.Se, entry, _seLoop[ch] != 0 ? 1 : 0, 0f);
        SoundChannelRecord r = _se[ch];
        r.State = 1;
        r.VolumeClass = 1;
        r.Entry = unchecked((ushort)entry);
        r.Volume = 0f;
        r.VolumeStep = rate;
        r.VolumeTarget = target;
        ClearPans(r);
        r.Started = 1;
    }

    /// <summary>VA 0xA9E0: an immediate pan (only on an SE-kind record).</summary>
    private void SeSetPan(int ch, int pan)
    {
        if (ch > 0x13)
        {
            return;
        }

        SoundChannelRecord r = _se[ch];
        if (r.VolumeClass != 1)
        {
            return;
        }

        ClearPans(r);
        Emit(AudioCommand.SetPan(ch + AudioStreams.FirstSe, pan * 0.001f));
    }

    private static int Channel(ReadOnlySpan<byte> b, int sep) => SoundMath.I16(SoundMath.I16(SoundTags.Int(b, sep, 0)) % 20);

    private static IEnumerable<int> ChannelRange(int ch)
    {
        int lo = ch > 0 ? ch : 0;
        int hi = ch >= 0 ? ch : 0x13;
        for (int c = lo; c <= hi; c++)
        {
            yield return c;
        }
    }

    /// <summary>The index Python gives a possibly negative channel (a negative list index counts from the end).</summary>
    private static int Wrap(int ch) => ch < 0 ? ch + SeChannels : ch;

    // -- SE commands -----------------------------------------------------------------------------

    /// <summary>VA 0x893F0: <c>_SEPL(ch, loop, name, , `001 fade, `017 volume, `018 pan, `068 delay, `070 character)</c> (also <c>_SEFI</c>).</summary>
    private int CmdSepl(ReadOnlySpan<byte> b, int sep)
    {
        SoundTags t = SoundTags.Parse(b, sep);
        if (t.Count > 0)
        {
            int jd = t.Find(SoundTags.Delay);
            if (jd > 0 && !FastForward && !Scan)
            {
                DeferCommand(b, sep, 1);
                return 0;
            }
        }

        string name = SoundTags.ArgText(b, sep, 2);
        int ch = Wrap(Channel(b, sep));
        _defer[ch].Timer = 0;
        int vol = 100, rawVol = -1, pan = 0, fade = 0;
        if (t.Count > 0)
        {
            int j = t.Find(SoundTags.Fade);
            if (j >= 0)
            {
                int v = SoundMath.I16(SoundTags.Int(b, sep, j));
                fade = _se[ch].State == 2 ? v + 1000 : v;
            }

            j = t.Find(SoundTags.Volume);
            if (j >= 0)
            {
                rawVol = SoundMath.I16(SoundTags.Int(b, sep, j));
                vol = Math.Min(Math.Max(rawVol, 0), 100);
            }

            j = t.Find(SoundTags.Pan);
            if (j >= 0)
            {
                pan = SoundMath.PanFromTag(SoundMath.I16(SoundTags.Int(b, sep, j)));
            }
        }

        if (name.Length == 0)
        {
            HistoryPush();
            if (!Silent)
            {
                SeStop(ch);
            }

            _scrSeLoopId[ch] = -1;
            return 0;
        }

        int loopArg = SoundTags.Present(b, sep, 1) ? SoundTags.Int(b, sep, 1) : 0;
        int bx = SoundMath.I16(loopArg) != 0 ? 1 : 0;
        int flags = rawVol < 0 ? (2 * bx) + 1 : 3;
        int seId = SeLookup(name, flags) ?? -1;
        _scrSeId[ch] = SoundMath.I16(seId);
        int r14 = SoundMath.I16(SoundTags.Int(b, sep, 1));
        if (r14 == 1)
        {
            HistoryPush();
            if (_scrSeLoopId[ch] == SoundMath.I16(seId) && _scrSeLoop[ch] == 1 && _scrSeVol[ch] > 0)
            {
                return 0;
            }

            _scrSeLoopId[ch] = SoundMath.I16(seId);
            _scrSeLoop[ch] = 1;
        }
        else
        {
            _scrSeLoop[ch] = 0;
            if (Skip)
            {
                if (_scrSeLoopId[ch] >= 0)
                {
                    SeStop(ch);
                }

                _scrSeLoopId[ch] = -1;
                return 0;
            }

            _scrSeLoopId[ch] = -1;
        }

        int loop = bx != 0 ? r14 : 0;
        _seLoop[ch] = loop;
        int jc = t.Find(SoundTags.Character);
        int chr = jc >= 0 ? SoundMath.I8(SoundTags.Int(b, sep, jc)) : 0;
        _scrSeChar[ch] = chr & 0xFF;
        _se[ch].Character = (byte)(chr & 0xFF);
        if (!Silent)
        {
            if (fade >= 0x11)
            {
                SePlayFade(ch, seId, 2000.0f, 0);
                SeRamp(ch, vol, SoundMath.Rate(vol, fade));
            }
            else
            {
                SePlayNow(ch, seId, vol);
            }

            SeSetPan(ch, pan);
            _scrSePan[ch] = pan;
            if (Config.SeCaption != 0)
            {
                SeStarted?.Invoke(ch, seId);
            }
        }
        else
        {
            _scrSePan[ch] = pan;
        }

        _scrSeVol[ch] = rawVol >= 0 ? rawVol : 100;
        return 0;
    }

    /// <summary>VA 0x89E40: <c>_SEFD(ch, , `001 ms)</c> fades channel ch (or all for a negative ch) out over `001 ms.</summary>
    private int CmdSefd(ReadOnlySpan<byte> b, int sep)
    {
        SoundTags t = SoundTags.Parse(b, sep);
        if (t.Count > 0)
        {
            int jd = t.Find(SoundTags.Delay);
            if (jd > 0 && !FastForward && !Scan)
            {
                DeferCommand(b, sep, 2);
                return 0;
            }
        }

        int j = t.Find(SoundTags.Fade);
        int ms = j >= 0 ? SoundMath.I16(SoundTags.Int(b, sep, j)) : 0;
        int ch = Channel(b, sep);
        HistoryPush();
        foreach (int c in ChannelRange(ch))
        {
            DeferredSe d = _defer[c];
            if (d.Kind == 1 && d.Timer > 0)                           // a pending deferred _SEPL is cancelled
            {
                d.Timer = 0;
                d.Kind = 0;
            }

            if (!Silent)
            {
                if (ms > 0)
                {
                    SeFadeOut(c, ms);
                }
                else
                {
                    SeStop(c);
                }
            }

            _scrSeLoopId[c] = -1;
        }

        return 0;
    }

    /// <summary>VA 0x89B50: <c>_SEVL(ch, , `001 ms, `017 volume)</c> ramps channel ch to `017 over `001 ms (from the script volume).</summary>
    private int CmdSevl(ReadOnlySpan<byte> b, int sep)
    {
        SoundTags t = SoundTags.Parse(b, sep);
        if (t.Count > 0)
        {
            int jd = t.Find(SoundTags.Delay);
            if (jd > 0 && !FastForward && !Scan)
            {
                DeferCommand(b, sep, 3);
                return 0;
            }
        }

        int j = t.Find(SoundTags.Fade);
        int ms = j >= 0 ? SoundMath.I16(SoundTags.Int(b, sep, j)) : 0;
        j = t.Find(SoundTags.Volume);
        int v = j >= 0 ? SoundMath.I16(SoundTags.Int(b, sep, j)) : 0;
        HistoryPush();
        if (!Silent)
        {
            int ch = Wrap(Channel(b, sep));
            float rate = ms > 0 ? SoundMath.Rate(Math.Abs(v - _scrSeVol[ch]), ms) : 2000.0f;
            SeRamp(ch, v, rate);
            _scrSeVol[ch] = v;
        }

        return 0;
    }

    /// <summary>VA 0x996E1: <c>_SESP(ch)</c> stops channel ch (or all) and cancels its deferred command.</summary>
    private int CmdSesp(ReadOnlySpan<byte> b, int sep)
    {
        int ch = SoundMath.I16(SoundMath.I16(SoundTags.Int(b, sep, 0)) % 20);
        HistoryPush();
        foreach (int c in ChannelRange(ch))
        {
            if (!FastForward)
            {
                SeStop(c);
            }

            _defer[c].Clear();
            _defer[c].Fade = -1;
            _scrSeLoopId[c] = -1;
            _scrSeId[c] = -1;
        }

        return 0;
    }

    // -- deferred (`068) commands ----------------------------------------------------------------

    /// <summary>
    /// VA 0x88160: queue SEPL (1) / SEFD (2) / SEVL (3) for channel ch (all channels for a negative SEFD channel); a pending command of
    /// the channel is executed first. Timer = max(1, trunc(delay ms * 3 / 50)) frames.
    /// </summary>
    private void DeferCommand(ReadOnlySpan<byte> b, int sep, int kind)
    {
        SoundTags t = SoundTags.Parse(b, sep);
        int jd = t.Find(SoundTags.Delay);
        int delay = SoundMath.I16(SoundTags.Int(b, sep, jd));
        int timer = (delay * 3) + 0x31 >= 0x63 ? (delay * 3) / 50 : 1;
        HistoryPush();
        int ch = SoundMath.I16(SoundTags.Int(b, sep, 0)) % 20;
        int lo = ch > 0 ? ch : 0;
        int hi = ch >= 0 ? ch : 0x13;
        if (hi < lo)
        {
            return;
        }

        int seId = -1;
        if (kind == 1)
        {
            string name = SoundTags.ArgText(b, sep, 2);
            int loopArg = SoundTags.Present(b, sep, 1) ? SoundTags.Int(b, sep, 1) : 0;
            int jv = t.Find(SoundTags.Volume);
            int flags = jv >= 0 ? 3 : (2 * (SoundMath.I16(loopArg) != 0 ? 1 : 0)) + 1;
            seId = name.Length > 0 ? SeLookup(name, flags) ?? -1 : -1;
        }

        for (int c = lo; c <= hi; c++)
        {
            if (_defer[c].Kind != 0)
            {
                RunDeferred(c);
            }

            var d = new DeferredSe { Timer = timer, Kind = kind, SeId = -1, Fade = -1, Vol = 0xFF, Vol2 = 0xFF, Pan = 0xFF, Pan2 = 0xFF, Loop = 0xFF, Char = 0 };
            int j = t.Find(SoundTags.Fade);
            if (j >= 0)
            {
                d.Fade = SoundMath.I16(SoundTags.Int(b, sep, j));
            }

            if (kind == 1)
            {
                d.SeId = seId;
                j = t.Find(SoundTags.Volume);
                if (j >= 0)
                {
                    d.Vol = SoundTags.Int(b, sep, j) & 0xFF;
                }

                j = t.Find(SoundTags.Pan);
                if (j >= 0)
                {
                    d.Pan = SoundTags.Int(b, sep, j) & 0xFF;
                }

                d.Loop = SoundTags.Present(b, sep, 1) ? SoundTags.Int(b, sep, 1) & 0xFF : 0;
                j = t.Find(SoundTags.Character);
                d.Char = j >= 0 ? SoundTags.Int(b, sep, j) & 0xFF : 0;
            }
            else if (kind == 3)
            {
                j = t.Find(SoundTags.Volume);
                if (j >= 0)
                {
                    d.Vol = SoundTags.Int(b, sep, j) & 0xFF;
                }
            }

            _defer[c] = d;
        }
    }

    /// <summary>VA 0x87D50: executes the channel's deferred command and clears the entry.</summary>
    private void RunDeferred(int ch)
    {
        DeferredSe d = _defer[ch];
        switch (d.Kind)
        {
            case 1:
                DeferredPlay(ch);
                break;
            case 2:
                if (!Silent)
                {
                    int fade = unchecked((int)d.Fade);
                    if (fade > 0 && _scrSeVol[ch] > 0)
                    {
                        SeFadeOutRate(ch, SoundMath.Rate(_scrSeVol[ch], fade));
                    }
                    else
                    {
                        SeStop(ch);
                    }
                }

                _scrSeLoopId[ch] = -1;
                break;
            case 3:
                if (!Silent && SoundMath.I8(d.Vol) >= 0)
                {
                    int tgt = SoundMath.I8(d.Vol);
                    int fade = unchecked((int)d.Fade);
                    fade = fade >= 2 ? fade : 1;
                    int cur = _scrSeVol[ch];
                    cur = cur < 0 ? 100 : Math.Min(cur, 100);
                    SeRamp(ch, tgt, SoundMath.Rate(Math.Abs(tgt - cur), fade));
                    _scrSeVol[ch] = tgt & 0xFF;
                }

                break;
            case 4:
                throw new NotSupportedException("deferred kind 4 (_SEAC) is not used by the scripts");
        }

        _defer[ch].Clear();
    }

    /// <summary>VA 0x87AF0.</summary>
    private void DeferredPlay(int ch)
    {
        DeferredSe d = _defer[ch];
        long fadeRaw = d.Fade;
        int fade;
        if (fadeRaw > 0)
        {
            fade = _se[ch].State == 2 ? unchecked((int)fadeRaw) + 1000 : unchecked((int)fadeRaw);
        }
        else
        {
            fade = 0;
        }

        _scrSeId[ch] = SoundMath.I16(d.SeId);
        if (d.Loop == 1)
        {
            _scrSeLoopId[ch] = SoundMath.I16(d.SeId);
            _scrSeLoop[ch] = 1;
            _seLoop[ch] = 1;
        }
        else
        {
            _scrSeLoop[ch] = 0;
            if (Skip)
            {
                if (_scrSeLoopId[ch] >= 0)
                {
                    SeStop(ch);
                }

                _scrSeLoopId[ch] = -1;
                return;
            }

            _scrSeLoopId[ch] = -1;
            _seLoop[ch] = 0;
        }

        _se[ch].Character = (byte)d.Char;
        _scrSeChar[ch] = d.Char;
        if (Silent)
        {
            return;
        }

        if (d.Kind == 4)
        {
            fade = 0;
        }

        _scrSeVol[ch] = 100;
        int vol = 100;
        if (SoundMath.I8(d.Vol) >= 0)
        {
            vol = d.Vol;
            _scrSeVol[ch] = vol;
        }

        if (fade >= 0x11)
        {
            SePlayNow(ch, SoundMath.I16(d.SeId), 0f);
            SeRamp(ch, vol, SoundMath.Rate(vol, fade));
        }
        else
        {
            SePlayNow(ch, SoundMath.I16(d.SeId), vol);
        }

        int pan = d.Pan != 0xFF ? SoundMath.PanFromTag(Math.Abs(SoundMath.I8(d.Pan))) : 0;
        SeSetPan(ch, pan);
        _scrSePan[ch] = pan;
        if (Config.SeCaption != 0)
        {
            SeStarted?.Invoke(ch, SoundMath.I16(d.SeId));
        }
    }

    internal int ScriptSeIdAt(int ch) => _scrSeId[ch];

    internal int ScriptSeLoopIdAt(int ch) => _scrSeLoopId[ch];

    internal int ScriptSeVolAt(int ch) => _scrSeVol[ch];

    internal int ScriptSeLoopAt(int ch) => _scrSeLoop[ch];

    internal int ScriptSePanAt(int ch) => _scrSePan[ch];

    internal int ScriptSeCharAt(int ch) => _scrSeChar[ch];

    private static string Fmt(int v) => v.ToString(CultureInfo.InvariantCulture);
}
