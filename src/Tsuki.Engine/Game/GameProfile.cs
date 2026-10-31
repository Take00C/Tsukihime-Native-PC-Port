using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Game;

/// <summary>
/// The state that outlives a playthrough: the system block (config, read tracking, picture and music unlocks, choices taken, the first 500 G
/// words) and the variable block whose G words (G0..G1999) the story reads and writes. A new game clears the F and Z words and keeps both
/// (adv_state.md 7.5, gallery.md 2; vm_variables.md). Save and load (package K4) read and write these blocks; the interpreter only mutates them.
/// </summary>
public sealed class GameProfile
{
    /// <summary>Creates a profile around existing blocks.</summary>
    public GameProfile(SystemBlock system, Vars vars)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(vars);
        System = system;
        Vars = vars;
    }

    /// <summary>The system block.</summary>
    public SystemBlock System { get; }

    /// <summary>The F / Z / G words.</summary>
    public Vars Vars { get; }

    /// <summary>A fresh profile: the boot config (language set), zero unlocks, zero variables (the first start of the game).</summary>
    public static GameProfile CreateNew(Language language)
    {
        var system = new SystemBlock { Magic = SystemBlock.MagicValue };
        system.Config.CopyFrom(ConfigBlock.CreateBootDefaults());
        system.Config.Language = (short)language;
        return new GameProfile(system, new Vars());
    }

    /// <summary>Clears the F and Z words (a new game); the G words stay.</summary>
    public void ClearPlaythrough()
    {
        Vars.Bytes[..(2 * Vars.GWord)].Clear();
    }

    /// <summary>Copies G0..G499 of the variable block into the system block (what a system save stores).</summary>
    public void SyncToSystem()
    {
        for (int i = 0; i < SystemBlock.GCount; i++)
        {
            System.SetG(i, Vars.GetG(i));
        }
    }

    /// <summary>Copies G0..G499 of the system block into the variable block (what loading the system data does, gallery.md 2.1).</summary>
    public void SyncFromSystem()
    {
        for (int i = 0; i < SystemBlock.GCount; i++)
        {
            Vars.SetG(i, System.GetG(i));
        }
    }
}
