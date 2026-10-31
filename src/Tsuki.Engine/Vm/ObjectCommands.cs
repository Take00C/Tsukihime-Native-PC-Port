using System.Collections.Frozen;
using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Vm;

/// <summary>
/// The 52 commands whose handler reads its arguments with the object parser instead of the inline readers
/// (docs/formats/script.md 3.2; <c>OBJ_COMMANDS</c> in <c>tools/tsuki/script_tables.py</c>, found by walking every handler
/// of the main interpreter).
/// </summary>
public static class ObjectCommands
{
    /// <summary>The names in alphabetical order.</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "CLO4", "CLO5", "FADE", "FADS", "FCCL", "FCLN", "FCSP", "FLNT", "FLOP", "FLSP", "MSQK",
        "RDAS", "RDED", "RDST", "RTST", "SACL", "SBAS", "SCH2", "SELA", "SELS", "SGMA", "SHK2",
        "SHS2", "SNX2", "SNX3", "SOXY", "SPFC", "SPMA", "SPRD", "SQK2", "SQS2", "SSS2", "SSW2",
        "STBR", "STCC", "STCF", "STCH", "STCL", "STCP", "STEC", "STGS", "STMA", "STNS", "STRA",
        "STRC", "STRR", "STRT", "STTR", "STZ4", "TOMP", "WTSV", "WTTR",
    ];

    private static readonly FrozenSet<uint> Set =
        Names.Select(n => CommandName.FromAscii(n).Value).ToFrozenSet();

    /// <summary>The name of TOMP, whose handler reads the movie id with the partial-strtol getter (VA 0xE3C0).</summary>
    public static readonly CommandName Tomp = CommandName.FromAscii("TOMP");

    /// <summary>True when the command reads its arguments with the object parser.</summary>
    public static bool Contains(CommandName name) => Set.Contains(name.Value);
}
