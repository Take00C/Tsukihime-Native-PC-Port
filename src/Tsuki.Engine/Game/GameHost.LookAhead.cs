using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;
using Tsuki.Engine.Vm;

namespace Tsuki.Engine.Game;

// The picture look-ahead (package K8; knife engine-writeup: "after each dispatch the engine looks ahead 24 commands and queues the images they name for
// loading, so an STCH rarely has to wait for its picture"): the statements after the interpreter's position are read for the pictures they put on layers,
// and the tiles of those pictures are named to the texture loader before the stage commits them, so a scene change finds them decoded.
public sealed partial class GameHost
{
    private const int LookAheadStatements = 400;
    private const int LookAheadMessages = 10;
    private int _lookScript = -1;
    private int _lookOffset = -1;
    private readonly HashSet<(int Pic, int Key)> _lookDone = [];

    /// <summary>
    /// Appends to <paramref name="into"/> the picture tiles that the next statements will need (the pictures of <c>_STCH</c> / <c>_SCH2</c> up to the third message
    /// ahead or 160 statements), without the colour grade a later <c>_STEC</c> may add. Does nothing when the interpreter has not moved since the last call.
    /// </summary>
    public void LookAhead(List<TextureRef> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (Phase != GamePhase.Running)
        {
            return;
        }

        ScriptPosition at = _vm.Position;
        if (at.Script == _lookScript && at.Offset == _lookOffset)
        {
            return;
        }

        if (at.Script != _lookScript || _lookDone.Count > 400)
        {
            _lookDone.Clear();
        }

        _lookScript = at.Script;
        _lookOffset = at.Offset;
        ReadOnlyMemory<byte> bytes = _vm.Image.Bytes;
        int pos = at.Offset;
        int messages = 0;
        StageRuntime.LookAheadScan scan = Stage.BeginLookAhead();
        for (int n = 0; n < LookAheadStatements && pos < bytes.Length && messages < LookAheadMessages; n++)
        {
            ScriptSegment seg = ScriptSegmenter.Read(bytes.Span, pos);
            pos = seg.End;
            if (!ScriptSegmenter.TryGetStatement(at.Script, bytes, seg, out Statement st))
            {
                continue;
            }

            if (GameCommands.Classify(st.Name) == Cmd.ZoneMessage)
            {
                messages++;
            }
            else if (scan.Feed(st))
            {
                scan.Flush(into, _lookDone);                // a commit: the layers' pictures with the grade they hold
            }
        }

        scan.Flush(into, _lookDone);
    }
}
