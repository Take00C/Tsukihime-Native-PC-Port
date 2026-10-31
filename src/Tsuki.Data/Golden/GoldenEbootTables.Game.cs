#if TSUKI_DEV
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Text;

namespace Tsuki.Data.Golden;

// The game-interpreter area of the golden eboot tables (package K3). The slice does not hold the glyph tables in the engine's form or the
// flowchart; the game tests that need them use GameFolderSource.
public sealed partial class GoldenEbootTables
{
    /// <inheritdoc/>
    FontTables IEbootTables.GameFonts => throw new NotSupportedException("the golden slice holds the glyph tables as exported JSON, not as FontTables");

    /// <inheritdoc/>
    IGameFlowTables IEbootTables.GameFlow => throw new NotSupportedException("the golden slice does not hold the flowchart tables");
}
#endif
