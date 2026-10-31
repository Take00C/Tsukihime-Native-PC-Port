namespace Tsuki.Data.Scripts;

/// <summary>How a main-interpreter handler ends the interpreter's per-frame batch (<c>script_tables.ENGINE_MAIN</c>).</summary>
public enum FrameBehaviour
{
    /// <summary>The handler always ends the batch (returns to the caller).</summary>
    Yield,

    /// <summary>The handler never ends the batch.</summary>
    Continue,

    /// <summary>The handler ends the batch on some paths only (waits, jumps, scan mode).</summary>
    Mixed,
}

/// <summary>
/// The command names the main interpreter (VA 0x95E60) dispatches, with each handler's address and frame behaviour (docs/formats/script.md
/// 3.1, engine.md 5.3; <c>ENGINE_MAIN</c>, <c>ENGINE_ALIASES</c> and <c>KEEP_BGM_FLAG_VA</c> of <c>tools/tsuki/script_tables.py</c>). The table is
/// not stored in the eboot (the dispatch is a compare tree), so it is a constant of this build, produced by emulating the dispatch for every
/// first letter and every 24-bit suffix breakpoint and cross-checked by brute force over all 26 x 37^3 names. The handler address is the
/// start of the basic block that holds the first instruction with a side effect.
/// </summary>
public static class EngineCommandTable
{
    /// <summary>Entries of <see cref="Main"/>: 110 names plus the two prefix rules <c>J*</c> and <c>Z*</c>.</summary>
    public const int EntryCount = 112;

    /// <summary>The byte the script-end cleanup clears instead of stopping the BGM; <c>_RTM2</c> and <c>_RTMN</c> set it (VA 0x174DE0C).</summary>
    public const long KeepBgmFlagVa = 0x174DE0C;

