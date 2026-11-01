using System.Reflection;
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
    [InlineData("EBOOT.BIN")]
    [InlineData("EbOoT.BiN")]
    public void CaseVariantEbootResolvesFromEverySupportedSelection(string ebootName)
    {
        string game = Game(Path.Combine("dumps", "CUSA47999"));
        File.Move(Path.Combine(game, "eboot.bin"), Path.Combine(game, ebootName));
        string data = Path.Combine(game, "DATA");
        Directory.CreateDirectory(data);
        File.WriteAllBytes(Path.Combine(data, "Resource.BIN"), [1, 2, 3]);
        Assert.True(new DirectoryGameFiles(game).Exists("eboot.bin"));

        foreach (string chosen in new[] { game, _root, Path.Combine(_root, "dumps"), Path.Combine(game, ebootName), Path.Combine(game, "image"), data, Path.Combine(data, "Resource.BIN") })
        {
            GameLayout layout = GameLayoutResolver.Resolve(chosen);
            Assert.Equal(GameLayoutKind.Game, layout.Kind);
            Assert.Equal(game, layout.BaseFolder);
            Assert.Equal([1, 2, 3], layout.OpenFiles().ReadAll("data/resource.bin"));
        }
    }

    [Theory]
    [InlineData("IMAGE", "ALLPAC0.HED")]
    [InlineData("ImAgE", "AllPac0.HeD")]
    public void CaseVariantArchivesStillPairTheSiblingUpdate(string imageName, string archiveName)
    {
        string game = Game(Path.Combine("split", "CUSA47999"));
        string patch = Patch(Path.Combine("split", "CUSA47999-patch"));
        File.Move(Path.Combine(game, "image", "allpac0.hed"), Path.Combine(game, "image", archiveName));
        Directory.Move(Path.Combine(game, "image"), Path.Combine(game, imageName));
        File.Move(Path.Combine(patch, "eboot.bin"), Path.Combine(patch, "EBOOT.BIN"));
        File.Move(Path.Combine(patch, "image", "allscr.mrg"), Path.Combine(patch, "image", "ALLSCR.MRG"));
        Directory.Move(Path.Combine(patch, "image"), Path.Combine(patch, "IMAGE"));
        Assert.True(new DirectoryGameFiles(game).Exists("image/allpac0.hed"));

        foreach (string chosen in new[] { game, patch, Path.Combine(_root, "split"), Path.Combine(game, imageName) })
        {
            GameLayout layout = GameLayoutResolver.Resolve(chosen);
            Assert.Equal(GameLayoutKind.GameWithUpdate, layout.Kind);
            Assert.Equal(game, layout.BaseFolder);
            Assert.Equal(patch, layout.PatchFolder);
            IGameFiles files = layout.OpenFiles();
            Assert.Equal("patched allscr", Encoding.ASCII.GetString(files.ReadAll("image/allscr.mrg")));
            Assert.Equal("base allpac0", Encoding.ASCII.GetString(files.ReadAll("image/allpac0.mrg")));
            Assert.Equal(Path.Combine(game, imageName, archiveName), files.ResolveFile("image/allpac0.hed"),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            Assert.Equal("01.01", BuildIdentity.Identify(files).AppVersion);
        }
    }

    [Fact]
    public void CaseVariantMetadataControlsPatchClassificationAndTitleMatching()
    {
        string unrelated = Game(Path.Combine("metadata", "other"));
        File.WriteAllBytes(Path.Combine(unrelated, "sce_sys", "param.sfo"), GameFolderTests.Sfo(("TITLE_ID", "CUSA00000"), ("CATEGORY", "gd")));
        ChangeMetadataCase(unrelated);
        string patch = Patch(Path.Combine("metadata", "CUSA47999-extra"));
        ChangeMetadataCase(patch);

        Assert.True(GameLayoutResolver.IsPatchFolder(patch));
        Assert.Null(GameLayoutResolver.FindBaseBeside(patch));
        Assert.Equal(GameLayoutKind.UpdateOnly, GameLayoutResolver.Resolve(patch).Kind);

        string game = Game(Path.Combine("metadata", "CUSA47999"));
        ChangeMetadataCase(game);
        Assert.False(GameLayoutResolver.IsPatchFolder(game));
        Assert.Equal(game, GameLayoutResolver.FindBaseBeside(patch));
        Assert.Equal(patch, GameLayoutResolver.FindPatchBeside(game));
        foreach (string chosen in new[] { game, patch, Path.Combine(game, "ScE_SyS"), Path.Combine(patch, "ScE_SyS", "PaRaM.SfO") })
        {
            GameLayout layout = GameLayoutResolver.Resolve(chosen);
            Assert.Equal(GameLayoutKind.GameWithUpdate, layout.Kind);
            Assert.Equal(game, layout.BaseFolder);
            Assert.Equal(patch, layout.PatchFolder);
            BuildIdentity identity = BuildIdentity.Identify(layout.OpenFiles());
            Assert.Equal("CUSA47999", identity.TitleId);
            Assert.Equal("01.01", identity.AppVersion);
            Assert.Equal("gp", identity.Category);
        }
    }

    private static void ChangeMetadataCase(string folder)
    {
        File.Move(Path.Combine(folder, "sce_sys", "param.sfo"), Path.Combine(folder, "sce_sys", "PaRaM.SfO"));
        Directory.Move(Path.Combine(folder, "sce_sys"), Path.Combine(folder, "ScE_SyS"));
    }

    [Theory]
    [InlineData("01.01", "01.00", "01.01", false)]
    [InlineData("01.01", "01.01", "01.01", true)]
    [InlineData("01.01", "01.01", "01.00", true)]
    [InlineData("01.01", "01.00", "01.00", true)]
    [InlineData("01.01", "01.00", "01.02", false)]
    [InlineData("01.01", "01.02", "01.02", false)]
    [InlineData("01.10", "01.09", "01.10", false)]
    [InlineData("10.00", "09.99", "10.00", false)]
    [InlineData("01.01", null, "01.01", true)]
    [InlineData("01.01", null, "01.02", false)]
    [InlineData("01.01", "01.00", null, true)]
    [InlineData("01.01", null, null, true)]
    [InlineData("01.01", "", "01.01", true)]
    [InlineData("01.01", " 01.00", "01.01", true)]
    [InlineData("01.01", "+1.00", "01.01", true)]
    [InlineData("01.01", "01.0", "01.01", true)]
    [InlineData("01.01", "01.00.0", "01.01", true)]
    [InlineData("01.01", "garbage", "01.01", true)]
    [InlineData("01.01", "01.00", "", true)]
    [InlineData("01.01", "01.00", "garbage", false)]
    public void KnownExecutableDoesNotOverrideExplicitlyNewerPatchMetadata(string knownVersion, string? baseVersion, string? patchVersion, bool ignored)
    {
        string game = Game("CUSA47999");
        string patch = Patch("CUSA47999-patch");
        WriteVersion(game, baseVersion, "gd");
        WriteVersion(patch, patchVersion, "gp");
        ChangeMetadataCase(game);
        ChangeMetadataCase(patch);

        // Supply the known identity without embedding a retail executable or changing the registry.
        var decide = typeof(GameLayoutResolver).GetMethod("CurrentBaseVersion", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<string, string, KnownBuild, string?>>();
        KnownBuild known = KnownBuilds.Verified with { AppVersion = knownVersion };
        Assert.Equal(ignored ? knownVersion : null, decide(game, patch, known));
    }

    private static void WriteVersion(string folder, string? version, string category)
    {
        byte[] sfo = version is null
            ? GameFolderTests.Sfo(("CATEGORY", category), ("TITLE_ID", "CUSA47999"))
            : GameFolderTests.Sfo(("APP_VER", version), ("CATEGORY", category), ("TITLE_ID", "CUSA47999"));
        File.WriteAllBytes(Path.Combine(folder, "sce_sys", "param.sfo"), sfo);
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    [Fact(Skip = "Linux directory permissions.", SkipUnless = nameof(IsLinux))]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void AnUnreadableSiblingDoesNotHideTheGame()
    {
        string blocked = Dir("permissions", "unreadable");
        string game = Game(Path.Combine("permissions", "CUSA47999"));
        UnixFileMode mode = File.GetUnixFileMode(blocked);
        try
        {
            File.SetUnixFileMode(blocked, UnixFileMode.None);
            GameLayout layout = GameLayoutResolver.Resolve(Path.Combine(_root, "permissions"));
            Assert.Equal(GameLayoutKind.Game, layout.Kind);
            Assert.Equal(game, layout.BaseFolder);
        }
        finally
        {
            File.SetUnixFileMode(blocked, mode);
        }
    }

    [Fact(Skip = "Linux linked assets.", SkipUnless = nameof(IsLinux))]
    public void CaseVariantLinkedAssetsOutsideTheChosenFolderStillPairAndRead()
    {
        string assets = Game("assets");
        string game = Dir("linked", "CUSA47999");
        File.CreateSymbolicLink(Path.Combine(game, "EBOOT.BIN"), Path.Combine(assets, "eboot.bin"));
        Directory.CreateSymbolicLink(Path.Combine(game, "IMAGE"), Path.Combine(assets, "image"));
        Directory.CreateSymbolicLink(Path.Combine(game, "SCE_SYS"), Path.Combine(assets, "sce_sys"));
        string patch = Patch(Path.Combine("linked", "CUSA47999-patch"));

        GameLayout layout = GameLayoutResolver.Resolve(game);
        Assert.Equal(GameLayoutKind.GameWithUpdate, layout.Kind);
        Assert.Equal(game, layout.BaseFolder);
        Assert.Equal(patch, layout.PatchFolder);
        var files = new DirectoryGameFiles(game);
        byte[] eboot = File.ReadAllBytes(Path.Combine(assets, "eboot.bin"));
        Assert.Equal(eboot.Length, files.Length("eboot.bin"));
        Assert.Equal(eboot, files.ReadAll("eboot.bin"));
        Assert.Equal("01.00", BuildIdentity.Identify(files).AppVersion);
        Assert.Equal("patched allscr", Encoding.ASCII.GetString(layout.OpenFiles().ReadAll("image/allscr.mrg")));
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
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(root, GameLayoutResolver.Resolve(root.TrimEnd('\\', '/')).Chosen);
        }
        else
        {
            Assert.Equal(GameLayoutKind.NotSet, GameLayoutResolver.Resolve("").Kind);
        }
        Assert.Equal(root, GameLayoutResolver.Resolve("\"" + root + "\"").Chosen);

        // an ordinary folder loses its trailing separator, with or without quotes, and a run of separators alone is nothing
        string game = Game("CUSA47999");
        Assert.Equal(game, GameLayoutResolver.Resolve(game + "\\").Chosen);
        Assert.Equal(game, GameLayoutResolver.Resolve("\"" + game + "/\"").Chosen);
        Assert.Equal(GameLayoutKind.Game, GameLayoutResolver.Resolve(game + "\\").Kind);
        Assert.Equal(GameLayoutKind.NotSet, GameLayoutResolver.Resolve("\\").Kind);
        Assert.Equal(GameLayoutKind.NotSet, GameLayoutResolver.Resolve("  //  ").Kind);
        if (!OperatingSystem.IsWindows())
        {
            string withBackslash = Game("literal\\");
            Assert.Equal(withBackslash, GameLayoutResolver.Resolve(withBackslash).BaseFolder);
        }
    }
}
