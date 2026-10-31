// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/game.rs (jump_node, jump_record)
using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Game;

// The flowchart jump (gallery flowchart, system menu): the playthrough is wound back to a node and its scene starts from the top, where the scripts'
// `_IF__(F026,ne,0,...)` blocks set the story state up. The F words the jump keeps and clears come from the PS4 eboot (IGameFlowTables), not from
// knife's Switch tables. The structure of the rewind is knife's; the keep lists, the clear pairs and the node rows are the PS4's.
public sealed partial class GameHost
{
    /// <summary>
    /// Jumps to flowchart node <paramref name="node"/> (an RTST bit): returns false when the node has no scene to start. The F and Z words are
    /// cleared except the kept ones, the stage, sound and messages start fresh, and the scene runs from its top with F026 = its scene id.
    /// </summary>
    public bool JumpToNode(int node)
    {
        foreach (FlowNodeRow row in _flow.Nodes)
        {
            if (row.Scene == node && row.ScriptScene >= 0)
            {
                return JumpToRecord(row.Column, node, row.ScriptScene);
            }
        }

        return false;
    }

    /// <summary>The flowchart jump for the record under the cursor: column <paramref name="column"/>, node <paramref name="node"/>, scene id <paramref name="sceneId"/>.</summary>
    public bool JumpToRecord(int column, int node, int sceneId)
    {
        if (sceneId >= 0 && _flow.ScriptOfSceneId(sceneId) < 0)
        {
            return false;
        }

        // the columns after the target, back to the first chapter-0 column: node and edge bits of the playthrough off
        IReadOnlyList<FlowNodeRow> nodes = _flow.Nodes;
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            FlowNodeRow r = nodes[i];
            if (r.Column <= column || r.Group < 1)
            {
                break;
            }

            if (r.Scene != 0 && Math.Abs(r.Scene) < MaxNodeBit + 1)
            {
                ClearPlayBit(RouteNodeWord, Math.Abs(r.Scene));
            }

            int e = r.Link - FirstEdgeId;
            if (e >= 0 && e < EdgeBits)
            {
                ClearPlayBit(RouteEdgeWord, e);
            }
        }

        // the chapter words the column has not reached yet
        foreach ((int limit, int word) in _flow.JumpClears)
        {
            if (column <= limit)
            {
                SetF(word, 0);
            }
        }

        SetF(110, node);
        SetF(26, sceneId);
        SetF(1103, 1);
        foreach (int w in new[] { 823, 847, 1104, 1105, 1106, 1107, 1108, 1109, 1110, 1111, 1112, 1113 })
        {
            SetF(w, 0);
        }

        if (sceneId < 0)
        {
            return false;
        }

        // the words kept whole, the words kept as a signed byte, the words kept as a flag
        var words = _flow.JumpKeepWords.Select(w => (W: w, V: Vars.GetF(w))).ToList();
        var bytes = _flow.JumpKeepBytes.Select(w => (W: w, V: (ushort)(short)(sbyte)(byte)Vars.GetF(w))).ToList();
        var bits = _flow.JumpKeepBits.Where(w => Vars.GetF(w) != 0).ToList();
        Profile.ClearPlaythrough();
        ResetRuntime();
        foreach ((int w, ushort v) in words.Concat(bytes))
        {
            Vars.SetF(w, v);
        }

        foreach (int w in bits)
        {
            Vars.SetF(w, 1);
        }

        // F026 is on the list of signed bytes, which would cut a scene id to its low byte (372 -> 116, 512 -> 0, and the jump-entry blocks only test
        // F026 != 0): the jump target is set again here, whole
        SetF(110, node);
        SetF(26, sceneId);

        if (!EnterScene(sceneId, null, jump: false))
        {
            Phase = GamePhase.Ended;
            EndReason = EndReason.MissingScene;
            return false;
        }

        return true;
    }

    private void ClearPlayBit(int firstWord, int bit)
    {
        int w = firstWord + (bit >> 3);
        Vars.SetF(w, (ushort)(Vars.GetF(w) & ~(1 << (bit & 7))));
    }
}
