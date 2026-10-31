using Tsuki.Data;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Flow;
using Tsuki.Engine.Game;

namespace Tsuki.Cli.Play;

/// <summary>The scripts and label tables of a game folder, decoded once (the VM switches scripts at every <c>_JUMP</c>).</summary>
internal sealed class ScriptCache(GameFolderSource source) : IFlowScripts
{
    private readonly Dictionary<int, ScriptImage> _images = [];
    private readonly Dictionary<int, LabelTable> _labels = [];

    public ScriptImage Script(int k)
    {
        if (!_images.TryGetValue(k, out ScriptImage? image))
        {
            image = source.Script(k);
            _images[k] = image;
        }

        return image;
    }

    public LabelTable Labels(int k)
    {
        if (!_labels.TryGetValue(k, out LabelTable? table))
        {
            table = source.Scripts.LabelLookup(k);
            _labels[k] = table;
        }

        return table;
    }
}

/// <summary>What the headless runs need: the opened folder and the services built over it.</summary>
internal sealed class HeadlessRig : IDisposable
{
    private HeadlessRig(GameFolderSource source, GameServices services)
    {
        Source = source;
        Services = services;
    }

    public GameFolderSource Source { get; }

    public GameServices Services { get; }

    public static HeadlessRig Open(string folder)
    {
        var source = GameFolderSource.Open(folder);
        var services = new GameServices(source, new ScriptCache(source))
        {
            SeLookup = (name, flags) => source.SoundEffectNames.Lookup(name, flags)?.Index,
        };
        return new HeadlessRig(source, services);
    }

    public void Dispose() => Source.Dispose();
}
