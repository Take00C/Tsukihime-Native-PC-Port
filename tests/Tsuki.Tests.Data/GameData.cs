using System.Runtime.CompilerServices;

namespace Tsuki.Tests.Data;

/// <summary>
/// The user's CUSA47999 folder for the data tests: the environment variable <c>TSUKI_GAME_DIR</c> (the same rule as
/// <c>tools/tsuki/__init__.py</c>; there is no built-in default path). The folder is only ever read.
/// </summary>
public static class GameFolder
{
    /// <summary>The environment variable that names the game folder.</summary>
    public const string EnvironmentVariable = "TSUKI_GAME_DIR";

    private static readonly Lazy<string?> Resolved = new(Resolve);

    /// <summary>The game folder, or null when it is missing.</summary>
    public static string? Path => Resolved.Value;

    /// <summary>Why the data tests skip, or null when the folder is present.</summary>
    public static string? MissingReason { get; } = Resolved.Value is null
        ? Candidate() is { } tried
            ? $"game data missing: TSUKI_GAME_DIR must name the CUSA47999 folder (holding eboot.bin and image\\); tried {tried}"
            : "game data missing: set the environment variable TSUKI_GAME_DIR to the CUSA47999 folder (holding eboot.bin and image\\)"
        : null;

    /// <summary>The game folder; only call it from a <see cref="GameDataFactAttribute"/> test.</summary>
    public static string Require() =>
        Path ?? throw new InvalidOperationException(MissingReason);

    private static string? Candidate()
    {
        string? env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }

    private static string? Resolve()
    {
        string? dir = Candidate();
        return dir is not null && Directory.Exists(System.IO.Path.Combine(dir, "image")) && File.Exists(System.IO.Path.Combine(dir, "eboot.bin"))
            ? System.IO.Path.GetFullPath(dir)
            : null;
    }
}

/// <summary>
/// The one allowed skip (ARCHITECTURE 10.2, 10.8; ROADMAP 6 rule 5): a fact that needs the user's
/// game folder and skips, with the reason, when it is missing.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class GameDataFactAttribute : FactAttribute
{
    /// <summary>Creates the attribute.</summary>
    public GameDataFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (GameFolder.MissingReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>The <see cref="GameDataFactAttribute"/> rule for theories.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class GameDataTheoryAttribute : TheoryAttribute
{
    /// <summary>Creates the attribute.</summary>
    public GameDataTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (GameFolder.MissingReason is { } reason)
        {
            Skip = reason;
        }
    }
}
