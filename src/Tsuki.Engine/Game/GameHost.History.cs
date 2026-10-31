using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;
using Tsuki.Engine.Sound;
using Tsuki.Engine.Stage;

namespace Tsuki.Engine.Game;

/// <summary>
/// Maps the interpreter's push-site hooks onto the history ring of package K4 (<see cref="AdvHistory"/>): before each push the live state the
/// ring copies into a record is filled from the interpreter (<see cref="GameHost.FillLive"/>). Restoring a record (rewind, previous scene, backlog
/// jump, load) is the integration package's job: see docs/status/knife/K3.md "What is left".
/// </summary>
public sealed class AdvHistorySink : IHistorySink
{
    private readonly GameHost _host;
    private readonly Func<SlotDate> _clock;

    /// <summary>Creates the sink over a host. <paramref name="store"/> receives the auto saves of <c>_ATSV(8)</c> (null: they are dropped).</summary>
    public AdvHistorySink(GameHost host, AdvHistory history, AdvLiveState live, SaveStore? store = null, Func<SlotDate>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(live);
        _host = host;
        History = history;
        Live = live;
        Store = store;
        _clock = clock ?? (() => SlotDate.FromDateTime(DateTime.Now));
    }

    /// <summary>The ring.</summary>
    public AdvHistory History { get; }

    /// <summary>The live state the ring copies from and restores into.</summary>
    public AdvLiveState Live { get; }

    /// <summary>The save slots, or null.</summary>
    public SaveStore? Store { get; }

    /// <summary>Pushes recorded so far (for tests).</summary>
    public long Pushes { get; private set; }

    /// <summary>Supplies the thumbnail of an auto save (package K6: the app captures the frame); null: auto saves have none.</summary>
    public Func<SlotThumbnail?>? ThumbnailProvider { get; set; }

    /// <summary>
    /// Raised after <c>_ATSV(8)</c> put its auto save into <see cref="Store"/>: the PS4's AutoSave (0x73B70) ends in SaveImage_Commit(2), which writes every
    /// slot and the system block at once (adv_state.md 7.2); the app commits the save file here.
    /// </summary>
    public event Action? AutoSaved;

    /// <summary>Raised after every scene-stack push (an <c>_ATSV</c> scene start, adv_state.md 5.3): a point at which the app persists the system data.</summary>
    public event Action? SceneStackPushed;

    /// <inheritdoc/>
    public void Push(HistoryKind kind, ScriptPosition position, ScriptPosition resume)
    {
        _host.FillLive(Live, position.Script);
        // Script_Step passes the offset of the byte before the statement's '_' (name - 2); the statement ends at the resume offset
        ulong cmd = (ulong)Math.Max(0, position.Offset - 1);
        if (History.Push(Live, cmd, (ulong)resume.Offset, (ushort)kind))
        {
            Pushes++;
        }
    }

    /// <inheritdoc/>
    public void SceneStackPush()
    {
        History.PushSceneStack(Live.EffectBlockingBits);
        SceneStackPushed?.Invoke();
    }

    /// <inheritdoc/>
    public void AutoSave()
    {
        if (Store is { } store && store.AutoSave(History.Staging, _clock(), ThumbnailProvider?.Invoke()))
        {
            AutoSaved?.Invoke();
        }
    }

    /// <inheritdoc/>
    public void SetMessageText(ReadOnlySpan<byte> text) => History.SetNewestMessageText(text);

    /// <inheritdoc/>
    public void OnSceneJump(int fromScript, int toSceneId)
    {
    }

    /// <inheritdoc/>
    public void OnScriptEnd(EndReason reason)
    {
    }

    /// <inheritdoc/>
    public void OnChoiceTaken(int choiceId, int option)
    {
    }

    /// <inheritdoc/>
    public void OnMessageShown() => History.OnMessageShown();

    /// <inheritdoc/>
    public void OnVoiceStarted(int voiceId, short character, sbyte speakerRow)
    {
        if (Live.RestoredFromHistory == 0)
        {
            History.SetNewestVoice(voiceId, character, speakerRow);
        }
    }

