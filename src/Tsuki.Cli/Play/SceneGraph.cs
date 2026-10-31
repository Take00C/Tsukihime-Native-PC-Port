using System.Globalization;
using System.Text;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Flow;
using Tsuki.Engine.Vm;

namespace Tsuki.Cli.Play;

/// <summary>One choice block of a script: the <c>_GOTS</c> that follows its <c>_SELD</c> and the scene each option value leads to.</summary>
internal sealed record ChoiceBlock(int Script, int GotsOffset, IReadOnlyDictionary<int, int> NextScriptByValue);

/// <summary>
/// The static scene graph of the 549 scripts, as far as the jumps show it: an edge for every <c>_JUMP</c> of a script, and for every choice block the
/// script each option value reaches (the first jump after the option's label). Conditions are ignored, so the graph is an upper bound of what a
/// playthrough can reach; the coverage run uses it to aim at scenes (shortest path from the start) and to explain the ones it cannot reach.
/// </summary>
internal sealed class SceneGraph
{
    private readonly List<(int From, int To)> _edges = [];
    private readonly Dictionary<int, List<int>> _out = [];
    private readonly Dictionary<(int Script, int GotsOffset), ChoiceBlock> _blocks = [];
    private readonly List<ChoiceBlock>[] _blocksOf;

    private SceneGraph(int count)
    {
        _blocksOf = new List<ChoiceBlock>[count];
        for (int i = 0; i < count; i++)
        {
            _blocksOf[i] = [];
        }
    }

    /// <summary>The choice blocks of a script.</summary>
    public IReadOnlyList<ChoiceBlock> BlocksOf(int script) => _blocksOf[script];

    /// <summary>The scripts a script can jump to.</summary>
    public IReadOnlyList<int> Successors(int script) => _out.TryGetValue(script, out List<int>? l) ? l : [];

    /// <summary>The choice block whose <c>_GOTS</c> is the first at or after <paramref name="cursor"/> in <paramref name="script"/>.</summary>
    public ChoiceBlock? BlockAt(int script, int cursor)
    {
        ChoiceBlock? best = null;
        foreach (ChoiceBlock b in _blocksOf[script])
        {
            if (b.GotsOffset >= cursor && (best is null || b.GotsOffset < best.GotsOffset))
            {
                best = b;
            }
        }

        return best;
    }

    /// <summary>Builds the graph over the scripts of <paramref name="scripts"/>.</summary>
    public static SceneGraph Build(IFlowScripts scripts, IGameFlowTables flow)
    {
        var g = new SceneGraph(flow.SceneCount);
        for (int k = 0; k < flow.SceneCount; k++)
        {
            ScriptImage image = scripts.Script(k);
            LabelTable labels = scripts.Labels(k);
            ReadOnlyMemory<byte> bytes = image.Bytes;
            var jumps = new List<(int Offset, int Target)>();
            var gots = new List<(int Offset, List<string> Labels)>();
            int pos = 0;
            while (pos < bytes.Length)
            {
                ScriptSegment seg = ScriptSegmenter.Read(bytes.Span, pos);
                pos = seg.End;
                if (!ScriptSegmenter.TryGetStatement(k, bytes, seg, out Statement st))
                {
                    continue;
                }

                if (st.Name.First == (byte)'J')
                {
                    string[] args = Args(st);
                    if (args.Length > 0 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                    {
                        int to = flow.ScriptOfSceneId(id);
                        if (to >= 0)
                        {
                            jumps.Add((st.Offset, to));
                            g.AddEdge(k, to);
                        }
                    }
                }
                else if (st.Name.ToString() == "GOTS")
                {
                    string[] args = Args(st);
                    gots.Add((st.Offset, [.. args.Skip(1)]));
                }
            }

            foreach ((int offset, List<string> names) in gots)
            {
                var map = new Dictionary<int, int>();
                for (int v = 0; v < names.Count; v++)
                {
                    int at = labels.Find(names[v]);
                    if (at < 0)
                    {
                        continue;
                    }

                    foreach ((int jo, int target) in jumps)
                    {
                        if (jo >= at)
                        {
                            map[v] = target;
                            break;
                        }
                    }
                }

                var block = new ChoiceBlock(k, offset, map);
                g._blocks[(k, offset)] = block;
                g._blocksOf[k].Add(block);
            }
        }

        return g;
    }

    private void AddEdge(int from, int to)
    {
        if (!_out.TryGetValue(from, out List<int>? l))
        {
            _out[from] = l = [];
        }

        if (!l.Contains(to))
        {
            l.Add(to);
            _edges.Add((from, to));
        }
    }

    private static string[] Args(in Statement st)
    {
        ReadOnlySpan<byte> a = st.Args.Span;
        var ranges = new List<ArgRange>(8);
        ArgumentList.Split(a, ArgumentList.StartIn(st), ranges);
        var result = new string[ranges.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = Encoding.Latin1.GetString(a[ranges[i].Start..ranges[i].End]);
        }

        return result;
    }

    /// <summary>The scripts reachable from <paramref name="start"/> by any jump.</summary>
    public HashSet<int> Reachable(int start)
    {
        var seen = new HashSet<int> { start };
        var q = new Queue<int>();
        q.Enqueue(start);
        while (q.Count > 0)
        {
            int s = q.Dequeue();
            foreach (int t in Successors(s))
            {
                if (seen.Add(t))
                {
                    q.Enqueue(t);
                }
            }
        }

        return seen;
    }

    /// <summary>The shortest chain of scripts from <paramref name="start"/> to <paramref name="target"/> (both included), or null.</summary>
    public List<int>? Path(int start, int target, HashSet<int>? avoid = null)
    {
        var prev = new Dictionary<int, int> { [start] = -1 };
        var q = new Queue<int>();
        q.Enqueue(start);
        while (q.Count > 0)
        {
            int s = q.Dequeue();
            if (s == target)
            {
                break;
            }

            foreach (int t in Successors(s))
            {
                if (avoid is not null && avoid.Contains(t) && t != target)
                {
                    continue;
                }

                if (!prev.ContainsKey(t))
                {
                    prev[t] = s;
                    q.Enqueue(t);
                }
            }
        }

        if (!prev.ContainsKey(target))
        {
            return null;
        }

        var path = new List<int>();
        for (int s = target; s != -1; s = prev[s])
        {
            path.Add(s);
        }

        path.Reverse();
        return path;
    }
}
