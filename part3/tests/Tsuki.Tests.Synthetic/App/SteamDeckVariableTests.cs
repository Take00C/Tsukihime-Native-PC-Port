using Tsuki.App;
using Tsuki.App.Services;
using Tsuki.Engine.Contracts;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// deck-evidence-1: the Steam Deck rules read the SteamDeck variable Steam sets for the games it starts, from the process itself (the other tests pass it in). The
/// test sets the variable of the test process, so it runs alone.
/// </summary>
[Collection("Profile environment")]
public sealed class SteamDeckVariableTests : IDisposable
{
    private readonly string? _deck = Environment.GetEnvironmentVariable("SteamDeck");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SteamDeck", _deck);
    }

    private static AppArgs Args(params string[] args)
    {
        Assert.True(AppArgs.TryParse(args, out AppArgs? parsed, out string? error), error);
        return parsed!;
    }

    [Fact]
    public void TheDeckRulesReadTheSteamDeckVariableOfTheProcess()
    {
        StartupSettings startup = StartupSettings.From(new AppSettingsData());
        ConfigBlock config = ConfigBlock.CreateBootDefaults();
        Environment.SetEnvironmentVariable("SteamDeck", "1");
        Assert.Equal(new StartWindowMode(true, StartWindowSource.SteamDeckDefault, SteamDeck: true), startup.DecideWindowMode(Args(), config, linux: true));
        Assert.Equal(new StartWindowMode(false, StartWindowSource.Remembered, SteamDeck: false), startup.DecideWindowMode(Args(), config, linux: false));

        Environment.SetEnvironmentVariable("SteamDeck", null);
        Assert.Equal(new StartWindowMode(false, StartWindowSource.Remembered, SteamDeck: false), startup.DecideWindowMode(Args(), config, linux: true));
        Environment.SetEnvironmentVariable("SteamDeck", "0");
        Assert.False(startup.DecideWindowMode(Args(), config, linux: true).SteamDeck);
    }
}
