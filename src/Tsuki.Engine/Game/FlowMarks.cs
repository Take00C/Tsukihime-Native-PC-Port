using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Game;

/// <summary>
/// The flowchart marks of a scene <c>_JUMP</c> and of a <c>_SELD</c> (docs/formats/gallery.md 5.3), ported from the PS4 code statement by statement (package K44):
/// <list type="bullet">
/// <item>a jump (VA 0xBA690): <c>from</c> = the node of the script being left (0xB8840, text 0x228EF0), <c>to</c> = the node of the target scene id (0xB8870);
/// the special pairs of 0xB9460 (a 500-case switch: hidden nodes 498..530 picked by the last choice or by route bits, pairs that keep F110, a few extra edge bits);
/// otherwise, for <c>to &gt; 0</c> and <c>from</c> 1..530, the first edge record of the pair (0xBA3C0) and F110 = <c>to</c>;</item>
/// <item>a choice list (VA 0xB8620, called by <c>_SELD</c> when it opens, before the player picks): the three edge ids of the choice point (text 0x2214E0) and
/// the bad-end nodes of choice points 57, 67, 72 and 73.</item>
/// </list>
/// Verified against the PS4 functions run under unicorn (<c>tools/tsuki/flow_marks_emu.py</c>, <c>FlowMarksOracleTests</c>). The offsets below are the PS4's
/// byte offsets into var_F (0x164B1D0) and var_G (0x164C620); <see cref="Vars"/> keeps both words in the same order. The trophy check the PS4 runs after
/// marking a hidden node (0xB7CC0) is left out: the port has no trophies.
/// </summary>
public static class FlowMarks
{
    private const int SceneSeenByte = 0xDC;     // G110..
    private const int SceneDoneByte = 0x258;    // G300..
    private const int EdgeSeenByte = 0x320;     // G400..
    private const int RouteNodeByte = 0x192;    // F201.., 8 bits per word
    private const int RouteEdgeByte = 0x4B0;    // F600.., 8 bits per word
    private const int MaxNode = 0x23F;
    private const int FirstEdge = 564;
    private const int LastEdge = 1267;

    /// <summary>The marks of a <c>_JUMP</c> from script <paramref name="script"/> to scene id <paramref name="sceneId"/> (VA 0xBA690).</summary>
    /// <param name="vars">The variable block.</param>
    /// <param name="flow">The scene and edge tables.</param>
    /// <param name="script">The script being left (its allscr index).</param>
    /// <param name="sceneId">The <c>_JUMP</c> target.</param>
    /// <param name="pick">The cursor of the last decided choice (word 0x17506A6).</param>
    public static void Jump(Vars vars, IGameFlowTables flow, int script, int sceneId, int pick)
    {
        ArgumentNullException.ThrowIfNull(flow);
        int to = flow.ScriptOfSceneId(sceneId) is >= 0 and var k ? flow.NodeOfScript(k) : -1;
        JumpNodes(vars, flow.Edges, flow.NodeOfScript(script), to, pick);
    }

    /// <summary>The marks of a jump from node <paramref name="from"/> to node <paramref name="to"/> (0xBA690 after its two lookups).</summary>
    /// <param name="vars">The variable block.</param>
    /// <param name="edges">The edge table.</param>
    /// <param name="from">The node of the script being left, -1 none.</param>
    /// <param name="to">The node of the target, -1 none.</param>
    /// <param name="pick">The cursor of the last decided choice.</param>
    public static void JumpNodes(Vars vars, IReadOnlyList<FlowEdgeRow> edges, int from, int to, int pick)
    {
        ArgumentNullException.ThrowIfNull(vars);
        ArgumentNullException.ThrowIfNull(edges);
        var m = new Marker(vars, edges);
        if (!m.Special(from, to, pick) && to > 0 && from is >= 1 and <= 0x212)
        {
            m.Edge(from, to);
        }
    }

