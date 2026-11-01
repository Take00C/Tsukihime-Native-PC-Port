using Tsuki.Engine.Contracts;
using Tsuki.Engine.Sound;

namespace Tsuki.Engine.Game;

/// <summary>
/// A stand-in for the audio layer's <c>is_playing</c> when no mixer runs (headless runs, the explorer): it follows the sound model's
/// commands and lets a stream end after the frames of its clip. A looping stream and a BGM never end by themselves.
/// </summary>
public sealed class VirtualAudio
{
    private readonly int[] _left = new int[AudioStreams.Count];

    /// <summary>The commands seen since the last <see cref="Reset"/> (for tests and traces).</summary>
    public long Commands { get; private set; }

    /// <summary>Plays started.</summary>
    public long Plays { get; private set; }

    /// <summary>Forgets every stream from <paramref name="fromStream"/> on (0: all; <see cref="AudioStreams.FirstVoice"/>: all but the BGM slots).</summary>
    public void Reset(int fromStream = 0) => Array.Clear(_left, fromStream, _left.Length - fromStream);

    /// <summary>True while the stream plays.</summary>
    public bool IsPlaying(int stream) => (uint)stream < (uint)_left.Length && _left[stream] != 0;

    /// <summary>Applies the commands of one frame.</summary>
    public void Process(AudioCommand[] commands, Func<AudioBank, int, int>? clipFrames, int defaultVoiceFrames)
    {
        foreach (AudioCommand c in commands)
        {
            Commands++;
            if ((uint)c.Stream >= (uint)_left.Length)
            {
                continue;
            }

            switch (c.Op)
            {
                case AudioOp.Play:
                {
                    Plays++;
                    bool loop = c.Arg0 != 0 || c.Stream < AudioStreams.FirstVoice;
                    int frames = clipFrames?.Invoke(c.Bank, c.Entry) ?? (c.Stream is >= AudioStreams.FirstVoice and < AudioStreams.FirstSe ? defaultVoiceFrames : 30);
                    _left[c.Stream] = loop ? int.MaxValue : Math.Max(1, frames);
                    break;
                }

                case AudioOp.Stop:
                case AudioOp.Close:
                    _left[c.Stream] = 0;
                    break;
            }
        }
    }

    /// <summary>Advances every finite stream by one frame.</summary>
    public void Tick()
    {
        for (int i = 0; i < _left.Length; i++)
        {
            if (_left[i] is > 0 and < int.MaxValue)
            {
                _left[i]--;
            }
        }
    }
}

public sealed partial class GameHost
{
    private readonly VirtualAudio _audio = new();
    private readonly List<AudioCommand> _audioOut = [];
    private Func<int, bool>? _externalPlaying;
    private IReadOnlyList<int>? _voiceRowWords;
    private bool _voiceRowWordsRead;

    /// <summary>
    /// The eboot's row word table (text VA 0x1A6850, i32[19]: config voice row to the G word whose bit 4 shows it); null for a data source without
    /// the UI tables (a test rig), which then records the heard bit and the last voice but no row bit.
    /// </summary>
    public IReadOnlyList<int>? VoiceRowWords
    {
        get
        {
            if (!_voiceRowWordsRead)
            {
                _voiceRowWordsRead = true;
                try
                {
                    _voiceRowWords = _data.Eboot.Ui.VoiceRowWords;
                }
                catch (NotSupportedException)
                {
                    _voiceRowWords = null;
                }
            }

            return _voiceRowWords;
        }
    }

    /// <summary>
    /// Uses the real audio layer's <c>is_playing</c> (<c>AudioCommandPlayer.IsPlaying</c>) instead of the virtual clock; the caller then also performs
    /// the commands of <see cref="TakeAudioCommands"/>.
    /// </summary>
    public void UseExternalAudio(Func<int, bool> isPlaying)
    {
        _externalPlaying = isPlaying ?? throw new ArgumentNullException(nameof(isPlaying));
        Sound.IsPlaying = isPlaying;
    }

    /// <summary>Removes and returns the sound commands produced since the last call (for the mixer).</summary>
    public AudioCommand[] TakeAudioCommands()
    {
        AudioCommand[] a = [.. _audioOut];
        _audioOut.Clear();
        return a;
    }

    private StepResult DispatchSound(in Statement st)
    {
        string name = st.Name.ToString();
        bool music = name is "MPLY" or "MFAD" or "MVOL" or "MSTP";
        bool effect = name is "SEPL" or "SEFI" or "SEFD" or "SEVL" or "SESP";
        if (name is "VPLY" or "VPL2")
        {
            CommitLayerChange(st);
        }

        if (music)
        {
            Push(HistoryKind.Music, st);
        }
        else if (effect)
        {
            Push(HistoryKind.SoundEffect, st);
        }

        int wait = Sound.Execute(st);
        Wait.VoiceState = Sound.VoiceState;
        if (name is "VPLY" or "VPL2" && !VarState.ScanMode)
        {
            // the handler's character marks (VA 0x99F08, 0x9A058 -> 0x3D260; package K36): the heard bit, the slot's last voice and the row bit the
            // Sound tab shows the character's row by; written whenever the statement runs outside scan mode, whether or not the voice sounds
            VoiceMarks.Note(Vars, VoiceRowWords, Sound.PendingVoiceCharacter, Sound.VplyEntry);
        }

        if (name == "MPLY")
        {
            int track = Sound.ScriptTrack;
            if (track >= 0 && track < SystemBlock.BgmHeardTracks)
            {
                SystemData.SetBgmHeard(track, true);
            }
        }

        if (name == "MFAD" && wait == 1 && Sound.Wait16 > 0)
        {
            // VA 0x978F5: the wait of the fade goes into word 0x1653E08 (the timer); its click flag (E18) stays as the last wait left it
            Wait.Timer = unchecked((short)Sound.Wait16);
            return StepResult.Yield;
        }

        return StepResult.Continue;
    }

    private StepResult CmdVcwt()
    {
        if (Sound.VoiceState != 0)
        {
            Wait.Bits |= WaitBits.Voice;
            Wait.VoiceState = Sound.VoiceState;
            return StepResult.Yield;
        }

        return StepResult.Continue;
    }
}
