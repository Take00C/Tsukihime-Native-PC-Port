using Tsuki.Engine.Contracts;
using Tsuki.Engine.Flow;
using Tsuki.Engine.Movie;
using Tsuki.Engine.Save;
using Tsuki.Engine.Sound;
using Tsuki.Engine.Stage;
using Tsuki.Engine.Text;

namespace Tsuki.Engine.Game;

/// <summary>What kind of restore ran (<see cref="GameHost.Restored"/>).</summary>
public enum RestoreKind : byte
{
    /// <summary>A save slot was loaded.</summary>
    Load,

    /// <summary>One rewind step.</summary>
    Rewind,

    /// <summary>The previous-scene action.</summary>
    PreviousScene,

    /// <summary>A jump to a backlog entry.</summary>
    BacklogJump,
}

/// <summary>What a restore did (for the services, the tests and the log).</summary>
/// <param name="Kind">Which action ran.</param>
/// <param name="Script">The script the VM resumes in.</param>
/// <param name="Offset">The offset it resumes at.</param>
/// <param name="RecordKind">The kind of the restored record (adv_state.md 2.3).</param>
/// <param name="Redisplayed">The line was laid out again from the record (a rewind), the script resumes after its statement.</param>
/// <param name="BgmTrack">The BGM track restarted, or -1.</param>
/// <param name="LoopSeChannels">Number of loop-SE channels restarted.</param>
public sealed record RestoreReport(RestoreKind Kind, int Script, int Offset, int RecordKind, bool Redisplayed, int BgmTrack, int LoopSeChannels);

// The glue K3 and K4 left (docs/status/knife/K3.md "What is left", K4.md "Open issues 1"): the history ring restores variables and tells what else to do
// (RestoreOutcome); this file does it. A restore (load, rewind, previous scene, backlog jump) resets the runtime like a scene start, rebuilds the
// stage from the restored scene state, restarts the music and the looping effects from the restored sound variables, seeks the VM, and for a rewind
// lays the saved line out again. Everything the three other kinds restore runs the saved command again (the VM resumes at the record's CmdOffset).
public sealed partial class GameHost
{
    private int _restores;

    /// <summary>The history sink (ring, live state, save store), or null when <see cref="AttachHistory"/> was not called.</summary>
    public AdvHistorySink? HistorySink => Sink as AdvHistorySink;

    /// <summary>The history ring, or null.</summary>
    public AdvHistory? History => HistorySink?.History;

    /// <summary>Restores performed so far.</summary>
    public int RestoreCount => _restores;

    /// <summary>The last restore, or null.</summary>
    public RestoreReport? LastRestore { get; private set; }

    /// <summary>Raised after a restore has been applied.</summary>
    public event Action<RestoreReport>? Restored;

    private ScriptBytesProvider ScriptBytes => script => _services.Scripts.Script(script).Bytes;

    private ushort BlockingEffectBits => (ushort)(MovieActive ? 1 : 0);

    /// <summary>True when a rewind step has somewhere to go.</summary>
    public bool CanRewind => Phase == GamePhase.Running && HistorySink is not null && History!.CanRewind;

    /// <summary>True when the previous-scene action would restore something now.</summary>
    public bool CanGoToPreviousScene => Phase == GamePhase.Running && HistorySink is not null && History!.CanGoToPreviousScene(BlockingEffectBits, ChoiceOpen);

    /// <summary>
    /// Starts a new playthrough at <paramref name="sceneId"/> with an empty history ring (the PS4 clears the ring at a script reset, VA 0x7AF10).
    /// </summary>
    public void BeginStory(int sceneId, string? label = null)
    {
        if (HistorySink is { } h)
        {
            h.History.Clear(h.Live);
            h.Live.ResetSound();
        }

        NewGame(sceneId, label);
        StopEverythingOutside();
    }

    /// <summary>Starts a gallery replay with a clean history (see <see cref="StartReplay"/>).</summary>
    public bool BeginReplay(int sceneId)
    {
        if (HistorySink is { } h)
        {
            h.History.Clear(h.Live);
            h.Live.ResetSound();
        }

        bool started = StartReplay(sceneId);
        StopEverythingOutside();
        return started;
    }

    /// <summary>
    /// The flowchart jump of the in-game chart with a clean history (see <see cref="JumpToRecord"/>): the playthrough is wound back and the node's scene
    /// starts from its top.
    /// </summary>
    public bool BeginFlowchartJump(int column, int node, int sceneId)
    {
        if (!JumpToRecord(column, node, sceneId))
        {
            return false;
        }

        if (HistorySink is { } h)
        {
            h.History.Clear(h.Live);
            h.Live.ResetSound();
        }

        StopEverythingOutside();
        return true;
    }

