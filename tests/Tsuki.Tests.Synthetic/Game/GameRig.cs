using System.Reflection;
using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Flow;
using Tsuki.Engine.Game;
using Tsuki.Engine.Stage;
using Tsuki.Engine.Text;
using Tsuki.Engine.Vm;

namespace Tsuki.Tests.Synthetic.Game;

/// <summary>An interface implemented by a table of delegates keyed by member name; any other member throws (new interface members stay harmless).</summary>
public class DelegatingProxy : DispatchProxy
{
    /// <summary>The handlers by method name (<c>get_GameFlow</c>, <c>Script</c>, ...).</summary>
    public Dictionary<string, Func<object?[], object?>> Handlers { get; } = [];

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        string name = targetMethod!.Name;
        if (Handlers.TryGetValue(name, out Func<object?[], object?>? h))
        {
            return h(args ?? []);
        }

        throw new NotSupportedException($"the test rig does not provide {name}");
    }

    /// <summary>Creates a proxy of <typeparamref name="T"/> and returns it with its handler table.</summary>
    public static (T Proxy, Dictionary<string, Func<object?[], object?>> Handlers) Create<T>()
        where T : class
    {
        T proxy = DispatchProxy.Create<T, DelegatingProxy>();
        return (proxy, ((DelegatingProxy)(object)proxy).Handlers);
    }
}

/// <summary>Scene and flowchart tables of the rig: script k has scene id 100 + k and no flowchart node unless set.</summary>
public sealed class RigFlow : IGameFlowTables
{
    private readonly int _count;

    public RigFlow(int count) => _count = count;

    public Dictionary<int, int> NodeMap { get; } = [];

    public List<FlowEdgeRow> EdgeRows { get; } = [];

    public List<FlowNodeRow> NodeList { get; } = [];

    public int SceneCount => _count;

    public int SceneId(int script) => 100 + script;

    public int ScriptOfSceneId(int sceneId) => sceneId >= 100 && sceneId < 100 + _count ? sceneId - 100 : -1;

    public int NodeOfScript(int script) => NodeMap.GetValueOrDefault(script, -1);

    public int PictureSeenIndex(int pictureId) => pictureId;

    public IReadOnlyList<FlowEdgeRow> Edges => EdgeRows;

    IReadOnlyList<FlowNodeRow> IGameFlowTables.Nodes => NodeList;

    public List<int> KeepWords { get; } = [];

    public IReadOnlyList<int> JumpKeepWords => KeepWords;

    public IReadOnlyList<int> JumpKeepBytes => [];

    public IReadOnlyList<int> JumpKeepBits => [];

    public IReadOnlyList<(int LastColumn, int Word)> JumpClears => [];
}

/// <summary>The scripts of the rig.</summary>
public sealed class RigScripts : IFlowScripts
{
    private readonly List<string> _text = [];

    public int Add(string script)
    {
        _text.Add(script);
        return _text.Count - 1;
    }

    public int Count => _text.Count;

    public ScriptImage Script(int k) => ScriptSegmenter.CreateImage(k, Encoding.Latin1.GetBytes(_text[k]));

    /// <summary>Every <c>_ZZ</c> / <c>_ZY</c> statement with a non-empty argument is a label at its offset.</summary>
    public LabelTable Labels(int k)
    {
        string t = _text[k];
        var labels = new List<(string, int, int)>();
        int pos = 0;
        while ((pos = t.IndexOf("_Z", pos, StringComparison.Ordinal)) >= 0)
        {
            if (pos + 2 < t.Length && t[pos + 2] is 'Z' or 'Y')
            {
                int open = t.IndexOf('(', pos);
                int close = t.IndexOf(')', open);
                if (close > open + 1)
                {
                    labels.Add((t[(open + 1)..close], 1, pos));
                }
            }

            pos += 2;
        }

        return LabelTable.FromLabels(labels);
    }
}

/// <summary>
/// A game host over hand-made scripts and text: the data layer is a proxy that provides only what the interpreter asks for (scripts, text lines,
/// the English glyph tables, the scene table, an empty picture catalog), so the tests run without game data.
/// </summary>
public sealed class GameRig
{
    private readonly Dictionary<int, string> _lines = [];

    public GameRig(Language language = Language.En)
    {
        Language = language;
        Flow = new RigFlow(0);
    }

    public Language Language { get; }

    public RigScripts Scripts { get; } = new();

    public RigFlow Flow { get; private set; }

    public GameHost Host { get; private set; } = null!;

    /// <summary>The data proxy of the last <see cref="Build"/> (package K6: the app services take it).</summary>
    public IGameData Data { get; private set; } = null!;

    public IReadOnlyList<string> PictureNames { get; set; } = ["BLACK", "IMG0001", "IMG0002", "IMG0003"];

    /// <summary>The rig's voice row words: config voice row k -> G[100 + k] (the UI test kit's table; package K36).</summary>
    public static IReadOnlyList<int> VoiceRowWords { get; } = [.. Enumerable.Range(100, 19)];

