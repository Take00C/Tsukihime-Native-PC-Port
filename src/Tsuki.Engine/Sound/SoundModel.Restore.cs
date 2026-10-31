using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Sound;

// The sound side of a restore (package K6): after a load, a rewind, a previous-scene or a backlog jump the engine restarts the music and the looping
// effects from the script-side sound variables of the restored record (the resync 0x7E4E0 / 0x7E620 of docs/formats/adv_state.md 3.1, 5.1; K4.md
// "Where each AdvLiveState member comes from"). A fresh model is created for the restored scene; this fills its script-side state and starts the streams.
// A rewind onto a record with the BGM that already plays keeps that BGM (AdoptBgm; knife game.rs restore_with), and the end of a next-scene skip resyncs
// the live model's streams with the script-side state the skipped handlers left (ResyncScriptState, the 0x7E620 of every skip end site).
public sealed partial class SoundModel
{
    /// <summary>
    /// Restarts the script-side sound state of a restored record on this (fresh) model: the BGM <paramref name="bgmTrack"/> at
    /// <paramref name="bgmVolume"/> (negative track: none) and every loop-SE channel whose loop id is not negative and whose loop flag is above 0
    /// at its volume and pan. The play commands go to <see cref="TakeCommands()"/> like the script's own. A BGM stream that already plays the track
    /// (<see cref="AdoptBgm"/>) is left alone, as BgmStart's own early-out (VA 0x98C0) leaves it on the console.
    /// </summary>
    /// <param name="bgmTrack">The record's BGM track (RAM 0x16545D0).</param>
    /// <param name="bgmVolume">The record's BGM volume word (0x1654238).</param>
    /// <param name="loopSeIds">Loop SE entry per channel (0x1654240), -1 = none.</param>
    /// <param name="volume">Volume per channel (0x1654270).</param>
    /// <param name="looped">Loop flag per channel (0x16542A0).</param>
    /// <param name="pan">Pan per channel (0x16542D0, thousandths).</param>
    /// <param name="character">Voice character per channel (0x1654330).</param>
    public void RestoreScriptState(
        int bgmTrack, int bgmVolume, ReadOnlySpan<short> loopSeIds, ReadOnlySpan<ushort> volume, ReadOnlySpan<ushort> looped,
        ReadOnlySpan<short> pan, ReadOnlySpan<byte> character)
    {
        // a fresh model reports track 0 at volume 0 for "no music yet"; that is silence, not track 0
        if (bgmTrack > 0 || (bgmTrack == 0 && bgmVolume > 0))
        {
            if (_track != bgmTrack)
            {
                BgmSetLoop(1);
                BgmStart(bgmTrack, bgmVolume);
            }

            _scrTrack = bgmTrack;
            _scrBgmVol = SoundMath.I16(bgmVolume);
        }
        else
        {
            _scrTrack = -1;
        }

        for (int ch = 0; ch < SeChannels && ch < loopSeIds.Length; ch++)
        {
            int id = loopSeIds[ch];
            if (id < 0 || looped[ch] == 0)
            {
                continue;
            }

            int vol = Math.Clamp((int)(short)volume[ch], 0, 100);
            _scrSeId[ch] = id;
            _scrSeLoopId[ch] = id;
            _scrSeLoop[ch] = 1;
            _scrSeVol[ch] = volume[ch];
            _scrSePan[ch] = pan[ch];
            _scrSeChar[ch] = character[ch];
            _se[ch].Character = character[ch];
            _seLoop[ch] = 1;
            SePlayNow(ch, id, vol);
            SeSetPan(ch, pan[ch]);
        }
    }

    /// <summary>
    /// Takes the BGM side of <paramref name="previous"/> over into this fresh model: the two slot records, the loop flags, the current slot and track, the
    /// ducking level. A rewind onto a record whose track already plays uses it (knife game.rs restore_with: sub_5D710 keeps the BGM when it is the same
    /// track and stops only the effects and the voice); <see cref="RestoreScriptState"/> then leaves the stream alone.
    /// </summary>
    public void AdoptBgm(SoundModel previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        for (int s = 0; s < BgmSlots; s++)
        {
            _bgm[s].CopyFrom(previous._bgm[s]);
        }

        _bgmLoop[0] = previous._bgmLoop[0];
        _bgmLoop[1] = previous._bgmLoop[1];
        _slot = previous._slot;
        _track = previous._track;
        _duckLevel = previous._duckLevel;
        _duckApplied = previous._duckApplied;
    }

