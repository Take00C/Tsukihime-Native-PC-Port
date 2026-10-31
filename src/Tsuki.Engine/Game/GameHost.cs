using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game.Effects;
using Tsuki.Engine.Flow;
using Tsuki.Engine.Sound;
using Tsuki.Engine.Stage;
using Tsuki.Engine.Text;
using Tsuki.Engine.Vm;
using Tsuki.Engine.Vm.Variables;

namespace Tsuki.Engine.Game;

/// <summary>Where a session is.</summary>
public enum GamePhase : byte
{
    /// <summary>No scene started.</summary>
    NotStarted = 0,

    /// <summary>A script runs.</summary>
    Running = 1,

    /// <summary>The script ended (<see cref="GameHost.EndReason"/>); the caller goes back to the title.</summary>
    Ended = 2,
}

/// <summary>Why the script ended (script.md 4.1: <c>END_</c>, <c>RTM_</c>, <c>RTMN</c>).</summary>
public enum EndReason : byte
{
    /// <summary>Still running.</summary>
    None = 0,

    /// <summary><c>_END_</c>: script state 1.</summary>
    End = 1,

    /// <summary><c>_RTM_</c>: return to the title, script state 2.</summary>
    ReturnToTitle = 2,

    /// <summary><c>_RTMN</c>: return to the title with the BGM kept.</summary>
    ReturnKeepBgm = 3,

    /// <summary>The cursor ran off the end of the script.</summary>
    EndOfScript = 4,

    /// <summary>A jump named a scene the data does not have.</summary>
    MissingScene = 5,
}

/// <summary>An unknown command in a strict session (dev builds).</summary>
public sealed class UnknownCommandException : Exception
{
    /// <summary>Creates the exception.</summary>
    public UnknownCommandException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public UnknownCommandException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public UnknownCommandException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The game interpreter of the PS4 build: the command handlers (<see cref="IHost"/>), the 60 Hz frame (<see cref="Step"/>) and the state that
/// belongs to a running story (messages, waits, choices, sound, movies, flowchart marks). The VM (<see cref="ScriptVm"/>) executes the variable
/// and flow commands itself and hands every other statement to <see cref="Dispatch"/>. The layer commands go to <see cref="StageRuntime"/>
/// (GameHost.Stage.cs), the sound commands to <see cref="SoundModel"/> (GameHost.Sound.cs); everything the history ring or a save needs is
/// reported through <see cref="IHistorySink"/> (GameHost.Save.cs).
/// </summary>
/// <remarks>
/// Behaviour follows the PS4 documents (wait_model.md, textbox.md, adv_state.md, script.md); where they are silent knife's game.rs is the
/// reference (docs/design/knife_port.md). The frame order is the eboot's (wait_model.md 13). The class is not thread-safe.
/// </remarks>
public sealed partial class GameHost : IHost
{
    private readonly GameServices _services;
    private readonly GameOptions _options;
    private readonly IGameData _data;
    private readonly IGameFlowTables _flow;
    private readonly FontTables _fonts;
    private readonly Language _language;
    private readonly Dictionary<string, int> _unknown = [];
    private ScriptVm _vm;
    private long _frame;
    private int _lastSceneScript = -1;

    /// <summary>Creates a host over the game data. The profile (system block, G words) is new; use <see cref="Profile"/> to carry one over.</summary>
    public GameHost(GameServices services, GameOptions? options = null, GameProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _options = options ?? new GameOptions();
        _data = services.Data;
        _flow = _data.Eboot.GameFlow;
        _fonts = _data.Eboot.GameFonts;
        _language = _options.Language;
        Profile = profile ?? GameProfile.CreateNew(_language);
        VarState = new VarState(new VarMemory(Profile.Vars, OutOfBlockPolicy.Flat));
        Markers = new MarkerState(Profile.System);
        Wait = new WaitState();
        Stage = CreateStage();
        Sound = CreateSound();
        _vm = new ScriptVm(_services.Scripts, VarState, Markers, 0);
        Sink = NullHistorySink.Instance;
        InitMessages();
        InitMovie();
        InitChoice();
    }

    /// <summary>The persistent profile: system block (config, read tracking, unlocks) and the F / Z / G words.</summary>
    public GameProfile Profile { get; }

    /// <summary>The persistent system block.</summary>
    public SystemBlock SystemData => Profile.System;

    /// <summary>The config block (system block + 4).</summary>
    public ConfigBlock Config => Profile.System.Config;

