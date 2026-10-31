using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;

namespace Tsuki.Engine.Game;

// The flowchart marks of the progress commands (docs/formats/gallery.md 2.1): _RDST / _RTST / _RDED / _RDAS and the edge marks of a _JUMP.
// scene_seen = G110.., scene_done = G300.., edge_seen = G400.. (all in the variable block, so they persist with the G words); the per-playthrough
// copies are F201.. (node bits, 8 per word) and F600.. (edge bits).
public sealed partial class GameHost
{
    private const int SceneSeenWord = 110;
    private const int SceneDoneWord = 300;
    private const int EdgeSeenWord = 400;
    private const int RouteNodeWord = 201;
    private const int RouteEdgeWord = 600;
    private const int MaxNodeBit = 0x23F;
    private const int FirstEdgeId = 564;
    private const int EdgeBits = 704;

    private bool _staffTask;

    private void ResetMarks()
    {
        _staffTask = false;
    }

    private bool GetBit(int firstWord, int bit) => (Vars.GetG(firstWord + (bit >> 4)) >> (bit & 15) & 1) != 0;

    private void SetBit(int firstWord, int bit)
    {
        int w = firstWord + (bit >> 4);
        Vars.SetG(w, (ushort)(Vars.GetG(w) | (1 << (bit & 15))));
    }

    private bool PlayBit(int firstWord, int bit) => (Vars.GetF(firstWord + (bit >> 3)) >> (bit & 7) & 1) != 0;

    private void SetPlayBit(int firstWord, int bit)
    {
        int w = firstWord + (bit >> 3);
        Vars.SetF(w, (ushort)(Vars.GetF(w) | (1 << (bit & 7))));
    }

    /// <summary>scene_seen(n): the node was reached in any playthrough (<c>_RDST</c>).</summary>
    public bool IsSceneReached(int n) => n is >= 0 and <= MaxNodeBit && GetBit(SceneSeenWord, n);

    /// <summary>scene_done(n): the node was completed (<c>_RDED</c>).</summary>
    public bool IsSceneDone(int n) => n is >= 0 and <= MaxNodeBit && GetBit(SceneDoneWord, n);

    /// <summary>edge_seen(id) for an edge id 564..1267.</summary>
    public bool IsEdgeSeen(int id) => id >= FirstEdgeId && id < FirstEdgeId + EdgeBits && GetBit(EdgeSeenWord, id - FirstEdgeId);

    private bool AllEndingsDone()
    {
        for (int n = 533; n <= 556; n++)
        {
            if (!IsSceneDone(n))
            {
                return false;
            }
        }

        return true;
    }

    private StepResult CmdRdst(in Statement st)
    {
        int n = new StageArgs(st).Expr(0);
        if (n is >= 0 and <= MaxNodeBit)
        {
            SetBit(SceneSeenWord, n);
        }

        return StepResult.Continue;
    }

    private StepResult CmdRtst(in Statement st)
    {
        int n = new StageArgs(st).Expr(0);
        if (n is >= 0 and <= MaxNodeBit)
        {
            SetPlayBit(RouteNodeWord, n);
            if (n is >= 533 and <= 557 || F(24) != 0)
            {
                SetF(110, n);
            }
        }

        return StepResult.Continue;
    }

    private StepResult CmdRded(in Statement st)
    {
        int n = new StageArgs(st).Expr(0);
        if (n is >= 0 and <= MaxNodeBit && n == F(110) && PlayBit(RouteNodeWord, n))
        {
            SetBit(SceneDoneWord, n);
            EndSceneSkip();
            NodeCompleted?.Invoke(n);
            if (ArchiveMode)
            {
                // a gallery replay ends at the _RDED of its node (the gallery reopens on the replayed entry)
                return ScriptEnd(EndReason.ReturnToTitle, keepBgm: false);
            }
        }

        return StepResult.Continue;
    }

    private StepResult CmdRdas(in Statement st)
    {
        int n = new StageArgs(st).Expr(0);
        if (n is >= 0 and <= MaxNodeBit && !ArchiveMode && IsSceneDone(n) && Config.AutoSkipCleared != 0)
        {
            AutoSkipOffered?.Invoke(n);
        }

        return StepResult.Continue;
    }

    /// <summary>Raised when <c>_RDED</c> completes a node.</summary>
    public event Action<int>? NodeCompleted;

    /// <summary>Raised when <c>_RDAS</c> offers to skip an already completed scene (the mode panel asks the player; the interpreter does not).</summary>
    public event Action<int>? AutoSkipOffered;

    /// <summary>
    /// The edge marks of a <c>_JUMP</c> from script <paramref name="fromScript"/> to scene id <paramref name="toSceneId"/> (gallery.md 2.1: the PS4
    /// code at VA 0xBA3C0 sets the edge flags of the node pair and F110 = the target node).
    /// </summary>
    private void MarkSceneJump(int fromScript, int toSceneId)
    {
        int from = _flow.NodeOfScript(fromScript);
        int toScript = _flow.ScriptOfSceneId(toSceneId);
        int to = toScript < 0 ? -1 : _flow.NodeOfScript(toScript);
        if (to >= 1 && from is >= 1 and <= 530)
        {
            foreach (FlowEdgeRow e in _flow.Edges)
            {
                if (e.From == from && e.To == to)
                {
                    foreach (int id in e.GlobalIds)
                    {
                        if (id >= FirstEdgeId && id - FirstEdgeId < EdgeBits)
                        {
                            SetBit(EdgeSeenWord, id - FirstEdgeId);
                        }
                    }

                    foreach (int id in e.PlaythroughIds)
                    {
                        if (id >= FirstEdgeId && id - FirstEdgeId < EdgeBits)
                        {
                            SetPlayBit(RouteEdgeWord, id - FirstEdgeId);
                        }
                    }

                    break;
                }
            }

            SetF(110, to);
        }
    }

    // -- the staff roll task ------------------------------------------------------------------------------------------------------

    private StepResult CmdXstp()
    {
        _staffTask = true;
        SetF(36, 1);
        return StepResult.Continue;
    }

    private StepResult CmdXrsm()
    {
        if (_staffTask)
        {
            _staffTask = false;
            SetF(36, 0);
        }

        return StepResult.Continue;
    }
}
