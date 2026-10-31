#if TSUKI_DEV
using Tsuki.Engine.Contracts;

namespace Tsuki.Data.Golden;

// The menu area of the golden eboot tables (package K5a). The oracle's export holds no UI tables, so the UI is built over GameFolderSource only.
public sealed partial class GoldenEbootTables
{
    /// <inheritdoc/>
    IUiTables Tsuki.Engine.Contracts.IEbootTables.Ui => throw new NotSupportedException("the golden slice does not hold the UI tables");

    /// <inheritdoc/>
    IUiGalleryTables? Tsuki.Engine.Contracts.IEbootTables.Gallery => null;
}
#endif
