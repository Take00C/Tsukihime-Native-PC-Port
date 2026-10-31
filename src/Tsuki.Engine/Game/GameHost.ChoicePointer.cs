using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game.Effects;
using Tsuki.Engine.Vm.Variables;

namespace Tsuki.Engine.Game;

// The mouse on the choice list (package K6): the pointer picks the line under it, the left button decides it through the interpreter's own click rule.
// The line rows are the ones DrawChoice lays out (26.02 units apart on the 640 x 344.7 canvas, 1080 / 344.7 pixels per unit).
public sealed partial class GameHost
{
    /// <summary>The choice line whose row contains canvas row <paramref name="canvasY"/> (1080 rows), or -1.</summary>
    public int ChoiceLineAt(double canvasY)
    {
        if (VarState.ChoiceMode != ChoiceMode.List || _lines.Count == 0)
        {
            return -1;
        }

        double y0 = ChoiceFirstY();
        double rows = ((canvasY / ChoiceScaleY) - y0) / ChoiceView.LinePitch;
        int k = (int)Math.Floor(rows);
        return rows >= 0 && k < _lines.Count ? k : -1;
    }

    /// <summary>
    /// Moves the choice cursor to line <paramref name="line"/> as a mouse hover does: only while the choice accepts a decision, never onto a locked line.
    /// Returns true when the cursor is on that line afterwards.
    /// </summary>
    public bool HoverChoice(int line)
    {
        if (!ChoiceSelectable || VarState.ChoiceMode != ChoiceMode.List || (uint)line >= (uint)_lines.Count || _lines[line].Locked)
        {
            return false;
        }

        SetCursor(line);
        return true;
    }
}
