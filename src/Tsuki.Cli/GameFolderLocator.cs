namespace Tsuki.Cli;

/// <summary>Finds the user's CUSA47999 folder (the same rule as <c>tools/tsuki/__init__.py</c>: <c>--game</c>, else <c>TSUKI_GAME_DIR</c>; no built-in default path).</summary>
public static class GameFolderLocator
{
    /// <summary>The environment variable that names the game folder when <c>--game</c> is not given.</summary>
    public const string EnvironmentVariable = "TSUKI_GAME_DIR";

    /// <summary>What a command prints when neither <c>--game</c> nor the variable names the folder.</summary>
    public const string MissingMessage =
        "game folder not found: pass --game <folder> or set the environment variable TSUKI_GAME_DIR to your extracted CUSA47999 folder (the one holding eboot.bin, image\\, data\\ and sce_sys\\)";

    /// <summary>
    /// The folder named by <c>--game</c>, then <c>TSUKI_GAME_DIR</c>, else null. An explicit choice is returned even if
    /// the folder does not exist, so the build check can say so.
    /// </summary>
    public static string? Resolve(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        string? env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }
}