    /// <summary>The F / Z / G variable words.</summary>
    public Vars Vars => Profile.Vars;

    /// <summary>The variable state the VM commands run on (choice registers, argument buffer).</summary>
    public VarState VarState { get; }

    /// <summary>The read tracking (<c>readmax</c>).</summary>
    public MarkerState Markers { get; }

    /// <summary>The wait state (wait_model.md 2).</summary>
    public WaitState Wait { get; }

    /// <summary>The layer stage.</summary>
    public StageRuntime Stage { get; private set; }

    /// <summary>The sound model.</summary>
    public SoundModel Sound { get; private set; }

    /// <summary>The script VM of the running story.</summary>
    public ScriptVm Machine => _vm;

    /// <summary>The running script number, or -1.</summary>
    public int Script => _vm.Image.Index;

    /// <summary>The scene id of the running script.</summary>
    public int SceneId => _flow.SceneId(_vm.Image.Index);

    /// <summary>Where the session is.</summary>
    public GamePhase Phase { get; private set; }

    /// <summary>Why the script ended.</summary>
    public EndReason EndReason { get; private set; }

    /// <summary>The 60 Hz frame counter of the last <see cref="Step"/>.</summary>
    public long Frame => _frame;

    /// <summary>The history / save hooks (GameHost.Save.cs); defaults to a sink that records nothing.</summary>
    public IHistorySink Sink { get; set; }

    /// <summary>Unknown command names seen so far and how often.</summary>
    public IReadOnlyDictionary<string, int> UnknownCommands => _unknown;

    /// <summary>Statements dispatched to the host so far.</summary>
    public long DispatchCount { get; private set; }

    /// <summary>The scripts reached since the host was created (script numbers).</summary>
    public SortedSet<int> ScriptsReached { get; } = [];

    /// <summary>Raised when a script is entered (a new game, a <c>_JUMP</c>): script number, scene id.</summary>
    public event Action<int, int>? SceneEntered;

    /// <summary>The parts of the player's text the options hide from the headless runner: set by the runner for traces.</summary>
    public Action<string>? Log { get; set; }

