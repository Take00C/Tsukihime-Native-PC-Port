namespace Tsuki.Data.Eboot.Tables;

/// <summary>
/// Every table K1b reads from the eboot (docs/research/knife_tables.md), each built on first use over one <see cref="EbootReader"/> and shared afterwards.
/// All tables are immutable once built, so one instance may serve several threads. The cell tables of the UI (<see cref="UiCellTables"/>) are static
/// readers and are not repeated here.
/// </summary>
public sealed class EngineTables
{
    private readonly Lazy<HazeTable> _haze;
    private readonly Lazy<SceneNodeTable> _sceneNodes;
    private readonly Lazy<MotionLoopTable> _motionLoops;
    private readonly Lazy<JumpClearTable> _jumpClears;
    private readonly Lazy<FlowKeepLists> _flowKeep;
    private readonly Lazy<FlowKeepLists> _historyKeep;
    private readonly Lazy<FclnLineTable> _fclnLines;
    private readonly Lazy<IReadOnlyList<FclnNote>> _fclnNotes;
    private readonly Lazy<VoiceSpeakerTable> _voices;
    private readonly Lazy<IReadOnlyList<string>> _testSes;
    private readonly Lazy<DialogMessageTable> _dialogs;
    private readonly Lazy<MessageWindowTable> _windows;
    private readonly Lazy<IReadOnlyList<float>> _clipRadii;
    private readonly Lazy<IReadOnlyList<(byte R, byte G, byte B)>> _textColours;
    private readonly Lazy<IReadOnlyList<ConfigPage>> _configPages;
    private readonly Lazy<MotionFieldMasks> _motionMasks;
    private readonly Lazy<GalleryTables> _gallery;
    private readonly Lazy<FlowchartTables> _flowchart;

    /// <summary>Creates the set over a parsed eboot.</summary>
    public EngineTables(EbootReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Reader = reader;
        const LazyThreadSafetyMode Mode = LazyThreadSafetyMode.ExecutionAndPublication;
        _haze = new(() => HazeTable.Read(reader), Mode);
        _sceneNodes = new(() => SceneNodeTable.Read(reader), Mode);
        _motionLoops = new(() => MotionLoopTable.Read(reader), Mode);
        _jumpClears = new(() => JumpClearTable.Read(reader), Mode);
        _flowKeep = new(() => FlowKeepLists.Read(reader), Mode);
        _historyKeep = new(() => FlowKeepLists.Read(reader, history: true), Mode);
        _fclnLines = new(() => FclnLineTable.Read(reader), Mode);
        _fclnNotes = new(() => FclnNoteTable.Read(reader), Mode);
        _voices = new(() => VoiceSpeakerTable.Read(reader), Mode);
        _testSes = new(() => TestSeNames.Read(reader), Mode);
        _dialogs = new(() => DialogMessageTable.Read(reader), Mode);
        _windows = new(() => MessageWindowTable.Read(reader), Mode);
        _clipRadii = new(() => SoftClipRadii.Read(reader), Mode);
        _textColours = new(() => TextColourTable.Read(reader), Mode);
        _configPages = new(() => ConfigPageTable.Read(reader), Mode);
        _motionMasks = new(() => MotionFieldMasks.Read(reader), Mode);
        _gallery = new(() => GalleryTables.Read(reader), Mode);
        _flowchart = new(() => FlowchartTables.Read(reader), Mode);
    }

    /// <summary>The parsed eboot the tables are read from.</summary>
    public EbootReader Reader { get; }

    /// <summary>The STRA haze parameters (558 entries).</summary>
    public HazeTable Haze => _haze.Value;

    /// <summary>Flowchart node per scene (527).</summary>
    public SceneNodeTable SceneNodes => _sceneNodes.Value;

    /// <summary>STMA loop points (2,027 looping motions; the first call interprets 24,192 calls of VA 0xC1C70, about 0.1 s).</summary>
    public MotionLoopTable MotionLoops => _motionLoops.Value;

    /// <summary>The F words the flowchart jump zeroes by column.</summary>
    public JumpClearTable JumpClears => _jumpClears.Value;

    /// <summary>The F-word lists kept through a flowchart jump.</summary>
    public FlowKeepLists FlowKeep => _flowKeep.Value;

    /// <summary>The same lists as saved in a history record.</summary>
    public FlowKeepLists HistoryKeep => _historyKeep.Value;

    /// <summary>The 30 focus-line effects.</summary>
    public FclnLineTable FclnLines => _fclnLines.Value;

    /// <summary>The 3 falling-note kinds of FCLN effects 1..3.</summary>
    public IReadOnlyList<FclnNote> FclnNotes => _fclnNotes.Value;

    /// <summary>Voice speakers, speaker row bits and voice row words.</summary>
    public VoiceSpeakerTable Voices => _voices.Value;

    /// <summary>The 14 config SE test names.</summary>
    public IReadOnlyList<string> TestSes => _testSes.Value;

    /// <summary>The confirm-dialog / help message ids.</summary>
    public DialogMessageTable Dialogs => _dialogs.Value;

    /// <summary>The message window types and offsets.</summary>
    public MessageWindowTable Windows => _windows.Value;

    /// <summary>The six soft-clip radii.</summary>
    public IReadOnlyList<float> ClipRadii => _clipRadii.Value;

    /// <summary>The five <c>@c1..@c5</c> fill colours.</summary>
    public IReadOnlyList<(byte R, byte G, byte B)> TextColours => _textColours.Value;

    /// <summary>The two config pages with control positions.</summary>
    public IReadOnlyList<ConfigPage> ConfigPages => _configPages.Value;

    /// <summary>The STMA field masks.</summary>
    public MotionFieldMasks MotionMasks => _motionMasks.Value;

    /// <summary>The CG gallery, music room and ending list.</summary>
    public GalleryTables Gallery => _gallery.Value;

    /// <summary>The flowchart columns, nodes, edges and choice flags.</summary>
    public FlowchartTables Flowchart => _flowchart.Value;
}