    /// <summary>
    /// Tells the audio layer and the movie host to stop whatever the previous scene left playing (the new runtime starts silent); from
    /// <paramref name="fromStream"/> on, so that a rewind that keeps its BGM stops only the voices and the effects.
    /// </summary>
    private void StopEverythingOutside(int fromStream = 0)
    {
        _audioOut.AddRange(SoundModel.StopEverything(fromStream));
        _movieOut.Add(MovieCommand.Stop(MovieSlots.FullScreen));
    }

    // -- the four actions ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One step of the held rewind action (Adv_Rewind): back to the previous page start, every recorded command in between undone, the line laid out
    /// again, the script resuming after its statement. False when there is nothing to rewind to.
    /// </summary>
    public bool RewindStep()
    {
        if (!CanRewind)
        {
            return false;
        }

        AdvHistorySink h = HistorySink!;
        RewindResult r = h.History.RewindStep(h.Live, ScriptBytes);
        if (!r.Stepped || r.Restores.Count == 0)
        {
            return false;
        }

        RestoreOutcome last = r.Restores[^1];
        ApplyRestore(RestoreKind.Rewind, last, h.Live, last.Redisplay, r.StopEffectSlots);
        return true;
    }

    /// <summary>The previous-scene action after its confirmation (adv_state.md 5.3). False when there is no scene to go back to.</summary>
    public bool PreviousScene()
    {
        if (Phase != GamePhase.Running || HistorySink is not { } h)
        {
            return false;
        }

        SceneRestoreResult? r = h.History.PreviousScene(h.Live, BlockingEffectBits, ChoiceOpen, out _);
        if (r is null)
        {
            return false;
        }

        ApplyRestore(RestoreKind.PreviousScene, r.Restore, h.Live, null, 0);
        ReplayVoiceOf(r);
        return true;
    }

    /// <summary>Jumps to a backlog entry (a ring index of <see cref="AdvHistory.BacklogEntries"/>): the entry's command runs again. False for an invalid entry.</summary>
    public bool BacklogJump(int ringIndex)
    {
        if (Phase != GamePhase.Running || HistorySink is not { } h)
        {
            return false;
        }

        RestoreOutcome? o = h.History.BacklogJump(ringIndex, h.Live);
        if (o is null)
        {
            return false;
        }

        ApplyRestore(RestoreKind.BacklogJump, o, h.Live, null, 0);
        return true;
    }

    /// <summary>Loads save slot <paramref name="slot"/> of <paramref name="store"/> (Slot_Load): the saved page's statement runs again. Throws for an empty or damaged slot.</summary>
    public void LoadSlot(SaveStore store, int slot)
    {
        ArgumentNullException.ThrowIfNull(store);
        AdvHistorySink h = HistorySink ?? throw new InvalidOperationException("no history is attached (call AttachHistory first)");
        SceneRestoreResult r = store.Load(slot, h.History, h.Live);
        ApplyRestore(RestoreKind.Load, r.Restore, h.Live, null, 0);
        ReplayVoiceOf(r);
    }

    private void ReplayVoiceOf(SceneRestoreResult r)
    {
        if (r.ReplayVoiceId >= 0)
        {
            Sound.SetPendingVoice(r.ReplayVoiceId, r.ReplayVoiceCharacter);
        }
    }

