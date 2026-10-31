using Tsuki.Cli.Commands;

namespace Tsuki.Cli;

/// <summary>Exit codes shared by every command.</summary>
public static class ExitCode
{
    /// <summary>The command did what it was asked to do.</summary>
    public const int Ok = 0;

    /// <summary>The command ran and reported a problem with the data (rejected build, mismatching output).</summary>
    public const int Failed = 1;

    /// <summary>The command line or the environment is wrong (unknown command, missing folder).</summary>
    public const int Usage = 2;
}

/// <summary>One CLI command.</summary>
public interface ICliCommand
{
    /// <summary>The word after <c>Tsuki.Cli</c>.</summary>
    string Name { get; }

    /// <summary>One line for the usage text.</summary>
    string Summary { get; }

    /// <summary>Runs the command; returns an <see cref="ExitCode"/> value.</summary>
    int Run(string[] args, TextWriter output, TextWriter error);
}

/// <summary>Command routing for <c>Tsuki.Cli</c>.</summary>
public static class CliApp
{
    private static readonly ICliCommand[] Commands =
    [
        new VerifyCommand(),
        new MzxBenchCommand(),
        new DecodeAllCommand(),
        new CensusCommand(),
        new PlayCommand(),
        new UiShotCommand(),
        new UiExitArtCommand(),
        new EffectShotCommand(),
        new FaceIconCommand(),
        new ReleaseCheckCommand(),
    ];

    /// <summary>Runs <c>Tsuki.Cli &lt;command&gt; [options]</c>.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage(args.Length == 0 ? error : output);
            return args.Length == 0 ? ExitCode.Usage : ExitCode.Ok;
        }

        var command = Commands.FirstOrDefault(c => string.Equals(c.Name, args[0], StringComparison.OrdinalIgnoreCase));
        if (command is null)
        {
            error.WriteLine($"unknown command '{args[0]}'");
            PrintUsage(error);
            return ExitCode.Usage;
        }

        try
        {
            return command.Run(args[1..], output, error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error.WriteLine($"{command.Name}: {e.Message}");
            return ExitCode.Usage;
        }
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("usage: Tsuki.Cli <command> [options]");
        foreach (var c in Commands)
        {
            w.WriteLine($"  {c.Name,-12} {c.Summary}");
        }

        w.WriteLine("  (dump, trace, explore, render-checkpoint, save-check: later milestones)");
        w.WriteLine("The game folder comes from --game <dir>, else the environment variable TSUKI_GAME_DIR (there is no built-in default path).");
    }
}