    /// <summary>The marks of a choice list (VA 0xB8620): <c>_SELD</c> argument 1, the choice point 1..82.</summary>
    /// <param name="vars">The variable block.</param>
    /// <param name="choiceEdges">The three edge ids of each choice point (index = choice point; -1 none).</param>
    /// <param name="choice">The choice point.</param>
    public static void Choice(Vars vars, IReadOnlyList<(int A, int B, int C)> choiceEdges, int choice)
    {
        ArgumentNullException.ThrowIfNull(vars);
        ArgumentNullException.ThrowIfNull(choiceEdges);
        if (choice is < 1 or > 0x52 || choice >= choiceEdges.Count)
        {
            return;
        }

        var m = new Marker(vars, []);
        (int a, int b, int c) = choiceEdges[choice];
        m.EdgeBit(a);
        if (b > 0)
        {
            m.EdgeBit(b);
            m.EdgeBit(c);
        }

        switch (choice)
        {
            case 57:
                m.HiddenByChoice(512, 0x11C, 1, 0x212);
                m.OrG(0x345, 0x60);
                break;
            case 67:
                m.HiddenByChoice(513, 0x11C, 2, 0x212);
                m.OrG(0x353, 0x60);
                m.OrG(0x348, 0x1C);
                break;
            case 72:
                m.HiddenByChoice(523, 0x11D, 8, 0x214);
                m.OrGWord(0x36A, (ushort)(((m.FByte(0x1D4) & 0x40) == 0 ? 0x1000 : 0) | 0x800));
                break;
            case 73:
                m.HiddenByChoice(517, 0x11C, 0x20, 0x212);
                m.OrGWord(0x364, (ushort)((m.FByte(0x200) & 0x40) != 0 ? 0x800 : 0x1800));
                break;
        }
    }