    // -- applying a restored record ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Brings the interpreter to the state the history ring wrote into <paramref name="live"/> (the variables are already there): the runtime is reset like a
    /// scene start, the stage is rebuilt from the record's scene state, the music and loop effects restart (a rewind onto the track that plays keeps it),
    /// the screen effects come back from the restored words, the VM is moved, and a rewound line is shown again.
    /// </summary>
    private void ApplyRestore(RestoreKind kind, RestoreOutcome last, AdvLiveState live, RedisplayInfo? redisplay, ushort stopEffects)
    {
        EndSceneSkip(resync: false);
        int autoBit = _modeWord & 1;
        bool replay = ReplayRequested;
        // knife game.rs restore_with: a rewind (sub_5D710) keeps the BGM when the record has the same track and stops only the SEs and the voice; a load, the
        // previous scene and a backlog jump stop every sound (sub_82330 -> sub_5EF10(1)). BgmStart's early-out (VA 0x98C0) then leaves the kept stream alone.
        bool keepBgm = kind == RestoreKind.Rewind && Sound.CurrentTrack == live.BgmTrack;
        ResetRuntime(keepBgm);
        ReplayRequested = replay;
        _modeWord = autoBit;
        Phase = GamePhase.Running;
        EndReason = EndReason.None;
        StopEverythingOutside(keepBgm ? AudioStreams.FirstVoice : 0);

        // the stage: kinds 1 and 9 rebuild the layers from the committed copy and then return to the script-side state, every other kind commits the
        // script-side state (0xE98D0 / 0x7DD10); two updates move B to A and let the zero-length transition end before the first draw
        if (last.RebuildFromCommitted)
        {
            Stage.Scene.CopyFrom(live.Committed);
            Stage.Commit(0, 0, 0);
            Stage.Update(1);
            Stage.Update(1);
            Stage.Scene.CopyFrom(live.Scene);
        }
        else
        {
            Stage.Scene.CopyFrom(live.Scene);
            Stage.Commit(0, 0, 0);
            Stage.Update(1);
            Stage.Update(1);
        }

        _committedScene.CopyFrom(live.Committed);

        // sound: the music and the looping effects from the restored script-side variables
        int loops = 0;
        for (int ch = 0; ch < AdvLiveState.SoundChannels; ch++)
        {
            if (live.LoopSeIds[ch] >= 0 && live.SeVolB[ch] != 0)
            {
                loops++;
            }
        }

        Sound.RestoreScriptState(live.BgmTrack, live.BgmVolume, live.LoopSeIds, live.SeVolA, live.SeVolB, live.SeArrC, live.SeArrE);
        DrainSound();

        // the screen effects (History_Restore's common part, adv_state.md 3.1): the FADE mode from F1100, the focus effect from the scene byte +0x19A2
        RestoreEffects();

        // the script
        Markers.Restore(live.TextBlock, live.TextLine);
        _vm.Seek(new ScriptPosition(live.ScriptNumber, (int)live.ScriptOffset));
        _lastSceneScript = live.ScriptNumber;
        ScriptsReached.Add(live.ScriptNumber);

        // the window: a message record means the message window is open
        if (last.Kind is AdvHistory.Kinds.Message or AdvHistory.Kinds.ChoiceStart or AdvHistory.Kinds.ChoiceLine)
        {
            Wait.WindowTarget = 1;
            Wait.WindowAlpha = 1f;
        }

        _textFade = 1f;
        SetNamePlate(live.Window164FD40);
        bool shown = false;
        if (redisplay is not null)
        {
            ShowRestoredMessage(redisplay, live);
            shown = true;
        }
        else if (last.ShowRecordText is { Length: > 0 } text)
        {
            ShowRestoredMessage(new RedisplayInfo(text, 0, new byte[0x1C]), live);
            shown = true;
        }

        var report = new RestoreReport(kind, live.ScriptNumber, (int)live.ScriptOffset, last.Kind, shown, live.BgmTrack, loops);
        LastRestore = report;
        _restores++;
        Restored?.Invoke(report);
    }

    /// <summary>The re-display of a line (adv_state.md 3.2): the statement text is laid out as <c>_ZM</c> would and waits for its end mode.</summary>
    private void ShowRestoredMessage(RedisplayInfo info, AdvLiveState live)
    {
        byte[] raw = info.Text;
        byte[] text = ExpandMessage(raw);
        int block = live.TextBlock;
        int sub = live.TextLine;
        int max = block is >= 0 and <= MarkerState.MaxRecordedBlock ? Profile.System.GetReadmax(block) : 0;
        bool read = max != 0 && sub <= max;
        MessageEnd mode = info.EndCode switch
        {
            1 => MessageEnd.Continue,
            2 => MessageEnd.NoWait,
            _ => MessageEnd.Click,
        };

        int f823 = F(823);
        _page.Clear();
        (TextBlock layout, int colour) = LayoutText(text, read);
        SetNamePlate(info.NamePlate);
        int f32 = F(32);
        var msg = new PageMessage
        {
            Raw = raw,
            Text = text,
            Block = layout,
            Records = RecordsOf(layout),
            End = mode,
            Colour = colour,
            Read = read,
            Blank = F(846 + f823),
            ScaleY = f32 > 0 ? f32 / 24.0 : 1.0,
            Index = f823,
            Window = WindowIndex(),
            DimLimit = (F(870 + Math.Clamp(f823, 0, 11)) + 128) * 1.2f,
        };
        _page.Add(msg);
        _current = msg;
        _currentRaw = raw;
        _windowHidden = false;
        Wait.ReadSel = read ? 1 : 0;
        Wait.Head = 0f;
        Wait.Tail = 0f;
        ContinueByte = mode == MessageEnd.Continue;
        Wait.Flags |= WaitBits.Reveal;
        Wait.Bits |= (mode == MessageEnd.Click ? WaitBits.Click : 0) | WaitBits.Message;
        if (Wait.Flags != 0)
        {
            Wait.Bits |= Wait.Flags;
        }
    }
}
