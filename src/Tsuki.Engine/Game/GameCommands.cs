using System.Collections.Frozen;
using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Game;

/// <summary>The command families the game host dispatches on (docs/formats/script.md 3.6, 4).</summary>
public enum Cmd : byte
{
    /// <summary>Not a command of the PS4 interpreter (the engine skips it).</summary>
    Unknown = 0,

    // flow
    Jump, Rtm, Rtmn, Rtm2, End, Ifsc, Ifur, Ifla, Ifjp, Ifp4, Ifnx, Iff, Els, Eif, Nop, Atsv, Rdst, Rtst, Rded, Rdas, Trpy, Xstp, Xrsm,

    // messages and the window
    ZoneMessage, ZoneDisabled, Msad, Wnty, Pgst, Clo2, Clo3, Clo4, Clo5, Wttm, Wtsv, Wttr,

    // choices
    Selr, Seld, Sels, Sela,

    // sound and movies
    Sound, Vcwt, Tomp, Tomw,

    // stage family handled by the stage runtime
    Stage,

    // screen effects of the host
    Fcln, Fcsp, Flnt, Flop, Flsp, Fccl, Fade, Stcg, Spfc, Sprd, Sprc, Stpv,

    // accepted, not implemented by the PS4 engine or not used by any script
    Ignored,
}

/// <summary>
/// The command-name table of the PS4 main interpreter: the 110 names plus <c>J*</c> and <c>Z*</c> of <c>ENGINE_MAIN</c>
/// (docs/formats/script.md 3.6; tools/tsuki/script_tables.py) and the six names the engine skips (<c>SNX4 FANX IFP4 DMMY STQK NOP_</c>).
/// </summary>
public static class GameCommands
{
    private static readonly FrozenDictionary<uint, Cmd> Table = Build();

    /// <summary>The family of a command name; <see cref="Cmd.Unknown"/> for a name the engine ignores.</summary>
    public static Cmd Classify(CommandName name)
    {
        byte first = name.First;
        if (first == (byte)'J')
        {
            return Cmd.Jump;
        }

        if (first == (byte)'Z')
        {
            return ((name.Value >> 16) & 0xFF) switch
            {
                (byte)'M' => Cmd.ZoneMessage,
                (byte)'D' => Cmd.ZoneDisabled,
                _ => Cmd.Ignored,
            };
        }

        return Table.GetValueOrDefault(name.Value, Cmd.Unknown);
    }

    /// <summary>All names the table knows (for tests).</summary>
    public static IEnumerable<string> KnownNames => Names().Select(t => t.Name);

    private static IEnumerable<(string Name, Cmd Cmd)> Names()
    {
        foreach (string n in new[] { "JUMP" })
        {
            yield return (n, Cmd.Jump);
        }

        (string, Cmd)[] one =
        [
            ("RTM_", Cmd.Rtm), ("RTMN", Cmd.Rtmn), ("RTM2", Cmd.Rtm2), ("END_", Cmd.End), ("IFSC", Cmd.Ifsc), ("IFUR", Cmd.Ifur),
            ("IFLA", Cmd.Ifla), ("IFJP", Cmd.Ifjp), ("IFP4", Cmd.Ifp4), ("IFNX", Cmd.Ifnx), ("IFF_", Cmd.Iff), ("ELS_", Cmd.Els),
            ("EIF_", Cmd.Eif), ("NOP_", Cmd.Nop), ("DMMY", Cmd.Nop), ("ATSV", Cmd.Atsv), ("RDST", Cmd.Rdst), ("RTST", Cmd.Rtst),
            ("RDED", Cmd.Rded), ("RDAS", Cmd.Rdas), ("TRPY", Cmd.Trpy), ("XSTP", Cmd.Xstp), ("XRSM", Cmd.Xrsm),
            ("MSAD", Cmd.Msad), ("WNTY", Cmd.Wnty), ("PGST", Cmd.Pgst), ("CLO2", Cmd.Clo2), ("CLO3", Cmd.Clo3), ("CLO4", Cmd.Clo4),
            ("CLO5", Cmd.Clo5), ("WTTM", Cmd.Wttm), ("WT__", Cmd.Wttm), ("WTSV", Cmd.Wtsv), ("WTTR", Cmd.Wttr),
            ("SELR", Cmd.Selr), ("SELD", Cmd.Seld), ("SELS", Cmd.Sels), ("SELA", Cmd.Sela),
            ("MPLY", Cmd.Sound), ("MVOL", Cmd.Sound), ("MFAD", Cmd.Sound), ("MSTP", Cmd.Sound), ("SEPL", Cmd.Sound), ("SEFI", Cmd.Sound),
            ("SEFD", Cmd.Sound), ("SEVL", Cmd.Sound), ("SESP", Cmd.Sound), ("VPLY", Cmd.Sound), ("VPL2", Cmd.Sound), ("VCLR", Cmd.Sound),
            ("VCWT", Cmd.Vcwt), ("TOMP", Cmd.Tomp), ("TOMW", Cmd.Tomw),
            ("FCLN", Cmd.Fcln), ("FCSP", Cmd.Fcsp), ("FLNT", Cmd.Flnt), ("FLOP", Cmd.Flop), ("FLSP", Cmd.Flsp), ("FCCL", Cmd.Fccl),
            ("FADE", Cmd.Fade), ("STCG", Cmd.Stcg), ("SPFC", Cmd.Spfc), ("SPRD", Cmd.Sprd), ("SPRC", Cmd.Sprc), ("STPV", Cmd.Stpv),
            ("SNX4", Cmd.Ignored), ("FANX", Cmd.Ignored), ("STQK", Cmd.Ignored),
            ("SEPN", Cmd.Ignored), ("SEAC", Cmd.Ignored), ("SEP3", Cmd.Ignored), ("SHAK", Cmd.Ignored), ("SHAS", Cmd.Ignored),
            ("SNOW", Cmd.Ignored), ("STRC", Cmd.Ignored),
        ];
        foreach ((string, Cmd) t in one)
        {
            yield return t;
        }

        // the variable and VM-internal commands never reach the host; they are listed so that the table is the whole of ENGINE_MAIN
        foreach (string n in new[] { "WKST", "WKAD", "WKOR", "WKOF", "WKBO", "IF__", "GOTO", "GOTS" })
        {
            yield return (n, Cmd.Ignored);
        }

        foreach (string n in new[]
        {
            "STCH", "SCH2", "STCC", "STCP", "STBR", "STEC", "SNX2", "SNX3", "STZ4", "STRT", "STRR", "STRA", "SOXY", "STGS", "STMA", "SPMA", "STNS",
            "STTR", "SACL", "SBAS", "STCL", "SQK2", "SHK2", "SSW2", "SQS2", "SHS2", "SSS2", "SGMA", "STCF", "FADS", "SHOK", "SHAF", "MSQK",
        })
        {
            yield return (n, Cmd.Stage);
        }
    }

    private static FrozenDictionary<uint, Cmd> Build()
    {
        var d = new Dictionary<uint, Cmd>();
        foreach ((string name, Cmd cmd) in Names())
        {
            d[CommandName.FromAscii(name).Value] = cmd;
        }

        return d.ToFrozenDictionary();
    }
}
