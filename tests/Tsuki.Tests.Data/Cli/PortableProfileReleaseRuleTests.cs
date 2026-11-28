using Tsuki.Cli.Commands;

namespace Tsuki.Tests.Data.Cli;

/// <summary>
/// v0.1.11: a folder named save beside the programs is the portable profile (ProfileDirectory.Select): a release that carried one, even without a save.dat
/// (a portable switch tried once in the publish folder leaves settings.ini, game_dir.txt and launcher.log there), would become the profile of every player
/// who installed or updated to it, and their own saves would seem gone. So would the folders the switch sets aside (save-backup-..., save-incomplete-...).
/// (These need no game folder; they live here because the release rules are the command line tool's.)
/// </summary>
public sealed class PortableProfileReleaseRuleTests
{
    [Theory]
    [InlineData("save/settings.ini")]
    [InlineData("save/game_dir.txt")]
    [InlineData("save/launcher.log")]
    [InlineData("Save/backups/20261127-220449/settings.ini")]
    [InlineData("save\\icons\\tsukihime.ico.src")]
    [InlineData("/save/settings.ini")]
    [InlineData("Tsukihime-v0.1.11-linux-x64/save/settings.ini")]       // an archive's entry, below its one folder
    [InlineData("save-backup-20261127-220449/settings.ini")]
    [InlineData("save-backup-20261127-220449-2/game_dir.txt")]
    [InlineData("save-incomplete-20261127-220449/settings.ini")]
    public void APortableProfileIsRefusedWhereverItLies(string path)
    {
        foreach (bool macBundle in new[] { false, true })
        {
            string? why = ReleaseRules.Forbidden(path, macBundle);
            Assert.NotNull(why);
            Assert.Contains("portable profile", why, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("save")]                                                // a file named save is no folder
    [InlineData("savestate.txt")]
    [InlineData("saves/notes.txt")]
    [InlineData("docs/saves.md")]
    [InlineData("docs/formats/save.dat.md")]
    public void OnlyAFolderNamedSaveOrSetAsideByTheSwitchIsRefused(string path) => Assert.Null(ReleaseRules.Forbidden(path));
}
