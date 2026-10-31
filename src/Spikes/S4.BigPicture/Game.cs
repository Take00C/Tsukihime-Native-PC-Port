// Spike S4: game folder (--game, else the environment variable TSUKI_GAME_DIR; no built-in default path) and opened archives.
namespace S4.BigPicture;

internal sealed class Game : IDisposable
{
    public const string MissingMessage =
        "game folder not found: pass --game <folder> or set the environment variable TSUKI_GAME_DIR to your extracted CUSA47999 folder (the one holding eboot.bin and image\\)";

    public string Dir { get; }
    public string ImageDir => Path.Combine(Dir, "image");
    private readonly Dictionary<string, Triplet> _arcs = new();

    public Game(string? dir)
    {
        string? env = Environment.GetEnvironmentVariable("TSUKI_GAME_DIR");
        Dir = dir ?? (string.IsNullOrEmpty(env) ? throw new DirectoryNotFoundException(MissingMessage) : env);
        if (!Directory.Exists(ImageDir))
            throw new DirectoryNotFoundException($"game image folder not found: {ImageDir} (set TSUKI_GAME_DIR)");
    }

    public Triplet Archive(string name)
    {
        lock (_arcs)
        {
            if (!_arcs.TryGetValue(name, out var t))
                _arcs[name] = t = new Triplet(ImageDir, name);
            return t;
        }
    }

    public void Dispose()
    {
        foreach (var t in _arcs.Values)
            t.Dispose();
        _arcs.Clear();
    }
}
