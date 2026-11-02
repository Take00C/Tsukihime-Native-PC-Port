using Tsuki.Cli.Commands;
using Tsuki.Data;
using Tsuki.Data.Icons;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Data.Launcher;

/// <summary>
/// Package K43 on the real game (read only): the Arcueid icon the launcher cuts from the player's files (FaceIconCache, package K23) is stamped into a COPY of the
/// Tsukihime.exe built next to this test, in a temporary folder. The program then carries exactly her seven images (the pixels the window and the shortcuts show),
/// release-check refuses the stamped folder (a stamped install is not a release), and the crescent stamped back makes it a releasable folder again.
/// </summary>
public sealed class ExeIconStampRealDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k43-real-" + Guid.NewGuid().ToString("N"));

    public ExeIconStampRealDataTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [GameDataFact]
    public void HerFaceFromTheGameFilesIsStampedIntoACopyOfTheGameProgramAndTheCrescentComesBack()
    {
        string game = GameFolder.Require();
        string profile = Path.Combine(_root, "profile");
        string? icon = FaceIconCache.Ensure(profile, game, () => GameFolderSource.Open(game));
        Assert.NotNull(icon);
        IReadOnlyList<IcoImage>? face = IcoFile.ReadFile(icon);
        Assert.NotNull(face);
        Assert.Equal(FaceIcon.Sizes, face.Select(i => i.Size));

        string folder = Path.Combine(_root, "install");
        Directory.CreateDirectory(folder);
        foreach (string file in new[] { "Tsukihime.exe", "Tsukihime.dll" })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(folder, file));
        }

        string exe = Path.Combine(folder, "Tsukihime.exe");
        byte[] crescentIco = File.ReadAllBytes(Path.Combine(GoldenData.RepoRoot, "assets", "icon", "tsukihime.ico"));
        var crescent = new IconChoice("the port's crescent", IcoFile.Read(crescentIco)!);
        var arcueid = new IconChoice("Arcueid", face);
        IconChoice[] known = [crescent, arcueid];
        Assert.True(ReleaseCheck.Run(folder, crescentIco).Ok, "a copy of the build is a releasable folder");

        StampResult stamped = ExeIconStamp.StampExe(exe, arcueid, known);
        Assert.Equal(StampOutcome.Stamped, stamped.Outcome);
        Assert.Equal("Tsukihime.exe: Arcueid (7 images) written (it was the port's crescent (10 images))", stamped.Describe());

        // the program's group decodes to the very pixels of the profile's icon, every size
        IReadOnlyList<IconFrame> fromExe = FaceIcon.DecodeIco(PeIcons.IconGroup(exe)!);
        IReadOnlyList<IconFrame> fromProfile = FaceIcon.DecodeIco(File.ReadAllBytes(icon));
        Assert.Equal(fromProfile.Select(f => f.Size), fromExe.Select(f => f.Size));
        for (int i = 0; i < fromProfile.Count; i++)
        {
            Assert.Equal(fromProfile[i].Rgba, fromExe[i].Rgba);
        }

        // a stamped install is not a release: release-check says so; the launcher's own checks of a release (its .dll) are untouched
        ReleaseCheckResult refused = ReleaseCheck.Run(folder, crescentIco);
        Assert.False(refused.Ok);
        Assert.Contains(refused.Problems, p => p.StartsWith("Tsukihime.exe carries an icon that is not the port's crescent (7 frames", StringComparison.Ordinal));
        Assert.Equal(StampOutcome.Unchanged, ExeIconStamp.StampExe(exe, arcueid, known).Outcome);

        Assert.Equal(StampOutcome.Stamped, ExeIconStamp.StampExe(exe, crescent, known).Outcome);
        Assert.True(ReleaseCheck.Run(folder, crescentIco).Ok, "the crescent stamped back passes release-check again");
        Assert.Empty(Directory.GetFiles(folder, "*.icon-*"));
    }
}