    // The PS4 code writes bytes and words of var_F / var_G directly; these helpers keep its offsets.
    private readonly struct Marker
    {
        private readonly Vars _v;
        private readonly IReadOnlyList<FlowEdgeRow> _edges;

        public Marker(Vars vars, IReadOnlyList<FlowEdgeRow> edges)
        {
            _v = vars;
            _edges = edges;
        }

        public int FByte(int off) => (_v.GetF(off >> 1) >> ((off & 1) * 8)) & 0xFF;

        private int FWord(int off) => _v.GetF(off >> 1);

        public void OrG(int off, int mask) => _v.SetG(off >> 1, (ushort)(_v.GetG(off >> 1) | (mask << ((off & 1) * 8))));

        public void OrGWord(int off, ushort mask) => _v.SetG(off >> 1, (ushort)(_v.GetG(off >> 1) | mask));

        private void OrF(int off, int mask) => _v.SetF(off >> 1, (ushort)(_v.GetF(off >> 1) | (mask << ((off & 1) * 8))));

        private void SetF110(int n) => _v.SetF(110, (ushort)n);

        /// <summary>One edge id into edge_seen (0xB8620 / 0xBA3C0: ids outside 564..1267 are skipped).</summary>
        public void EdgeBit(int id)
        {
            if (id is >= FirstEdge and <= LastEdge)
            {
                int i = id - FirstEdge;
                OrGWord(EdgeSeenByte + (2 * (i >> 4)), (ushort)(1 << (i & 15)));
            }
        }

        private void RouteEdgeBit(int id)
        {
            if (id is >= FirstEdge and <= LastEdge)
            {
                int i = id - FirstEdge;
                _v.SetF((RouteEdgeByte >> 1) + (i >> 3), (ushort)(_v.GetF((RouteEdgeByte >> 1) + (i >> 3)) | (1 << (i & 7))));
            }
        }

        /// <summary>
        /// 0xBA3C0: the first edge record of (<paramref name="from"/>, <paramref name="to"/>) sets its ids in edge_seen and in the playthrough's edge bits, and
        /// F110 becomes <paramref name="to"/> whether a record matched or not. The records' ids are the positive ones in order, which is what the PS4's
        /// "stop at the first id &lt;= 0, the fifth only for record 455" loop reads on the retail table (checked by the oracle test).
        /// </summary>
        public void Edge(int from, int to)
        {
            foreach (FlowEdgeRow e in _edges)
            {
                if (e.From == from && e.To == to)
                {
                    foreach (int id in e.GlobalIds)
                    {
                        EdgeBit(id);
                    }

                    foreach (int id in e.PlaythroughIds)
                    {
                        RouteEdgeBit(id);
                    }

                    break;
                }
            }

            SetF110(to);
        }

        /// <summary>0xB85A0: hidden node <paramref name="n"/> is seen, reached in this playthrough and done; F110 = n for 533..557 or while F024 is set.</summary>
        private void Hidden(int n)
        {
            if ((uint)n > MaxNode)
            {
                return;
            }

            _v.SetG(110 + (n >> 4), (ushort)(_v.GetG(110 + (n >> 4)) | (1 << (n & 15))));
            _v.SetF(201 + (n >> 3), (ushort)(_v.GetF(201 + (n >> 3)) | (1 << (n & 7))));
            if (n is >= 0x215 and <= 0x22D)
            {
                SetF110(n);
            }

            if (_v.GetF(24) != 0)
            {
                SetF110(n);
            }

            _v.SetG(300 + (n >> 4), (ushort)(_v.GetG(300 + (n >> 4)) | (1 << (n & 15))));
        }

        /// <summary>The hidden-node marks of 0xB86FC.. (choice points 57, 67, 72, 73): seen, reached, F110 while F024 is set, done (no 533..557 rule).</summary>
        public void HiddenByChoice(int n, int seenByte, int mask, int routeByte)
        {
            OrG(seenByte, mask);
            OrF(routeByte, mask);
            if (_v.GetF(24) != 0)
            {
                SetF110(n);
            }

            OrG(seenByte - SceneSeenByte + SceneDoneByte, mask);
        }

        // hidden node n between from and to: n is marked, then the edges from -> n -> to
        private bool Via(int from, int n, int to)
        {
            Hidden(n);
            Edge(from, n);
            Edge(n, to);
            return true;
        }

        /// <summary>0xB9460: true when the pair is handled here (no plain edge follows).</summary>
        public bool Special(int from, int to, int pick)
        {
            switch (from)
            {
                case 275 or 276:
                    return to == 521;
                case 279 or 280:
                    return to == 1196;
                case 446 or 447:
                    return to == 517;
                case 270 or 483 or 905:
                    return to == 523;
                case 24:
                    return to == 371;
                case 51:
                    if (to != 69)
                    {
                        return false;
                    }

                    Hidden(519);
                    OrG(0x351, 8);
                    Edge(51, 519);
                    Edge(519, 69);
                    return true;
                case 52:
                    return to == 312 && Pair(66, 312);
                case 55:
                    return to == 58 && (FByte(0x1A0) & 2) != 0 && Pair(57, 58);
                case 57:
                    return to == 55;
                case 66:
                    if (to == 53)
                    {
                        return Via(66, 530, 53);
                    }

                    return to == 52 && FWord(0x64) != 0;
                case 68 or 311:
                    return to == 67;
                case 69:
                    return to == 311 && Pair(519, 311);
                case 98:
                    return to == 203 && Pair(202, 203);
                case 101:
                    return to == 102 && Via(101, pick == 1 ? 502 : pick != 0 ? 503 : 501, 102);
                case 105:
                    return to is 104 or 204;
                case 108:
                    return to == 322;
                case 136:
                    if (to == 137)
                    {
                        OrG(0x335, 4);
                    }

                    return false;
                case 149:
                    return to == 341 && (FByte(0x1B8) & 1) != 0 && Pair(152, 341);
                case 152:
                    return to == 149;
                case 166:
                    return to == 342 && Via(166, pick == 0 ? 504 : 505, 342);
                case 169:
                    return to == 323 && (FByte(0x1BC) & 1) != 0 && Pair(168, 323);
                case 181:
                    if (to != 184)
                    {
                        return false;
                    }

                    Hidden(520);
                    Edge(181, 520);
                    if (FWord(0x6C) != 0)
                    {
                        OrG(0x351, 0x40);
                        Edge(520, 184);
                    }

                    return true;
                case 184:
                    return to == 314 && Pair(520, 314);
                case 202:
                    return to == 98;
                case 204:
                    return to == 207 && (FByte(0x1C4) & 0x20) != 0 && Pair(205, 207);
                case 205:
                    return to == 105;
                case 213:
                    if (to == 215)
                    {
                        OrG(0x342, 2);
                    }

                    return false;
                case 250:
                    return to == 257;
                case 258:
                    return to == 256;
                case 261:
                    if (to == 262)
                    {
                        OrG(0x36B, 1);
                    }

                    return false;
                case 269:
                    if (to == 483)
                    {
                        return Via(269, 514, 483);
                    }

                    if (to != 270)
                    {
                        return false;
                    }

                    OrG(0x34A, 0x20);
                    return Via(269, 514, 270);
                case 274:
                    if (to == 276)
                    {
                        return Via(274, 516, 276);
                    }

                    if (to != 275)
                    {
                        return false;
                    }

                    Via(274, 516, 275);
                    OrG(0x36A, 0x80);
                    return true;
                case 277 or 278:
                    if (to == 280)
                    {
                        return Via(from, 518, 280);
                    }

                    if (to != 279)
                    {
                        return false;
                    }

                    Via(from, 518, 279);
                    OrG(0x36A, 0x20);
                    return true;
                case 322:
                    return to == 112 && (FByte(0x1AC) & 0x10) != 0 && Pair(108, 112);
                case 324:
                    return to == 329 && (FByte(0x1D6) & 1) != 0;
                case 329:
                    if (to != 274)
                    {
                        return false;
                    }

                    if ((FByte(0x1D4) & 0x80) != 0)
                    {
                        return Pair(271, 274);
                    }

                    return (FByte(0x1D6) & 1) != 0 && Pair(272, 274);
                case 331:
                    if (to == 330)
                    {
                        // node 522 seen and done (no playthrough bit, no F110), edge 1158
                        OrG(0x11D, 4);
                        OrG(0x299, 4);
                        OrG(0x36A, 4);
                    }

                    return false;
                case 355:
                    if (to == 20)
                    {
                        OrG(0x33D, 8);
                    }

                    return false;
                case 371:
                    return to == 24;
                case 478:
                    if (to == 331)
                    {
                        OrG(0x36A, 2);
                        return false;
                    }

                    return to == 330 && Via(478, 522, 330);
                case 486:
                    return to == 1 && Via(486, 529, 1);
                case 517:
                    if (to is not (278 or 277))
                    {
                        return false;
                    }

                    Hidden(517);
                    Edge((FByte(0x200) & 0x40) == 0 ? 447 : 446, 517);
                    Edge(517, to);
                    return true;
                case 518:
                    return to is 279 or 280;
                case 523:
                    if (to == 515)
                    {
                        Hidden(523);
                        Hidden(515);
                        if ((FByte(0x1D4) & 0x40) != 0)
                        {
                            Edge(270, 523);
                            OrG(0x36B, 4);
                        }
                        else
                        {
                            Edge(483, 523);
                        }

                        return Pair(523, 515);
                    }

                    if (to != 271)
                    {
                        return false;
                    }

                    Hidden(523);
                    Edge((FByte(0x1D4) & 0x40) != 0 ? 270 : 483, 523);
                    return Pair(523, 271);
                case 67:
                    if (to != 66)
                    {
                        return false;
                    }

                    if ((FByte(0x1A2) & 0x10) != 0)
                    {
                        return Pair(68, 66);
                    }

                    return (FByte(0x1DE) & 0x80) != 0 && Pair(311, 66);
                case 104:
                    return to == 107 && (FByte(0x1AC) & 2) != 0 && Pair(105, 107);
                case 126:
                    return to == 129 && (FByte(0x1B0) & 0x80) != 0 && Pair(127, 129);
                case 127:
                    return to == 126;
                case 172:
                    return to == 11 && Via(172, 506, 11);
                case 221:
                    return to == 222 && Via(221, pick == 1 ? 510 : pick != 0 ? 511 : 509, 222);
                case 234:
                    return to is 236 or 235 && Via(234, 512, to);
                case 251:
                    return to is 252 or 253 or 254 or 325 or 326 && Via(251, 513, to);
                case 256:
                    return to == 259 && (FByte(0x1D2) & 4) != 0 && Pair(258, 259);
                case 312:
                    if (to == 493)
                    {
                        OrG(0x36E, 4);
                    }

                    return false;
                case 369:
                    return to == 29 && Via(369, pick == 1 ? 499 : pick != 0 ? 500 : 498, 29);
                case 410:
                    return to == 220 && Via(410, pick == 0 ? 507 : 508, 220);
                case 439:
                    if (to == 327)
                    {
                        OrG(0x354, 1);
                    }

                    return false;
                case 463:
                    if (to != 300)
                    {
                        return false;
                    }

                    Hidden(525);
                    Hidden(526);
                    Edge(463, 525);
                    Edge(525, 526);
                    return Pair(526, 300);
                case 480:
                    if (to != 309)
                    {
                        return false;
                    }

                    Hidden(524);
                    Hidden(527);
                    Edge(480, 524);
                    Edge(524, 527);
                    return Pair(527, 309);
                case 521:
                    if (to is not (447 or 446))
                    {
                        return false;
                    }

                    Hidden(521);
                    if ((FByte(0x1D6) & 8) != 0)
                    {
                        Edge(275, 521);
                        if (to == 446)
                        {
                            OrG(0x36A, 0x40);
                        }
                    }
                    else
                    {
                        Edge(276, 521);
                    }

                    return Pair(521, to);
                case 87 or 168 or 252 or 253 or 271 or 272 or 325 or 326 or 515:
                    return false;
                case 1196:
                    return to == 281 && Pair((FByte(0x1D6) & 0x80) != 0 ? 279 : 280, 281);
                default:
                    // every other node (and none): the prologue's second half is entered through 485 and 528
                    if (to != 486)
                    {
                        return false;
                    }

                    Hidden(528);
                    Hidden(485);
                    Edge(485, 528);
                    return Pair(528, 486);
            }
        }

        // the edge of another pair instead of (from, to)
        private bool Pair(int from, int to)
        {
            Edge(from, to);
            return true;
        }
    }
}
