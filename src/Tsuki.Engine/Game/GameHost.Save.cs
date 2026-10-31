using Tsuki.Engine.Contracts;
using Tsuki.Engine.Vm;

namespace Tsuki.Engine.Game;

/// <summary>The kinds of history record the interpreter pushes (adv_state.md 2.3).</summary>
public enum HistoryKind : byte
{
    /// <summary>The <c>_ZM</c> message marker (site VA 0x9291F).</summary>
    Message = 0,

    /// <summary>The first layer change since the last commit (F991 was 0).</summary>
    LayerBatchStart = 1,

    /// <summary>Music: <c>MPLY MFAD MVOL MSTP</c>.</summary>
    Music = 2,

    /// <summary>Sound effects: <c>SEPL SEVL SEFD SESP</c> and friends.</summary>
    SoundEffect = 3,

    /// <summary>Scene start: <c>_ATSV()</c> and <c>_ATSV(8)</c>.</summary>
    SceneStart = 6,

    /// <summary>Choice save point: <c>_ATSV(n)</c> with <c>n &amp; 3 == 1</c>.</summary>
    ChoicePoint = 7,

    /// <summary>Commit point of a pending layer change (F991 != 0).</summary>
    LayerCommit = 9,

    /// <summary>The start of a choice (<c>_SELD</c>).</summary>
    ChoiceStart = 12,

    /// <summary>A choice line (<c>_SELR</c>, the first of a group).</summary>
    ChoiceLine = 13,
}

/// <summary>
/// One hook per push site of the history ring and of the save machinery (adv_state.md 2.4, 2.5, 5, 7). The interpreter calls them at the engine's
/// own sites, in the engine's order, before the command's effect where the engine pushes before it (History_Push stores the state before the
/// command). The real implementation (the history ring, the scene stack, the slots: package K4) plugs in here; <see cref="NullHistorySink"/>
/// records nothing.
/// </summary>
public interface IHistorySink
{
    /// <summary>
    /// History_Push (VA 0x7AF90) of <paramref name="kind"/> for the statement at <paramref name="position"/>. The interpreter has already applied
    /// the engine's own gates (scan mode, F929, F1102, the continue byte) except the ones that need the ring.
    /// </summary>
    void Push(HistoryKind kind, ScriptPosition position, ScriptPosition resume);

    /// <summary>SceneStack_Push (VA 0x7E2E0, adv_state.md 5.3) after a kind-6 / kind-7 push of <c>_ATSV</c>.</summary>
    void SceneStackPush();

    /// <summary>The auto save of <c>_ATSV(8)</c> (VA 0x73B70, adv_state.md 7.2).</summary>
    void AutoSave();

    /// <summary>The message text the newest record keeps (adv_state.md 2.5: record +0x38FE).</summary>
    void SetMessageText(ReadOnlySpan<byte> text);

    /// <summary>A <c>_JUMP</c> leaves script <paramref name="fromScript"/> for scene <paramref name="toSceneId"/>.</summary>
    void OnSceneJump(int fromScript, int toSceneId);

    /// <summary>The script ended (<c>_END_</c>, <c>_RTM_</c>, <c>_RTMN</c>).</summary>
    void OnScriptEnd(EndReason reason);

    /// <summary>The player took choice <paramref name="choiceId"/> option <paramref name="option"/> (the choice-taken bit set; a save point candidate).</summary>
    void OnChoiceTaken(int choiceId, int option);

    /// <summary>A message was shown (<c>_ZM</c>): the scene-start counter counts down (VA 0x92414).</summary>
    void OnMessageShown();

    /// <summary>A voice started for the message just pushed (voice play stores voice id, character and speaker row in the newest record).</summary>
    void OnVoiceStarted(int voiceId, short character, sbyte speakerRow);

    /// <summary>The Staging_SetSnapshot site of <c>_ZM</c>, <c>_MSAD</c>, <c>_ATSV</c> and <c>_SELD</c> (adv_state.md 7.1).</summary>
    void UpdateStaging();
}