    /// <summary>Defines text line <paramref name="id"/> (<c>$00000N</c>).</summary>
    public GameRig Line(int id, string text)
    {
        _lines[id] = text;
        return this;
    }

    public GameRig Script(string text)
    {
        Scripts.Add(text);
        return this;
    }

    /// <summary>Creates the host over the scripts added so far.</summary>
    public GameHost Build(GameOptions? options = null, GameProfile? profile = null)
    {
        Flow = new RigFlow(Scripts.Count);
        FontTables fonts = RigFont.Create();
        var catalog = new ArrayPictureCatalog(PictureNames, PictureNames.Select(_ => new PictureInfo(0, 0, 0, 0, false)).ToArray());
        var motions = new ArrayMotionTables([100], [1], [0], new long[MotionTable.FieldCount], [0UL], [0]);
        (IEbootTables eboot, var eh) = DelegatingProxy.Create<IEbootTables>();
        eh["get_GameFlow"] = _ => Flow;
        eh["get_GameFonts"] = _ => fonts;
        eh["get_Pictures"] = _ => catalog;
        eh["get_Motions"] = _ => motions;
        eh["get_Haze"] = _ => new NoHaze();
        eh["get_Effects"] = _ => null;
        // package K36: the voice row words (config voice row k -> G[100 + k]) the _VPLY marks and the Sound tab use; every other UI table stays unavailable
        (IUiTables ui, var uh) = DelegatingProxy.Create<IUiTables>();
        uh["get_VoiceRowWords"] = _ => VoiceRowWords;
        eh["get_Ui"] = _ => ui;
        (IGameData data, var dh) = DelegatingProxy.Create<IGameData>();
        dh["get_Eboot"] = _ => eboot;
        dh["get_ScriptCount"] = _ => Scripts.Count;
        dh["Script"] = a => Scripts.Script((int)a[0]!);
        dh["get_TextLineCount"] = _ => 43_969;
        // package K12: the app runtime draws the window, which needs the size of its sheet; package K34: the click-wait icon sheets have their retail sizes
        dh["UiSheetSize"] = a => (int)a[2]! switch { GameHost.LineIconChild or GameHost.PageIconChild => (4095, 39), GameHost.BubbleIconChild => (2537, 46), _ => (1000, 300) };
        dh["TextLine"] = a => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(_lines.GetValueOrDefault((int)a[1]!, string.Empty));
        var services = new GameServices(data, Scripts) { SeLookup = (name, _) => name.StartsWith("SE", StringComparison.Ordinal) ? 1 : null };
        Data = data;
        Host = new GameHost(services, options ?? new GameOptions { Language = Language, RecordUnknownCommands = true }, profile);
        return Host;
    }

    private sealed class NoHaze : IHazeTable
    {
        public bool TryGetEntry(int straId, out float[] entry)
        {
            entry = [];
            return false;
        }
    }

    /// <summary>Steps frames without input until <paramref name="stop"/> is true; returns the frames stepped.</summary>
    public int Run(Func<GameHost, bool> stop, int max = 5000)
    {
        int n = 0;
        while (n < max && !stop(Host))
        {
            Host.Step(default);
            n++;
        }

        return n;
    }

    /// <summary>Presses decide (an edge) in the next frame and steps it.</summary>
    public void Click() => Host.Step(new InputFrame { Held = PadButtons.Decide, Pressed = PadButtons.Decide, Repeated = PadButtons.Decide });
}

/// <summary>English glyph tables for ASCII: every character 0x20..0x7E has a 20 x 40 cell in the narrow table.</summary>
public static class RigFont
{
    public static FontTables Create()
    {
        string[] lookupNames = ["narrow", "ja_s1", "en_s1", "zc_s1", "zt_s1", "ko_s1", "ja_s2", "en_s2", "zc_s2", "zt_s2", "ko_s2", "ja_wide", "en_wide", "zc_wide", "zt_wide", "ko_wide"];
        string[] recordNames = ["ja_s0", "ja_s2", "ja_s1", "en_s0", "en_s2", "en_s3", "en_s4", "en_s5", "en_s1", "zc_s0", "zc_s2", "zc_s1", "zt_s0", "zt_s1", "ko_s0", "ko_s2", "ko_s1"];
        ulong[] keys = [.. Enumerable.Range(0x20, 0x5F).Select(c => (ulong)c)];
        int[] indices = [.. Enumerable.Range(0, 0x5F)];
        var lookups = lookupNames.Select(n => n == "narrow" ? new GlyphLookupTable(n, keys, indices) : new GlyphLookupTable(n, [], [])).ToArray();
        GlyphRect[] cells = [.. Enumerable.Range(0, 0x5F).Select(i => new GlyphRect(34, (i % 40) * 24, (i / 40) * 44, 20, 40))];
        var records = recordNames.Select(n => n is "en_s0" or "ja_s0" ? new GlyphRecordTable(n, cells.Length, GlyphRecordTable.Pack(cells)) : new GlyphRecordTable(n, 0, ReadOnlyMemory<byte>.Empty)).ToArray();
        return new FontTables(lookups, records, [59, 59, 59, 57, 59]);
    }
}
