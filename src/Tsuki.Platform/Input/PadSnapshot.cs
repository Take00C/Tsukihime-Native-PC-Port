namespace Tsuki.Platform.Input;

/// <summary>
/// The state of the connected gamepads at one instant (merged over all pads). Face buttons are positional, as SDL names them: south is the bottom button
/// (Cross on a PlayStation pad, A on an Xbox pad); axes are SDL's raw values (sticks -32768..32767, triggers 0..32767).
/// </summary>
public readonly record struct PadSnapshot(
    bool South = false, bool East = false, bool West = false, bool North = false,
    bool LeftShoulder = false, bool RightShoulder = false, bool Start = false, bool Back = false,
    bool LeftStick = false, bool RightStick = false, bool Up = false, bool Down = false, bool Left = false, bool Right = false,
    short LeftTrigger = 0, short RightTrigger = 0, short LeftX = 0, short LeftY = 0, short RightX = 0, short RightY = 0)
{
    /// <summary>Combines two snapshots: buttons OR-ed, each axis the one of larger magnitude.</summary>
    public PadSnapshot Merge(PadSnapshot o) => new(
        South || o.South, East || o.East, West || o.West, North || o.North,
        LeftShoulder || o.LeftShoulder, RightShoulder || o.RightShoulder, Start || o.Start, Back || o.Back,
        LeftStick || o.LeftStick, RightStick || o.RightStick, Up || o.Up, Down || o.Down, Left || o.Left, Right || o.Right,
        Big(LeftTrigger, o.LeftTrigger), Big(RightTrigger, o.RightTrigger), Big(LeftX, o.LeftX), Big(LeftY, o.LeftY), Big(RightX, o.RightX), Big(RightY, o.RightY));

    private static short Big(short a, short b) => Math.Abs((int)a) >= Math.Abs((int)b) ? a : b;
}
