using System.Diagnostics;
using Tsuki.Data.IO;
using Tsuki.Data.Verification;

namespace Tsuki.Cli.Commands;

/// <summary>
/// <c>Tsuki.Cli verify [--game DIR] [-v]</c>: the build check of RR 3.3 (ROADMAP M0 acceptance 2). Accepts the
/// user's folder when eboot.bin has the analysed size and SHA-256 and the FORMATS 2 counts hold; rejects anything
/// else with a message that names the cause.
/// </summary>
public sealed class VerifyCommand : ICliCommand
{
    /// <inheritdoc />
    public string Name => "verify";

    /// <inheritdoc />
    public string Summary => "check the game folder against the analysed build (eboot SHA-256, FORMATS 2 counts); options: --game DIR, -v";

    /// <inheritdoc />
    public int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? game = null;
        bool verbose = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--game" when i + 1 < args.Length:
                    game = args[++i];
                    break;
                case "-v" or "--verbose":
                    verbose = true;
                    break;
                default:
                    error.WriteLine($"verify: unexpected argument '{args[i]}'");
                    return ExitCode.Usage;
            }
        }

        string? folder = GameFolderLocator.Resolve(game);
        if (folder is null)
        {
            error.WriteLine("verify: no game folder; pass --game <dir> or set TSUKI_GAME_DIR to the CUSA47999 folder");
            return ExitCode.Usage;
        }

        if (!Directory.Exists(folder) && !File.Exists(folder))
        {
            error.WriteLine($"verify: the folder '{folder}' does not exist");
            return ExitCode.Usage;
        }

        // package K25: the folder above the game, the base game with its update beside it, a .pkg: resolved the way the game and the launcher resolve them
        GameLayout layout = GameLayoutResolver.Resolve(folder);
        output.WriteLine($"layout: {layout.Describe()}");
        if (!layout.IsGame)
        {
            error.WriteLine("verify: " + new GameLayoutException(layout).Message);
            return ExitCode.Failed;
        }

        var sw = Stopwatch.StartNew();
        var report = BuildCheck.Run(layout);
        sw.Stop();
        output.Write(BuildReportFormatter.Format(report, verbose));
        output.WriteLine($"checked in {sw.Elapsed.TotalMilliseconds:F0} ms");
        return report.Accepted ? ExitCode.Ok : report.Usable ? ExitCode.Ok : ExitCode.Failed;
    }
}