    // the soft-clip radii of the eboot; a data source that has none (the golden slice, test doubles) leaves the stage its built-in copy
    private static IReadOnlyList<float>? ClipRadiiOf(IEbootTables tables)
    {
        try
        {
            return tables.ClipRadii;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private StageRuntime CreateStage()
    {
        IEbootTables t = _data.Eboot;
        var stage = new StageRuntime(t.Pictures, t.Motions, _options.RandomSeed == 0 ? null : new Ps4Rand(_options.RandomSeed))
        {
            HazeTable = t.Haze,
            ClipRadii = ClipRadiiOf(t),
            Layouts = _services.Layouts,
            Language = (int)_language,
            MotionWaitConfig = Config.ConstantA5,
        };
        _focus = new FocusEffect(t.Effects, stage.Rng);
        _ghost = new GhostEffect();
        stage.AfterLayers = DrawScreenEffects;
        return stage;
    }

    private SoundModel CreateSound()
    {
        var model = new SoundModel(SoundConfig.FromBlock(Config), _services.SeLookup);
        if (!_options.Sound)
        {
            model.IsPlaying = static _ => false;
        }
        else
        {
            model.IsPlaying = _externalPlaying ?? _audio.IsPlaying;
        }

        return model;
    }

    // -- starting a story ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts a new playthrough at scene <paramref name="sceneId"/>: the F and Z words are cleared (the G words and the system block stay), the
    /// stage, sound, messages and waits start fresh, and the script runs from its top (or from <paramref name="label"/>).
    /// </summary>
    public void NewGame(int sceneId, string? label = null)
    {
        Profile.ClearPlaythrough();
        ResetRuntime();
        if (!EnterScene(sceneId, label, jump: false))
        {
            Phase = GamePhase.Ended;
            EndReason = EndReason.MissingScene;
        }
    }

    /// <summary>The scene a New Game starts: START_SCRIPT (110) once scene 260 (the prologue movie, bit 485) was read, else the prologue (script.md 7).</summary>
    public int NewGameSceneId() => IsSceneReached(485) ? 110 : 260;

    /// <param name="keepBgm">A rewind onto a record whose track already plays: the new sound model takes the BGM over and the virtual clock keeps its streams.</param>
    private void ResetRuntime(bool keepBgm = false)
    {
        Wait.Bits = 0;
        Wait.Flags = 0;
        Wait.Timer = 0;
        Wait.Auto = 0;
        Wait.TimerNoClick = 0;
        Wait.StcfLayers = 0;
        Array.Clear(Wait.StcfMask);
        Wait.AdvanceLatch = 0;
        Wait.Head = 0;
        Wait.Tail = 0;
        Wait.Width = 0;
        Wait.AdvKey = 0;
        Wait.NewPage = 0;
        Wait.WindowTarget = 0;
        Wait.WindowAlpha = 0f;
        Wait.TextFadeStep = 0;
        Wait.ReadSel = 0;
        Wait.VoiceState = 0;
        Wait.FadeState = 0;
        VarState.ChoiceMode = ChoiceMode.None;
        VarState.ChoiceCount = 0;
        VarState.ChoiceValues.Clear();
        VarState.ChoiceLocked.Clear();
        VarState.ChoiceId = -1;
        VarState.SkipCounter = 0;
        Stage = CreateStage();
        SoundModel previous = Sound;
        Sound = CreateSound();
        if (keepBgm)
        {
            Sound.AdoptBgm(previous);
        }

        _audio.Reset(keepBgm ? AudioStreams.FirstVoice : 0);
        ResetMessages();
        ResetMovie();
        ResetChoice();
        ResetEffects();
        ResetMarks();
        _modeWord = 0;
        ReplayRequested = false;
        SkipStoppedByReadRule = false;
        Phase = GamePhase.NotStarted;
        EndReason = EndReason.None;
    }

    /// <summary>
    /// Enters scene <paramref name="sceneId"/> (a new game or a <c>_JUMP</c>) and positions the VM at <paramref name="label"/> or the script start.
    /// A scene id the table does not have falls back to START_SCRIPT (script.md 6.1). Returns false when no script can be found.
    /// </summary>
    private bool EnterScene(int sceneId, string? label, bool jump)
    {
        int k = _flow.ScriptOfSceneId(sceneId);
        if (k < 0)
        {
            k = _flow.ScriptOfSceneId(110);
            if (k < 0)
            {
                return false;
            }
        }

        var target = new ScriptPosition(k, 0);
        if (!string.IsNullOrEmpty(label))
        {
            int offset = _services.Scripts.Labels(k).Find(label);
            if (offset < 0)
            {
                throw new ScriptFlowException($"scene {sceneId} (script {k}): label '{label}' is not in its label table");
            }

            target = new ScriptPosition(k, offset);
        }

        if (!jump)
        {
            _vm = new ScriptVm(_services.Scripts, VarState, Markers, k, target.Offset);
            if (_traceObserver is not null)
            {
                _vm.Observer = _traceObserver;
            }
        }

        _lastSceneScript = k;
        ScriptsReached.Add(k);
        Phase = GamePhase.Running;
        SceneEntered?.Invoke(k, _flow.SceneId(k));
        return true;
    }

    private Action<FlowStep>? _traceObserver;

    /// <summary>Sets the statement observer of the VM (traces and tests); null removes it.</summary>
    public void SetObserver(Action<FlowStep>? observer)
    {
        _traceObserver = observer;
        _vm.Observer = observer;
    }

    // -- the command dispatch -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// When set, called for every statement whose dispatch took 5 ms or more: command name, script, offset, milliseconds (the verification mode's hitch log, package K8).
    /// </summary>
    public Action<string, int, int, double>? SlowStatement { get; set; }

    /// <inheritdoc/>
    public StepResult Dispatch(in Statement statement)
    {
        if (SlowStatement is not { } slow)
        {
            return DispatchCore(statement);
        }

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        StepResult result = DispatchCore(statement);
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (ms >= 5)
        {
            slow(statement.Name.ToString(), statement.Script, statement.Offset, ms);
        }

        return result;
    }

    private StepResult DispatchCore(in Statement statement)
    {
        DispatchCount++;
        Cmd cmd = GameCommands.Classify(statement.Name);
        switch (cmd)
        {
            case Cmd.Stage:
                return DispatchStage(statement);
            case Cmd.Sound:
                return DispatchSound(statement);
            case Cmd.ZoneMessage:
                return CmdMessage(statement);
            case Cmd.ZoneDisabled:
                return StepResult.Continue;
            case Cmd.Msad:
                return CmdMsad(statement);
            case Cmd.Wnty:
                return CmdWnty(statement);
            case Cmd.Pgst:
                return CmdPgst(statement);
            case Cmd.Clo2:
                return CmdClo2();
            case Cmd.Clo3:
                return CmdClo3();
            case Cmd.Clo4:
                return CmdClo4(statement);
            case Cmd.Clo5:
                return CmdClo5(statement);
            case Cmd.Wttm:
            case Cmd.Wttr:
                return CmdWttm(statement);
            case Cmd.Wtsv:
                return CmdWtsv(statement);
            case Cmd.Selr:
                return CmdSelr(statement);
            case Cmd.Seld:
                return CmdSeld(statement);
            case Cmd.Sels:
                return CmdSels(statement);
            case Cmd.Sela:
                return CmdSela(statement);
            case Cmd.Vcwt:
                return CmdVcwt();
            case Cmd.Tomp:
                return CmdTomp(statement);
            case Cmd.Tomw:
                return CmdTomw(statement);
            case Cmd.Jump:
                return CmdJump(statement);
            case Cmd.End:
                return ScriptEnd(EndReason.End, keepBgm: false);
            case Cmd.Rtm:
                return ScriptEnd(EndReason.ReturnToTitle, keepBgm: false);
            case Cmd.Rtmn:
                _keepBgm = true;
                return ScriptEnd(EndReason.ReturnKeepBgm, keepBgm: true);
            case Cmd.Rtm2:
                _keepBgm = true;
                return StepResult.Continue;
            case Cmd.Ifsc:
                // script.md 4.1: "goto label if a system condition holds"; at the start of ARCHIVE. Outside the gallery the condition is false
                // (knife: IFSC jumps unless the scene runs as a gallery replay).
                return JumpToLabelArg(statement, 1, condition: !ReplayRequested);
            case Cmd.Ifur:
                return JumpToLabelArg(statement, 1, condition: AllEndingsDone());
            case Cmd.Ifla:
                return JumpToLabelArg(statement, 1, condition: InlineInt(statement, 0) != (int)_language);
            case Cmd.Ifjp:
                return StepResult.Continue;
            case Cmd.Ifp4:
            case Cmd.Ifnx:
            case Cmd.Iff:
            case Cmd.Els:
            case Cmd.Eif:
            case Cmd.Nop:
            case Cmd.Ignored:
                return StepResult.Continue;
            case Cmd.Atsv:
                return CmdAtsv(statement);
            case Cmd.Rdst:
                return CmdRdst(statement);
            case Cmd.Rtst:
                return CmdRtst(statement);
            case Cmd.Rded:
                return CmdRded(statement);
            case Cmd.Rdas:
                return CmdRdas(statement);
            case Cmd.Trpy:
                TrophyUnlocked?.Invoke(InlineInt(statement, 0));
                return StepResult.Continue;
            case Cmd.Xstp:
                return CmdXstp();
            case Cmd.Xrsm:
                return CmdXrsm();
            case Cmd.Fcln:
            case Cmd.Fcsp:
            case Cmd.Flnt:
            case Cmd.Flop:
            case Cmd.Flsp:
            case Cmd.Fccl:
                return CmdFocus(statement, cmd);
            case Cmd.Fade:
                return CmdFade(statement);
            case Cmd.Stcg:
                return CmdStcg(statement);
            case Cmd.Spfc:
            case Cmd.Sprd:
            case Cmd.Sprc:
            case Cmd.Stpv:
                return DispatchStage(statement);
            default:
                return CmdUnknown(statement);
        }
    }

    /// <summary>Raised by <c>_TRPY(n)</c> (trophy n; the platform layer decides what to do).</summary>
    public event Action<int>? TrophyUnlocked;

    private StepResult CmdUnknown(in Statement st)
    {
        string name = st.Name.ToString();
        _unknown[name] = _unknown.GetValueOrDefault(name) + 1;
#if TSUKI_DEV
        if (!_options.RecordUnknownCommands)
        {
            throw new UnknownCommandException($"script {st.Script} offset 0x{st.Offset:x}: command {name} is not a command of the PS4 interpreter");
        }
#endif
        if (_options.StrictCommands)
        {
            throw new UnknownCommandException($"script {st.Script} offset 0x{st.Offset:x}: command {name} is not a command of the PS4 interpreter");
        }

        return StepResult.Continue;
    }

    // -- flow ---------------------------------------------------------------------------------------------------------------------

    /// <summary>True while a gallery replay runs (<c>F024</c>, set by the ARCHIVE scene itself; the replay scenes end at their <c>_RDED</c>).</summary>
    public bool ArchiveMode => F(24) != 0;

    /// <summary>True when the gallery started the ARCHIVE scene (the system condition <c>_IFSC</c> tests, VA 0x67430): see <see cref="StartReplay"/>.</summary>
    public bool ReplayRequested { get; private set; }

    /// <summary>
    /// Starts a gallery replay: the ARCHIVE scene (3000) runs with G008 = <paramref name="sceneId"/> (the scene to replay); the scene sets F024 / F025
    /// itself and jumps to the replayed scene, which ends at its <c>_RDED</c> (script.md 7, gallery.md 2).
    /// </summary>
    public bool StartReplay(int sceneId)
    {
        Profile.ClearPlaythrough();
        ResetRuntime();
        Vars.SetG(8, (ushort)sceneId);
        ReplayRequested = true;
        return EnterScene(3000, null, jump: false);
    }

    private static int InlineInt(in Statement st, int index) => InlineReader.ReadInt16(st, index);

    private StepResult JumpToLabelArg(in Statement st, int argIndex, bool condition)
    {
        if (!condition)
        {
            return StepResult.Continue;
        }

        string label = ArgText(st, argIndex);
        if (label.Length == 0)
        {
            return StepResult.Continue;
        }

        return StepResult.ContinueAt(LabelPosition(label));
    }

    private ScriptPosition LabelPosition(string label)
    {
        int offset = _services.Scripts.Labels(_vm.Image.Index).Find(label);
        if (offset < 0)
        {
            throw new ScriptFlowException($"script {_vm.Image.Index}: label '{label}' is not in its label table");
        }

        return new ScriptPosition(_vm.Image.Index, offset);
    }

    /// <summary>The text of textual argument <paramref name="index"/> (Latin-1; the inline layout of an ordinary command).</summary>
    private static string ArgText(in Statement st, int index)
    {
        ReadOnlySpan<byte> a = st.Args.Span;
        var ranges = new List<ArgRange>(4);
        ArgumentList.Split(a, ArgumentList.StartIn(st), ranges);
        if (index >= ranges.Count)
        {
            return string.Empty;
        }

        return System.Text.Encoding.Latin1.GetString(a[ranges[index].Start..ranges[index].End]);
    }

    private StepResult CmdJump(in Statement st)
    {
        EndSceneSkip();
        string idText = ArgText(st, 0);
        int id = ObjectParser.PartialInt(System.Text.Encoding.Latin1.GetBytes(idText)) ?? -1;
        string label = ArgText(st, 1);
        int k = _flow.ScriptOfSceneId(id);
        MarkSceneJump(_vm.Image.Index, id);
        Sink.OnSceneJump(_vm.Image.Index, id);
        if (k < 0)
        {
            // script.md 6.1: unknown ids fall back to START_SCRIPT
            k = _flow.ScriptOfSceneId(110);
            label = string.Empty;
            if (k < 0)
            {
                Phase = GamePhase.Ended;
                EndReason = EndReason.MissingScene;
                return StepResult.Halt;
            }
        }

        int offset = 0;
        if (label.Length > 0)
        {
            offset = _services.Scripts.Labels(k).Find(label);
            if (offset < 0)
            {
                throw new ScriptFlowException($"JUMP to scene {id} (script {k}): label '{label}' is not in its label table");
            }
        }

        _lastSceneScript = k;
        ScriptsReached.Add(k);
        SceneEntered?.Invoke(k, _flow.SceneId(k));
        return StepResult.YieldAt(new ScriptPosition(k, offset));
    }

    private bool _keepBgm;

    /// <summary>
    /// The script-end cleanup (VA 0x7F1A0; END_, RTM_, RTMN): every SE channel stops, the voice stops, the BGM stops unless the keep flag
    /// (RTMN / RTM2) is set, the window closes. The host halts the VM; the caller returns to the title (<see cref="Phase"/> = Ended).
    /// </summary>
    private StepResult ScriptEnd(EndReason reason, bool keepBgm)
    {
        EndSceneSkip();                                 // END_ (0x9B08C) and RDED / RTMN / RTM_ (0x9AA7A) call the resync 0x7E620 like the other end sites (adv_state.md 5.2)
        EndReason = reason;
        Phase = GamePhase.Ended;
        Sound.Run("_SESP(-1);");
        Sound.StopVoice();
        if (!keepBgm && !_keepBgm)
        {
            Sound.Run("_MPLY(-1);");
        }

        _keepBgm = false;
        Wait.Bits = 0;
        Sink.OnScriptEnd(reason);
        return StepResult.Halt;
    }

}
