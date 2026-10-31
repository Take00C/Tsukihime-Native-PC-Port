using System.Text;
using Tsuki.Data.IO;
using Tsuki.Data.Verification;
using Tsuki.Tests.Synthetic.Launcher;

namespace Tsuki.Tests.Synthetic.Data;

/// <summary>
/// Package K25: how other players' dumps are laid out on disk, with folders made for the purpose (no game data): the game folder itself, the folder above it (one or
/// two levels), a folder inside it, the base game with the update beside it under the names the extractors use, the update alone, a .pkg, several games, nothing. And
/// the layered view the update is read through.
/// </summary>
public sealed class GameLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k25-layout-" + Guid.NewGuid().ToString("N"));

    public GameLayoutTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private string Dir(params string[] parts)
    {
        string dir = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A full game folder: eboot.bin, image\allpac0.hed and the other files the resolver looks at, param.sfo with CATEGORY gd.</summary>
    private string Game(string name, string version = "01.00", string category = "gd", bool archives = true)
    {
        string dir = Dir(name);
        Directory.CreateDirectory(Path.Combine(dir, "image"));
        Directory.CreateDirectory(Path.Combine(dir, "sce_sys"));
        File.WriteAllBytes(Path.Combine(dir, "eboot.bin"), Encoding.ASCII.GetBytes("eboot of " + name));
        if (archives)
        {
            File.WriteAllBytes(Path.Combine(dir, "image", "allpac0.hed"), new byte[16]);
            File.WriteAllBytes(Path.Combine(dir, "image", "allpac0.mrg"), Encoding.ASCII.GetBytes("base allpac0"));
            File.WriteAllBytes(Path.Combine(dir, "image", "allscr.mrg"), Encoding.ASCII.GetBytes("base allscr"));
        }

        File.WriteAllBytes(Path.Combine(dir, "sce_sys", "param.sfo"), GameFolderTests.Sfo(("APP_VER", version), ("CATEGORY", category), ("TITLE_ID", "CUSA47999"), ("TITLE", "TSUKIHIME")));
        return dir;
    }

    /// <summary>An update folder: eboot.bin, a few image files, param.sfo with CATEGORY gp.</summary>
    private string Patch(string name, bool sfo = true)
    {
        string dir = Dir(name);
        Directory.CreateDirectory(Path.Combine(dir, "image"));
        File.WriteAllBytes(Path.Combine(dir, "eboot.bin"), Encoding.ASCII.GetBytes("eboot of " + name));
        File.WriteAllBytes(Path.Combine(dir, "image", "allscr.mrg"), Encoding.ASCII.GetBytes("patched allscr"));
        File.WriteAllBytes(Path.Combine(dir, "image", "script_text.mrg"), Encoding.ASCII.GetBytes("patched text"));
        if (sfo)
        {
            Directory.CreateDirectory(Path.Combine(dir, "sce_sys"));
            File.WriteAllBytes(Path.Combine(dir, "sce_sys", "param.sfo"), GameFolderTests.Sfo(("APP_VER", "01.01"), ("CATEGORY", "gp"), ("TITLE_ID", "CUSA47999")));
        }

        return dir;
    }

    [Fact]
    public void TheGameFolderItselfAndTheFoldersAroundItResolveToIt()
    {
        string game = Game(Path.Combine("dumps", "CUSA47999"));
        GameLayout direct = GameLayoutResolver.Resolve(game);
        Assert.Equal(GameLayoutKind.Game, direct.Kind);
        Assert.Equal(game, direct.BaseFolder);
        Assert.Null(direct.PatchFolder);
        Assert.True(direct.IsGame);

        Assert.Equal(game, GameLayoutResolver.Resolve(Path.Combine(_root, "dumps")).BaseFolder);                      // the folder above
        Assert.Equal(game, GameLayoutResolver.Resolve(_root).BaseFolder);                                             // two levels above
        Assert.Equal(game, GameLayoutResolver.Resolve(Path.Combine(game, "image")).BaseFolder);                       // a folder inside
        Assert.Equal(game, GameLayoutResolver.Resolve(Path.Combine(game, "sce_sys")).BaseFolder);
        Assert.Equal(game, GameLayoutResolver.Resolve(Path.Combine(game, "eboot.bin")).BaseFolder);                   // the file itself
        Assert.Equal(game, GameLayoutResolver.Resolve("\"" + game + "\\\"").BaseFolder);                               // pasted with quotes and a trailing slash
        Assert.Equal(_root, GameLayoutResolver.Resolve(_root).Chosen);
        Assert.Equal(game, GameLayoutResolver.Resolve(_root).PrimaryFolder);
    }

    [Fact]
    public void ABaseGameWithTheUpdateBesideItIsOneGameWhoseUpdateWins()
    {
        string game = Game(Path.Combine("split", "CUSA47999"));
        string patch = Patch(Path.Combine("split", "CUSA47999-patch"));
        foreach (string chosen in new[] { game, patch, Path.Combine(_root, "split"), Path.Combine(game, "image") })
        {
            GameLayout layout = GameLayoutResolver.Resolve(chosen);
            Assert.Equal(GameLayoutKind.GameWithUpdate, layout.Kind);
            Assert.Equal(game, layout.BaseFolder);
            Assert.Equal(patch, layout.PatchFolder);
            Assert.True(layout.IsGame);
            Assert.Contains("update folder", layout.Describe(), StringComparison.Ordinal);
        }

        IGameFiles files = GameLayoutResolver.Resolve(game).OpenFiles();
        Assert.IsType<LayeredGameFiles>(files);
        Assert.Equal("eboot of " + Path.Combine("split", "CUSA47999-patch"), Encoding.ASCII.GetString(files.ReadAll("eboot.bin")));      // the update's eboot
        Assert.Equal("patched allscr", Encoding.ASCII.GetString(files.ReadAll("image/allscr.mrg")));                                    // the update's file wins
        Assert.Equal("base allpac0", Encoding.ASCII.GetString(files.ReadAll("image/allpac0.mrg")));                                     // the base's file stays
        Assert.Equal(Path.Combine(patch, "image", "allscr.mrg"), files.ResolveFile("image/allscr.mrg"));
        Assert.Equal(Path.Combine(game, "image", "allpac0.mrg"), files.ResolveFile("image/allpac0.mrg"));
        Assert.Null(files.ResolveFile("image/nothing.mrg"));
        Assert.Equal(["allpac0.hed", "allpac0.mrg", "allscr.mrg", "script_text.mrg"], files.ListFiles("image"));
        Assert.True(files.Exists("image/script_text.mrg"));
        Assert.Equal(12, files.Length("image/script_text.mrg"));
        Assert.Throws<FileNotFoundException>(() => files.Length("image/none"));
        Assert.Contains("CUSA47999-patch", files.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CUSA47999", "CUSA47999-patch")]
    [InlineData("CUSA47999", "CUSA47999_patch")]
    [InlineData("CUSA47999", "CUSA47999-UPDATE")]
    [InlineData("CUSA47999-app", "CUSA47999-patch")]
    [InlineData("CUSA47999", "CUSA47999-Patch")]
    public void TheExtractorsNamesForTheTwoFoldersArePaired(string baseName, string patchName)
    {
        string game = Game(Path.Combine("pair", baseName));
        string patch = Patch(Path.Combine("pair", patchName));
        GameLayout fromBase = GameLayoutResolver.Resolve(game);
        GameLayout fromPatch = GameLayoutResolver.Resolve(patch);
        GameLayout fromParent = GameLayoutResolver.Resolve(Path.Combine(_root, "pair"));
        foreach (GameLayout layout in new[] { fromBase, fromPatch, fromParent })
        {
            Assert.Equal(GameLayoutKind.GameWithUpdate, layout.Kind);
            Assert.Equal(game, layout.BaseFolder);
            Assert.Equal(patch, layout.PatchFolder);
        }
    }

    [Fact]
    public void AnUpdateAloneAPkgAndAFolderWithoutTheGameAreToldApart()
    {
        string patch = Patch(Path.Combine("alone", "CUSA47999-patch"));
        GameLayout alone = GameLayoutResolver.Resolve(patch);
        Assert.Equal(GameLayoutKind.UpdateOnly, alone.Kind);
        Assert.False(alone.IsGame);
        Assert.Equal(patch, alone.PatchFolder);
        Assert.Equal(GameLayoutKind.UpdateOnly, GameLayoutResolver.Resolve(Path.Combine(_root, "alone")).Kind);

        // an update folder recognised by its name alone (no param.sfo)
        string named = Patch(Path.Combine("named", "CUSA47999-patch"), sfo: false);
        Assert.Equal(GameLayoutKind.UpdateOnly, GameLayoutResolver.Resolve(named).Kind);

        string pkgDir = Dir("pkg");
        string pkg = Path.Combine(pkgDir, "Tsukihime-CUSA47999.pkg");
        File.WriteAllBytes(pkg, new byte[64]);
        GameLayout pkgLayout = GameLayoutResolver.Resolve(pkg);
        Assert.Equal(GameLayoutKind.PkgFile, pkgLayout.Kind);
        Assert.Equal(GameLayoutKind.NoGame, GameLayoutResolver.Resolve(pkgDir).Kind);                                  // the folder with only the .pkg

        Assert.Equal(GameLayoutKind.NoGame, GameLayoutResolver.Resolve(Dir("empty")).Kind);
        Assert.Equal(GameLayoutKind.Missing, GameLayoutResolver.Resolve(Path.Combine(_root, "nowhere")).Kind);
        Assert.Equal(GameLayoutKind.NotSet, GameLayoutResolver.Resolve(null).Kind);
        Assert.Equal(GameLayoutKind.NotSet, GameLayoutResolver.Resolve("  ").Kind);

        // the messages the game shows name the cause
        Assert.Contains(".pkg", new GameLayoutException(pkgLayout).Message, StringComparison.Ordinal);
        Assert.Contains("extracted", new GameLayoutException(pkgLayout).Message, StringComparison.Ordinal);
        Assert.Contains("update", new GameLayoutException(alone).Message, StringComparison.Ordinal);
        Assert.Contains("beside", new GameLayoutException(alone).Message, StringComparison.Ordinal);
        Assert.Throws<GameLayoutException>(() => Tsuki.Data.GameFolderSource.Open(pkgLayout));
        Assert.Throws<InvalidOperationException>(() => alone.OpenFiles());
    }

    [Fact]
    public void SeveralGamesInsideOneFolderAreReportedUnlessOneCarriesTheTitleId()
    {
        string a = Game(Path.Combine("many", "GameA"));
        string b = Game(Path.Combine("many", "GameB"));
        GameLayout several = GameLayoutResolver.Resolve(Path.Combine(_root, "many"));
        Assert.Equal(GameLayoutKind.Several, several.Kind);
        Assert.Equal([a, b], several.CandidateList.Order(StringComparer.Ordinal));
        Assert.Contains("GameA", new GameLayoutException(several).Message, StringComparison.Ordinal);

        string titled = Game(Path.Combine("many", "CUSA47999"));
        GameLayout chosen = GameLayoutResolver.Resolve(Path.Combine(_root, "many"));
        Assert.Equal(GameLayoutKind.Game, chosen.Kind);
        Assert.Equal(titled, chosen.BaseFolder);
    }

    [Fact]
    public void AnEbootWithoutArchivesAndWithoutAPatchMarkIsAnIncompleteGameNotAnUpdate()
    {
        string bare = Game("bare", archives: false);
        GameLayout layout = GameLayoutResolver.Resolve(bare);
        Assert.Equal(GameLayoutKind.Game, layout.Kind);
        Assert.Equal(bare, layout.BaseFolder);
        Assert.False(GameLayoutResolver.IsPatchFolder(bare));
    }

    [Fact]
    public void TheResolverNeverWritesIntoTheFolders()
    {
        string game = Game(Path.Combine("ro", "CUSA47999"));
        Patch(Path.Combine("ro", "CUSA47999-patch"));
        string[] before = Listing(Path.Combine(_root, "ro"));
        _ = GameLayoutResolver.Resolve(game);
        _ = GameLayoutResolver.Resolve(Path.Combine(_root, "ro"));
        _ = GameLayoutResolver.Resolve(game).OpenFiles().ReadAll("eboot.bin");
        _ = BuildIdentity.Identify(GameLayoutResolver.Resolve(game).OpenFiles());
        Assert.Equal(before, Listing(Path.Combine(_root, "ro")));
    }

    private static string[] Listing(string dir) =>
        Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
            .Select(p => $"{Path.GetRelativePath(dir, p)}|{(File.Exists(p) ? new FileInfo(p).Length : -1)}|{File.GetLastWriteTimeUtc(p).Ticks}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    // -- identity on folders without the game's data ------------------------------------------------------------------

    [Fact]
    public void TheIdentityReadsParamSfoAndTellsTheKnownBuildsApart()
    {
        string game = Game(Path.Combine("id", "CUSA47999"), version: "01.00");
        string patch = Patch(Path.Combine("id", "CUSA47999-patch"));
        BuildIdentity split = BuildIdentity.Identify(GameLayoutResolver.Resolve(game).OpenFiles());
        Assert.Equal("CUSA47999", split.TitleId);
        Assert.Equal("01.01", split.AppVersion);                                       // the update's param.sfo wins
        Assert.Equal("gp", split.Category);
        Assert.Equal(BuildMatch.KnownRelease, split.Match);                             // a known title id, an eboot the port has not seen
        Assert.Null(split.Known);
        Assert.Empty(split.TextLanguages);
        Assert.Empty(split.AvailableLanguages);
        Assert.StartsWith("other version of a known release: CUSA47999 01.01", split.Describe(), StringComparison.Ordinal);
        Assert.Contains("selectable none", split.DescribeLanguages(), StringComparison.Ordinal);

        BuildIdentity alone = BuildIdentity.Identify(new DirectoryGameFiles(game));
        Assert.Equal("01.00", alone.AppVersion);
        Assert.Equal("gd", alone.Category);

        string other = Game("CUSA00001");
        File.WriteAllBytes(Path.Combine(other, "sce_sys", "param.sfo"), GameFolderTests.Sfo(("APP_VER", "02.00"), ("CONTENT_ID", "UP9000-CUSA00001_00-SOMETHINGELSE000")));
        BuildIdentity unknown = BuildIdentity.Identify(new DirectoryGameFiles(other));
        Assert.Equal("CUSA00001", unknown.TitleId);                                      // from CONTENT_ID when TITLE_ID is missing
        Assert.Equal(BuildMatch.Unknown, unknown.Match);
        Assert.StartsWith("unknown build: CUSA00001 02.00", unknown.Describe(), StringComparison.Ordinal);

        BuildIdentity none = BuildIdentity.Identify(new DirectoryGameFiles(Dir("nothing")));
        Assert.Null(none.TitleId);
        Assert.Equal(0, none.EbootSize);
        Assert.Equal(BuildMatch.Unknown, none.Match);
        Assert.Equal("unknown title (version unknown)", none.ShortName);
    }

    [Fact]
    public void TheKnownBuildsTableNamesTheVerifiedEbootAndOneMore()
    {
        Assert.Same(KnownBuilds.Verified, KnownBuilds.All[0]);
        Assert.Equal(Tsuki.Engine.Contracts.BuildInfo.AnalysedEbootSha256, KnownBuilds.Verified.EbootSha256);
        Assert.Equal(Tsuki.Engine.Contracts.BuildInfo.AnalysedEbootSize, KnownBuilds.Verified.EbootSize);
        Assert.Same(KnownBuilds.Verified, KnownBuilds.Find(Tsuki.Engine.Contracts.BuildInfo.AnalysedEbootSize, Tsuki.Engine.Contracts.BuildInfo.AnalysedEbootSha256.ToUpperInvariant()));
        Assert.Null(KnownBuilds.Find(1, "00"));
        Assert.Equal(["CUSA47999"], KnownBuilds.TitleIds);
        Assert.All(KnownBuilds.All, b => Assert.Equal(64, b.EbootSha256.Length));
        Assert.Equal(KnownBuilds.All.Count, KnownBuilds.All.Select(b => b.EbootSha256).Distinct().Count());
    }

    [Fact]
    public void ADriveRootStaysTheRootAndATrailingSeparatorIsDropped()
    {
        // K27: "D:\" trimmed to "D:" made GetFullPath answer the process's current directory on that drive; the root must stay the root, a bare "D:" means the root too
        string root = Path.GetPathRoot(_root)!;
        GameLayout slash = GameLayoutResolver.Resolve(root);
        Assert.Equal(root, slash.Chosen);
        Assert.NotEqual(GameLayoutKind.Missing, slash.Kind);
        Assert.NotEqual(GameLayoutKind.NotSet, slash.Kind);
        Assert.Equal(root, GameLayoutResolver.Resolve(root.TrimEnd('\\', '/')).Chosen);
        Assert.Equal(root, GameLayoutResolver.Resolve("\"" + root + "\"").Chosen);

        // an ordinary folder loses its trailing separator, with or without quotes, and a run of separators alone is nothing
        string game = Game("CUSA47999");
        Assert.Equal(game, GameLayoutResolver.Resolve(game + "\\").Chosen);
        Assert.Equal(game, GameLayoutResolver.Resolve("\"" + game + "/\"").Chosen);
        Assert.Equal(GameLayoutKind.Game, GameLayoutResolver.Resolve(game + "\\").Kind);
        Assert.Equal(GameLayoutKind.NotSet, GameLayoutResolver.Resolve("\\").Kind);
        Assert.Equal(GameLayoutKind.NotSet, GameLayoutResolver.Resolve("  //  ").Kind);
    }
}