    /// <inheritdoc/>
    public void UpdateStaging()
    {
        _host.FillLive(Live, _host.Script);
        History.UpdateStaging(History.Newest, Live);
    }
}

public sealed partial class GameHost
{
    private readonly SceneState _committedScene = SceneState.CreateDefault();

    /// <summary>
    /// Creates the history ring over this host and plugs it into the push sites (<see cref="Sink"/>). Returns the sink so that the caller can reach
    /// the ring, the live state and the save store.
    /// </summary>
    public AdvHistorySink AttachHistory(SaveStore? store = null)
    {
        var live = new AdvLiveState(Vars, Config, SceneState.CreateDefault(), SceneState.CreateDefault());
        var sink = new AdvHistorySink(this, new AdvHistory(), live, store);
        Sink = sink;
        return sink;
    }

    /// <summary>
    /// Fills the part of <paramref name="live"/> that History_Push copies into a record from the interpreter, stage, sound model and message window
    /// (the member list of docs/status/knife/K4.md). Members with no source in the interpreter yet are left as they are: the window state at
    /// 0x164FD00, the deferred-SE channel table (0x16543F0), the loop-SE word at 0x1654300 and the effect-slot bits other than the movie.
    /// </summary>
    public void FillLive(AdvLiveState live, int script)
    {
        ArgumentNullException.ThrowIfNull(live);
        live.ScriptNumber = (ushort)script;
        live.Scene.CopyFrom(Stage.Scene);
        live.Committed.CopyFrom(_committedScene);
        live.TextBlock = Markers.Block;
        live.TextLine = Markers.Sub;
        live.TextBlock2 = Markers.Block;
        live.TextLine2 = Markers.Sub;
        _namePlate.CopyTo(live.Window164FD40, 0);
        live.Var1654208 = (ushort)Sound.PendingVoiceCharacter;
        live.Var165420A = (ushort)Sound.VoiceArg3;
        Array.Clear(live.MessageText);
        if (_current is { } cur)
        {
            cur.Text.AsSpan(0, Math.Min(cur.Text.Length, AdvLiveState.MessageTextSize - 2)).CopyTo(live.MessageText);
        }

        live.BgmTrack = (short)Sound.ScriptTrack;
        live.BgmVolume = (short)Sound.ScriptBgmVolume;
        for (int ch = 0; ch < AdvLiveState.SoundChannels; ch++)
        {
            ScriptSeState se = Sound.ScriptSe;
            live.LoopSeIds[ch] = (short)se.LoopEntry(ch);
            live.SeVolA[ch] = (ushort)se.Volume(ch);
            live.SeVolB[ch] = (ushort)se.Looped(ch);
            live.SeArrC[ch] = (short)se.Pan(ch);
            live.SeArrE[ch] = (byte)se.Character(ch);
        }

        live.VoiceId = Sound.LastVoice;
        live.Var16541F4 = (byte)(ContinueByte ? 1 : 0);
        live.Var86AA24 = (uint)Wait.NewPage;
        live.TextFade = _textFade;
        live.Var1653E24 = BitConverter.SingleToUInt32Bits(Wait.TextFadeStep);
        live.WaitFlags = Wait.Bits;
        live.WaitFlags70 = Wait.Flags;
        live.Word1653E08 = (ushort)Wait.Timer;
        live.EffectActiveBits = (ushort)(MovieActive ? 1 : 0);
        live.EffectBlockingBits = live.EffectActiveBits;
        live.LastMessageOfPage = F(799) == 1;
        live.PageEndInstantLatch = Wait.PageLatch;
        live.ScanMode = 0;
    }

    /// <summary>Copies the stage's scene state to the committed copy (VA 0x1651AC8): kind-1 pushes, <c>_FADS</c> and the kind-9 commit point do it.</summary>
    private void CopyCommitted() => _committedScene.CopyFrom(Stage.Scene);
}
