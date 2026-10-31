using Tsuki.Engine.Contracts;
using Tsuki.Engine.Flow;
using Tsuki.Engine.Stage;

namespace Tsuki.Engine.Game;

/// <summary>
/// What the game interpreter needs from the outside world besides <see cref="IGameData"/> (which already carries the picture catalog, the
/// motions, the glyph tables and the scene tables through <see cref="IEbootTables"/>): the script label tables, the sound-effect name lookup
/// and the picture layout source of the stage. The composition root (Tsuki.App, Tsuki.Cli) builds it over <c>GameFolderSource</c>.
/// </summary>
public sealed class GameServices
{
    /// <summary>Creates the services.</summary>
    /// <param name="data">The game data.</param>
    /// <param name="scripts">The scripts and their label tables (the VM switches scripts through it).</param>
    public GameServices(IGameData data, IFlowScripts scripts)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(scripts);
        Data = data;
        Scripts = scripts;
    }

    /// <summary>The game data.</summary>
    public IGameData Data { get; }

    /// <summary>The scripts and their label tables.</summary>
    public IFlowScripts Scripts { get; }

    /// <summary>The sound-effect lookup of <c>_SEPL</c> (name, flags) to the vse entry (<c>SeNames.Lookup</c>); null: every name is unknown.</summary>
    public Func<string, int, int?>? SeLookup { get; init; }

    /// <summary>The picture layout source of the stage draw; null: the layers produce no quads (headless runs).</summary>
    public IPictureLayoutSource? Layouts { get; init; }

    /// <summary>
    /// How many 60 Hz frames a sound stream plays: voices (<see cref="AudioBank.Voice"/>) and effects. Headless runs have no mixer, so this
    /// stands in for <c>AudioCommandPlayer.IsPlaying</c>; null: a voice lasts <see cref="GameOptions.DefaultVoiceFrames"/> frames.
    /// </summary>
    public Func<AudioBank, int, int>? ClipFrames { get; init; }
}

/// <summary>Options of a game session.</summary>
public sealed class GameOptions
{
    /// <summary>The game language (the config language is set from it at a new profile).</summary>
    public Language Language { get; init; } = Language.En;

    /// <summary>Frames a voice lasts when <see cref="GameServices.ClipFrames"/> is null.</summary>
    public int DefaultVoiceFrames { get; init; } = 120;

    /// <summary>
    /// The movie clock: when true (headless runs and the explorer) a movie ends as soon as its start-up countdown is over; when false the
    /// host waits for the <see cref="MovieClock"/> the movie host reports each frame.
    /// </summary>
    public bool VirtualMovies { get; init; } = true;

    /// <summary>When true, an unknown command is an error even outside TSUKI_DEV builds.</summary>
    public bool StrictCommands { get; init; }

    /// <summary>When true, unknown commands are counted (<c>GameHost.UnknownCommands</c>) instead of throwing in TSUKI_DEV builds (the coverage run).</summary>
    public bool RecordUnknownCommands { get; init; }

    /// <summary>Seed of the stage's <c>rand()</c> (0: the engine's initial state 1).</summary>
    public uint RandomSeed { get; init; }

    /// <summary>When true, sound commands are executed (the sound model runs); headless coverage runs can switch it off.</summary>
    public bool Sound { get; init; } = true;
}