    /// <summary>Every command name the main interpreter accepts, to (handler VA, frame behaviour); <c>J*</c> and <c>Z*</c> stand for every name starting with that letter.</summary>
    public static IReadOnlyDictionary<string, (long HandlerVa, FrameBehaviour Behaviour)> Main { get; } =
        new SortedDictionary<string, (long, FrameBehaviour)>(StringComparer.Ordinal)
        {
        ["ATSV"] = (0x95F5D, FrameBehaviour.Mixed),
        ["CLO2"] = (0x9A887, FrameBehaviour.Yield),
        ["CLO3"] = (0x9A8B8, FrameBehaviour.Yield),
        ["CLO4"] = (0x9A8E7, FrameBehaviour.Yield),
        ["CLO5"] = (0x9A939, FrameBehaviour.Yield),
        ["EIF_"] = (0x96EFC, FrameBehaviour.Continue),
        ["ELS_"] = (0x96F2A, FrameBehaviour.Continue),
        ["END_"] = (0x9AB42, FrameBehaviour.Yield),
        ["FADE"] = (0x97973, FrameBehaviour.Mixed),
        ["FADS"] = (0x9B06D, FrameBehaviour.Yield),
        ["FCCL"] = (0x982B5, FrameBehaviour.Mixed),
        ["FCLN"] = (0x96D12, FrameBehaviour.Mixed),
        ["FCSP"] = (0x982A2, FrameBehaviour.Mixed),
        ["FLNT"] = (0x9606A, FrameBehaviour.Mixed),
        ["FLOP"] = (0x982C8, FrameBehaviour.Mixed),
        ["FLSP"] = (0x9715E, FrameBehaviour.Mixed),
        ["GOTO"] = (0x96E3F, FrameBehaviour.Mixed),
        ["GOTS"] = (0x9609A, FrameBehaviour.Mixed),
        ["IFF_"] = (0x97FC8, FrameBehaviour.Continue),
        ["IFJP"] = (0x9806F, FrameBehaviour.Mixed),
        ["IFLA"] = (0x96D4F, FrameBehaviour.Mixed),
        ["IFNX"] = (0x97F00, FrameBehaviour.Continue),
        ["IFSC"] = (0x96185, FrameBehaviour.Mixed),
        ["IFUR"] = (0x98161, FrameBehaviour.Mixed),
        ["IF__"] = (0x9708D, FrameBehaviour.Mixed),
        ["J*"] = (0x961DC, FrameBehaviour.Mixed),
        ["MFAD"] = (0x978F5, FrameBehaviour.Mixed),
        ["MPLY"] = (0x96E2C, FrameBehaviour.Continue),
        ["MSAD"] = (0x9AECE, FrameBehaviour.Yield),
        ["MSQK"] = (0x962D3, FrameBehaviour.Continue),
        ["MSTP"] = (0x978BD, FrameBehaviour.Continue),
        ["MVOL"] = (0x978AA, FrameBehaviour.Continue),
        ["PGST"] = (0x9637E, FrameBehaviour.Mixed),
        ["RDAS"] = (0x98118, FrameBehaviour.Continue),
        ["RDED"] = (0x9813B, FrameBehaviour.Mixed),
        ["RDST"] = (0x96DFF, FrameBehaviour.Continue),
        ["RTM2"] = (0x97FBC, FrameBehaviour.Continue),
        ["RTMN"] = (0x9643E, FrameBehaviour.Mixed),
        ["RTM_"] = (0x97174, FrameBehaviour.Mixed),
        ["RTST"] = (0x97131, FrameBehaviour.Continue),
        ["SACL"] = (0x99591, FrameBehaviour.Continue),
        ["SBAS"] = (0x99609, FrameBehaviour.Continue),
        ["SCH2"] = (0x98226, FrameBehaviour.Continue),
        ["SEAC"] = (0x99846, FrameBehaviour.Continue),
        ["SEFD"] = (0x99859, FrameBehaviour.Continue),
        ["SEFI"] = (0x98C8F, FrameBehaviour.Continue),
        ["SELA"] = (0x9B1B6, FrameBehaviour.Yield),
        ["SELD"] = (0x9B1DC, FrameBehaviour.Yield),
        ["SELR"] = (0x9936D, FrameBehaviour.Continue),
        ["SELS"] = (0x9B435, FrameBehaviour.Yield),
        ["SEP3"] = (0x99934, FrameBehaviour.Continue),
        ["SEPL"] = (0x98C8F, FrameBehaviour.Continue),
        ["SEPN"] = (0x996CB, FrameBehaviour.Continue),
        ["SESP"] = (0x996E1, FrameBehaviour.Continue),
        ["SEVL"] = (0x98C07, FrameBehaviour.Continue),
        ["SGMA"] = (0x99568, FrameBehaviour.Continue),
        ["SHAF"] = (0x99581, FrameBehaviour.Continue),
        ["SHAK"] = (0x971D5, FrameBehaviour.Mixed),
        ["SHAS"] = (0x9980A, FrameBehaviour.Continue),
        ["SHK2"] = (0x9982D, FrameBehaviour.Continue),
        ["SHOK"] = (0x98C44, FrameBehaviour.Continue),
        ["SHS2"] = (0x99673, FrameBehaviour.Continue),
        ["SNOW"] = (0x9968C, FrameBehaviour.Continue),
        ["SNX2"] = (0x98289, FrameBehaviour.Continue),
        ["SNX3"] = (0x9986C, FrameBehaviour.Continue),
        ["SOXY"] = (0x99885, FrameBehaviour.Continue),
        ["SPFC"] = (0x98CD0, FrameBehaviour.Continue),
        ["SPMA"] = (0x9929C, FrameBehaviour.Continue),
        ["SPRC"] = (0x992B6, FrameBehaviour.Continue),
        ["SPRD"] = (0x96A28, FrameBehaviour.Continue),
        ["SQK2"] = (0x992C7, FrameBehaviour.Continue),
        ["SQS2"] = (0x992E0, FrameBehaviour.Continue),
        ["SSS2"] = (0x986CF, FrameBehaviour.Continue),
        ["SSW2"] = (0x99D4B, FrameBehaviour.Continue),
        ["STBR"] = (0x990C2, FrameBehaviour.Continue),
        ["STCC"] = (0x97EED, FrameBehaviour.Continue),
        ["STCF"] = (0x9B448, FrameBehaviour.Yield),
        ["STCG"] = (0x99A4F, FrameBehaviour.Continue),
        ["STCH"] = (0x99AB7, FrameBehaviour.Continue),
        ["STCL"] = (0x99AF0, FrameBehaviour.Continue),
        ["STCP"] = (0x99B03, FrameBehaviour.Continue),
        ["STEC"] = (0x9991B, FrameBehaviour.Continue),
        ["STGS"] = (0x99065, FrameBehaviour.Continue),
        ["STMA"] = (0x9969F, FrameBehaviour.Continue),
        ["STNS"] = (0x996B8, FrameBehaviour.Continue),
        ["STPV"] = (0x983AB, FrameBehaviour.Continue),
        ["STRA"] = (0x97264, FrameBehaviour.Continue),
        ["STRC"] = (0x9952F, FrameBehaviour.Continue),
        ["STRR"] = (0x99542, FrameBehaviour.Continue),
        ["STRT"] = (0x99555, FrameBehaviour.Continue),
        ["STTR"] = (0x99947, FrameBehaviour.Continue),
        ["STZ4"] = (0x99038, FrameBehaviour.Continue),
        ["TOMP"] = (0x96F4C, FrameBehaviour.Mixed),
        ["TOMW"] = (0x9AB56, FrameBehaviour.Yield),
        ["TRPY"] = (0x96FC3, FrameBehaviour.Continue),
        ["VCLR"] = (0x97277, FrameBehaviour.Continue),
        ["VCWT"] = (0x96504, FrameBehaviour.Mixed),
        ["VPL2"] = (0x968B2, FrameBehaviour.Mixed),
        ["VPLY"] = (0x968B2, FrameBehaviour.Mixed),
        ["WKAD"] = (0x97AE6, FrameBehaviour.Continue),
        ["WKBO"] = (0x97BB8, FrameBehaviour.Continue),
        ["WKOF"] = (0x96B98, FrameBehaviour.Continue),
        ["WKOR"] = (0x9798B, FrameBehaviour.Continue),
        ["WKST"] = (0x96566, FrameBehaviour.Continue),
        ["WNTY"] = (0x972F5, FrameBehaviour.Continue),
        ["WTSV"] = (0x9AF36, FrameBehaviour.Yield),
        ["WTTM"] = (0x9AC1A, FrameBehaviour.Yield),
        ["WTTR"] = (0x9AFD1, FrameBehaviour.Yield),
        ["WT__"] = (0x9AC1A, FrameBehaviour.Yield),
        ["XRSM"] = (0x96E52, FrameBehaviour.Continue),
        ["XSTP"] = (0x965E5, FrameBehaviour.Continue),
        ["Z*"] = (0x96615, FrameBehaviour.Mixed),
        };

    /// <summary>Names that share a handler with another name.</summary>
    public static IReadOnlyDictionary<string, string> Aliases { get; } = new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["SEFI"] = "SEPL",
        ["VPL2"] = "VPLY",
        ["WT__"] = "WTTM",
    };

    /// <summary>
    /// The handler of a four-character command name, or null when the main interpreter ignores the command (for example <c>DMMY</c>,
    /// <c>NOP_</c>, <c>IFP4</c>, <c>SNX4</c>).
    /// </summary>
    public static (long HandlerVa, FrameBehaviour Behaviour)? Handler(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length > 0 && name[0] is 'J' or 'Z')
        {
            return Main[name[0] + "*"];
        }

        return Main.TryGetValue(name, out var h) ? h : null;
    }

    /// <summary>True when the main interpreter dispatches the name.</summary>
    public static bool IsEngineCommand(string name) => Handler(name) is not null;
}