    /// <summary>
    /// The resync at the end of a next-scene skip (VA 0x7E620, "restart BGM 0x16545D0 and the loop SEs", adv_state.md 2.5, 5.2; every end site calls it).
    /// While the skip ran (word 0x120A6D2) the handlers only updated the script-side state. The eboot (tools/eboot/disasm.py func 0x7E620): a script track
    /// at or above 0 gives BgmSetLoop(1), BgmStart(track, volume) (the early-out 0x98C0 keeps a stream that plays the track) and the volume 0x9CE0, a negative
    /// one BgmStop; then every SE channel without a script-side loop (0x1654240 below 0 or 0x16542A0 at most 0) is stopped (0xAAB0) and every channel with
    /// one is started again (loop flag 1, its character, 0xA300 at the script volume, 0xA9E0 at the script pan; 0x1654210 = the loop entry).
    /// </summary>
    public void ResyncScriptState()
    {
        if (_scrTrack > 0 || (_scrTrack == 0 && _scrBgmVol > 0))              // a fresh model's track 0 at volume 0 is "no music yet" (RestoreScriptState)
        {
            BgmSetLoop(1);
            BgmStart(_scrTrack, _scrBgmVol);
            BgmSetLoop(1);
            BgmSetVolume(_scrBgmVol);
        }
        else if (_track >= 0)
        {
            BgmStop();
        }

        for (int ch = 0; ch < SeChannels; ch++)
        {
            if (_scrSeLoopId[ch] < 0 || _scrSeLoop[ch] <= 0)
            {
                SeStop(ch);
                continue;
            }

            _seLoop[ch] = 1;
            _se[ch].Character = (byte)(_scrSeChar[ch] & 0xFF);
            SePlayNow(ch, _scrSeLoopId[ch], SoundMath.I16(_scrSeVol[ch]));
            SeSetPan(ch, _scrSePan[ch]);
            _scrSeId[ch] = _scrSeLoopId[ch];
        }
    }

    /// <summary>VA 0x9CE0: the current slot's volume and target become <paramref name="vol"/> at once (step 100), and the stream gets <c>vol * BGM byte / 100</c>.</summary>
    private void BgmSetVolume(int vol)
    {
        SoundChannelRecord r = _bgm[_slot];
        r.Volume = vol;
        r.VolumeTarget = vol;
        r.VolumeStep = 100f;
        int g = Math.Clamp(SoundMath.Trunc((float)vol * SoundMath.I8(_vol[0xC]) * 0.01f), 0, 100);
        SetVolumeCall(_slot, g * 0.01f);
    }

    /// <summary>
    /// Makes <paramref name="entry"/> the pending voice of <paramref name="character"/> (what <c>_VPLY</c> does): the next message display plays it
    /// through the usual gate. A load uses it to replay the voice of the restored line (VA 0x7EB19).
    /// </summary>
    public void SetPendingVoice(int entry, int character)
    {
        _voicePending = entry;
        _voiceChar = character;
    }

    /// <summary>
    /// The commands that stop every decoder stream from <paramref name="fromStream"/> on (0: BGM, voices and script effects, the way a script end does;
    /// <see cref="AudioStreams.FirstVoice"/>: everything but the BGM, a rewind that keeps its music); for the host's restore.
    /// </summary>
    public static AudioCommand[] StopEverything(int fromStream = 0)
    {
        var list = new List<AudioCommand>(AudioStreams.Count * 2);
        for (int s = Math.Max(0, fromStream); s < AudioStreams.Count; s++)
        {
            list.Add(AudioCommand.Stop(s));
            list.Add(AudioCommand.Close(s));
        }

        return [.. list];
    }
}