/// <summary>A sink that records nothing.</summary>
public sealed class NullHistorySink : IHistorySink
{
    /// <summary>The shared instance.</summary>
    public static NullHistorySink Instance { get; } = new();

    /// <inheritdoc/>
    public void Push(HistoryKind kind, ScriptPosition position, ScriptPosition resume)
    {
    }

    /// <inheritdoc/>
    public void SceneStackPush()
    {
    }

    /// <inheritdoc/>
    public void AutoSave()
    {
    }

    /// <inheritdoc/>
    public void SetMessageText(ReadOnlySpan<byte> text)
    {
    }

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
    public void OnMessageShown()
    {
    }

    /// <inheritdoc/>
    public void OnVoiceStarted(int voiceId, short character, sbyte speakerRow)
    {
    }

    /// <inheritdoc/>
    public void UpdateStaging()
    {
    }
}

public sealed partial class GameHost
{
    private const int LayerBatchFlagVar = 991;
    private const int HistorySuppressVar = 929;
    private const int HistoryModeVar = 1102;

    /// <summary>
    /// History_Push entry gate (VA 0x7AF9B..0x7AFD7) as far as the variables decide it: F929 != 0 stops every push, F1102 != 0 stops all
    /// but kinds 0 and 11.
    /// </summary>
    private bool PushAllowed(HistoryKind kind)
    {
        if (F(HistorySuppressVar) != 0 || ContinueByte)
        {
            return false;
        }

        return F(HistoryModeVar) == 0 || kind is HistoryKind.Message or (HistoryKind)11;
    }

    private void Push(HistoryKind kind, in Statement st)
    {
        if (PushAllowed(kind))
        {
            Sink.Push(kind, st.Position, st.Next);
        }
    }

    /// <summary>
    /// The layer-change batch of adv_state.md 2.3: the first layer change since the last commit (F991 == 0) pushes kind 1 and sets F991 = 1
    /// (the engine also copies the scene state to the committed copy there; the stage runtime keeps its own committed records).
    /// </summary>
    private void NoteLayerChange(in Statement st)
    {
        if (F(LayerBatchFlagVar) == 0)
        {
            Push(HistoryKind.LayerBatchStart, st);
            CopyCommitted();
            SetF(LayerBatchFlagVar, 1);
        }
    }

    /// <summary>The commit point of a pending layer change (<c>VPLY VPL2 Z* WTTM MSAD</c>): kind 9 and F991 = 0.</summary>
    private void CommitLayerChange(in Statement st)
    {
        if (F(LayerBatchFlagVar) != 0)
        {
            CopyCommitted();
            Push(HistoryKind.LayerCommit, st);
            SetF(LayerBatchFlagVar, 0);
        }
    }

    private StepResult CmdAtsv(in Statement st)
    {
        // adv_state.md 2.5: argc = 0 -> n = low = 2; else low = n & 3. Kind 7 for low == 1, else kind 6. n == 8 auto-saves.
        bool hasArg = InlineReader.IsPresent(st, 0);
        int n = hasArg ? InlineReader.ReadInt16(st, 0) : 2;
        int low = hasArg ? n & 3 : 2;
        if (SkipScene)
        {
            // the next-scene skip ends at an ATSV (adv_state.md 5.2)
            EndSceneSkip();
        }

        if (n == 8 && hasArg)
        {
            Push(HistoryKind.SceneStart, st);
            Sink.SetMessageText(ReadOnlySpan<byte>.Empty);
            Sink.UpdateStaging();
            Sink.SceneStackPush();
            Sink.AutoSave();
            return StepResult.Continue;
        }

        HistoryKind kind = low == 1 ? HistoryKind.ChoicePoint : HistoryKind.SceneStart;
        Push(kind, st);
        Sink.SetMessageText(kind == HistoryKind.ChoicePoint && _current is { } cur ? cur.Text : ReadOnlySpan<byte>.Empty);
        Sink.UpdateStaging();

        if (!hasArg || low == 2 || (n & 4) == 0)
        {
            Sink.SceneStackPush();
        }

        return StepResult.Continue;
    }
}
