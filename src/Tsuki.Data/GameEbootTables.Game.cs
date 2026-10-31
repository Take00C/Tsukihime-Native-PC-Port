using Tsuki.Data.Eboot.Tables;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Data;

// The game-interpreter area of the eboot tables (package K3): the font tables of the text layout and the scene / flowchart data of _JUMP.
public sealed partial class GameEbootTables
{
    private EngineTables? _gameEngineTables;
    private IGameFlowTables? _gameFlow;

    private EngineTables GameEngineTables => LazyInitializer.EnsureInitialized(ref _gameEngineTables, () => new EngineTables(Reader))!;

    /// <inheritdoc/>
    FontTables IEbootTables.GameFonts => FontTables;

    /// <inheritdoc/>
    IGameFlowTables IEbootTables.GameFlow => LazyInitializer.EnsureInitialized(ref _gameFlow, () => new GameFlowTables(SceneIds, GameEngineTables, Catalog))!;

    private sealed class GameFlowTables : IGameFlowTables
    {
        private readonly Scripts.SceneTable _scenes;
        private readonly EngineTables _tables;
        private readonly Dictionary<int, int> _scriptOfId = [];
        private readonly Lazy<IReadOnlyList<FlowEdgeRow>> _edges;
        private readonly Lazy<IReadOnlyList<FlowNodeRow>> _nodes;
        private readonly Lazy<IReadOnlyList<(int, int)>> _clears;

        private readonly Catalog.GameCatalog _catalog;
        private readonly Lazy<(Dictionary<string, int> ByName, Dictionary<int, int> SeenMap)> _seen;

        public GameFlowTables(Scripts.SceneTable scenes, EngineTables tables, Catalog.GameCatalog catalog)
        {
            _scenes = scenes;
            _tables = tables;
            _catalog = catalog;
            _seen = new(() =>
            {
                var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < catalog.PictureNames.Count; i++)
                {
                    byName.TryAdd(catalog.PictureNames[i], i);
                }

                var map = new Dictionary<int, int>();
                foreach (SeenMapEntry e in tables.Gallery.SeenMap)
                {
                    if (byName.TryGetValue(e.Name, out int id))
                    {
                        map[id] = e.SeenIndex;
                    }
                }

                return (byName, map);
            });
            for (int k = 0; k < scenes.Count; k++)
            {
                _scriptOfId.TryAdd(scenes.Ids[k], k);
            }

            _edges = new(() => [.. tables.Flowchart.Edges.Select(e => new FlowEdgeRow(e.From, e.To, e.GlobalIds, e.PlaythroughIds))]);
            _nodes = new(() => [.. tables.Flowchart.Nodes.Select(n => new FlowNodeRow(n.Column, n.Scene, n.ScriptScene, n.Link, tables.Flowchart.Columns[n.Column].Group))]);
            _clears = new(() => tables.JumpClears.Pairs);
        }

        public int SceneCount => _scenes.Count;

        public int SceneId(int script) => _scenes.Ids[script];

        public int ScriptOfSceneId(int sceneId) => _scriptOfId.TryGetValue(sceneId, out int k) ? k : -1;

        public int NodeOfScript(int script) => _tables.SceneNodes.NodeOfScriptIndex(script);

        public int PictureSeenIndex(int pictureId)
        {
            IReadOnlyList<Catalog.CatalogEntry> entries = _catalog.Images.Entries;
            if (pictureId < 0 || pictureId >= entries.Count)
            {
                return -1;
            }

            (Dictionary<string, int> byName, Dictionary<int, int> map) = _seen.Value;
            int pid = pictureId;
            if (entries[pid].IsVariant)
            {
                string name = _catalog.PictureNames[pid];
                pid = name.Length > 6 && byName.TryGetValue(name[..^6], out int b) ? b : -1;
            }

            if (pid >= 0xA8D)
            {
                pid = map.GetValueOrDefault(pid, -1);
            }

            return pid is >= 0 and <= 0xC7F ? pid : -1;
        }

        public IReadOnlyList<FlowEdgeRow> Edges => _edges.Value;

        public IReadOnlyList<FlowNodeRow> Nodes => _nodes.Value;

        public IReadOnlyList<int> JumpKeepWords => _tables.FlowKeep.Words;

        public IReadOnlyList<int> JumpKeepBytes => _tables.FlowKeep.Bytes;

        public IReadOnlyList<int> JumpKeepBits => _tables.FlowKeep.Bits;

        public IReadOnlyList<(int LastColumn, int Word)> JumpClears => _clears.Value;
    }
}
