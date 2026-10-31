using System.Diagnostics;
using Tsuki.Data;
using Tsuki.Data.Eboot;
using Tsuki.Data.IO;
using Tsuki.Data.Verification;
using Tsuki.Launcher.Core;
using Tsuki.Tests.Data.Catalog;

namespace Tsuki.Tests.Data.Source;

/// <summary>
/// Package K25 on the real game, through directory junctions under <c>scratch/k25/layouts</c> that point INTO the game folder (read-only; the 44 GB are never
/// copied): the folder above the game, two levels above, the base game with the update folder beside it (the base eboot made unknown so that the update's wins), a
/// wrong folder, a .pkg next to nothing, and a copy of eboot.bin with one table damaged. Each layout is resolved, inspected the launcher's way, opened the game's way,
/// and the game itself is started hidden on the ones that should run.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class GameLayoutRealDataTests : IClassFixture<GameLayoutRealDataTests.Layouts>
{
    /// <summary>The junction layouts, made once per test class and removed afterwards (the junctions are deleted as links; the game folder is never touched).</summary>
    public sealed class Layouts : IDisposable
    {
        private readonly List<string> _junctions = [];

        public Layouts()
        {
            string game = GameFolder.Require();
            Root = Path.Combine(GoldenData.RepoRoot, "scratch", "k25", "layouts", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);

            // the folder above the game, and two levels above
            Parent = Path.Combine(Root, "parent");
            Directory.CreateDirectory(Parent);
            Junction(Path.Combine(Parent, "CUSA47999"), game);
            Nested = Path.Combine(Root, "nested");
            Directory.CreateDirectory(Path.Combine(Nested, "dumps"));
            Junction(Path.Combine(Nested, "dumps", "CUSA47999"), game);

            // the base game with the update beside it: a real folder whose data folders are junctions into the game and whose eboot is a copy with one code byte
            // flipped (an eboot the port does not know), so that the update's eboot must win; the update is this machine's own update folder when it has one
            Split = Path.Combine(Root, "split");
            SplitBase = Path.Combine(Split, "CUSA47999");
            Directory.CreateDirectory(SplitBase);
            foreach (string sub in new[] { "image", "sce_sys", "data", "sce_module" })
            {
                if (Directory.Exists(Path.Combine(game, sub)))
                {
                    Junction(Path.Combine(SplitBase, sub), Path.Combine(game, sub));
                }
            }

            byte[] eboot = SharedEboot.Data;
            byte[] unknown = (byte[])eboot.Clone();
            unknown[0x2280 + 0x1000] ^= 0x01;                                  // text VA 0x1000: code, no table
            File.WriteAllBytes(Path.Combine(SplitBase, "eboot.bin"), unknown);
            string? realPatch = GameLayoutResolver.FindPatchBeside(game);
            if (realPatch is not null)
            {
                SplitPatch = Path.Combine(Split, "CUSA47999-patch");
                Junction(SplitPatch, realPatch);
            }

            // a wrong folder, a .pkg next to nothing
            Wrong = Path.Combine(Root, "wrong");
            Directory.CreateDirectory(Wrong);
            File.WriteAllText(Path.Combine(Wrong, "readme.txt"), "nothing here");
            Pkg = Path.Combine(Root, "pkg", "Tsukihime-CUSA47999.pkg");
            Directory.CreateDirectory(Path.GetDirectoryName(Pkg)!);
            File.WriteAllBytes(Pkg, new byte[64]);

            // a damaged eboot: the haze table's head zeroed, the rest of the game through junctions
            Damaged = Path.Combine(Root, "damaged");
            Directory.CreateDirectory(Damaged);
            foreach (string sub in new[] { "image", "sce_sys", "data" })
            {
                Junction(Path.Combine(Damaged, sub), Path.Combine(game, sub));
            }

            byte[] damaged = (byte[])eboot.Clone();
            damaged.AsSpan(0x2280 + (int)EbootLayout.Analysed.Haze + 40, 40).Clear();
            File.WriteAllBytes(Path.Combine(Damaged, "eboot.bin"), damaged);
        }

        public string Root { get; }

        public string Parent { get; }

        public string Nested { get; }

        public string Split { get; }

        public string SplitBase { get; }

        public string? SplitPatch { get; }

        public string Wrong { get; }

        public string Pkg { get; }

        public string Damaged { get; }

        private void Junction(string link, string target)
        {
            using Process p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit();
            if (p.ExitCode != 0 || !Directory.Exists(link))
            {
                throw new InvalidOperationException($"mklink /J {link} -> {target} failed: {p.StandardError.ReadToEnd()}{p.StandardOutput.ReadToEnd()}");
            }

            _junctions.Add(link);
        }

        public void Dispose()
        {
            // the junctions first, as links (never into the game folder), then the rest
            foreach (string j in _junctions.AsEnumerable().Reverse())
            {
                try
                {
                    Directory.Delete(j, recursive: false);
                }
                catch (IOException)
                {
                }
            }

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private readonly ITestOutputHelper output;
    private readonly Layouts _layouts;

    public GameLayoutRealDataTests(ITestOutputHelper output, Layouts layouts)
    {
        this.output = output;
        _layouts = layouts;
    }

    [GameDataFact]
    public void TheFolderAboveTheGameResolvesToTheGameAndIsReadyAndVerified()
    {
        string game = GameFolder.Require();
        GameLayout parent = GameLayoutResolver.Resolve(_layouts.Parent);
        Assert.True(parent.IsGame);
        Assert.Equal(Path.Combine(_layouts.Parent, "CUSA47999"), parent.BaseFolder);
        Assert.Equal(GameLayoutResolver.Resolve(_layouts.Nested).Kind, parent.Kind);
        Assert.Equal(Path.Combine(_layouts.Nested, "dumps", "CUSA47999"), GameLayoutResolver.Resolve(_layouts.Nested).BaseFolder);

        GameFolderInfo info = GameFolderCheck.Inspect(_layouts.Parent, measureSize: false);
        output.WriteLine($"{info.State}: {info.BuildText}; {info.BuildName}; layout {info.Layout?.Describe()}");
        Assert.Equal(FolderState.Ready, info.State);
        Assert.True(info.IsVerifiedBuild);
        Assert.Equal("verified build", info.BuildText);
        Assert.Equal("CUSA47999", info.TitleId);
        Assert.Equal(GameFolderCheck.AnalysedVersion, info.AppVersion);
        Assert.Equal(info.ChecksTotal, info.ChecksPassed);
        Assert.Equal(4, info.LanguageList.Count);

        using GameFolderSource source = GameFolderSource.Open(_layouts.Parent);
        Assert.Equal(BuildMatch.Verified, source.Identity.Match);
        Assert.Same(KnownBuilds.Verified, source.Identity.Known);
        Assert.Null(source.TableLocations);
        Assert.Equal(549, source.ScriptCount);
        Assert.True(source.Build.IsAnalysedBuild);
        Assert.Equal(GameLayoutKind.Game, source.Layout.Kind);
        _ = game;
    }

    [GameDataFact]
    public void AWrongFolderAPkgAndADamagedEbootAreRefusedWithTheirOwnWords()
    {
        GameFolderInfo wrong = GameFolderCheck.Inspect(_layouts.Wrong, measureSize: false);
        Assert.Equal(FolderState.NotTheGame, wrong.State);
        Assert.Contains("eboot.bin", wrong.Summary, StringComparison.Ordinal);
        Assert.Throws<GameLayoutException>(() => GameFolderSource.Open(GameLayoutResolver.Resolve(_layouts.Wrong)));

        GameFolderInfo pkg = GameFolderCheck.Inspect(_layouts.Pkg, measureSize: false);
        Assert.Equal(FolderState.PkgFile, pkg.State);
        Assert.Contains(".pkg", pkg.Summary, StringComparison.Ordinal);
        Assert.Contains("Extract", pkg.Summary, StringComparison.Ordinal);
        GameLayoutException pkgError = Assert.Throws<GameLayoutException>(() => GameFolderSource.Open(_layouts.Pkg));
        Assert.Equal(GameLayoutKind.PkgFile, pkgError.Layout.Kind);
        Assert.Equal(FolderState.NotTheGame, GameFolderCheck.Inspect(Path.GetDirectoryName(_layouts.Pkg), measureSize: false).State);      // the folder with only the .pkg

        GameFolderInfo damaged = GameFolderCheck.Inspect(_layouts.Damaged, measureSize: false);
        output.WriteLine($"damaged: {damaged.State}; {string.Join(" | ", damaged.ProblemList)}");
        Assert.Equal(FolderState.Incomplete, damaged.State);
        Assert.Contains(damaged.ProblemList, p => p.Contains("Haze", StringComparison.Ordinal) && p.Contains("report it with the log", StringComparison.Ordinal));
        Assert.Equal(BuildMatch.KnownRelease, damaged.Build);
        UnsupportedBuildException e = Assert.Throws<UnsupportedBuildException>(() => GameFolderSource.Open(_layouts.Damaged));
        Assert.Contains("Haze", e.MissingTables);
        Assert.Contains("CUSA47999", e.Message, StringComparison.Ordinal);
        output.WriteLine(e.Message);
    }

    [GameDataFact]
    public void TheBaseGameWithTheUpdateBesideItIsOneGameReadThroughTheUpdate()
    {
        if (_layouts.SplitPatch is null)
        {
            // no update folder beside the game on this machine: the base alone (with its unknown eboot) must still be found by content
            using GameFolderSource alone = GameFolderSource.Open(_layouts.SplitBase);
            Assert.Equal(BuildMatch.KnownRelease, alone.Identity.Match);
            Assert.NotNull(alone.TableLocations);
            Assert.True(alone.TableLocations!.Complete);
            output.WriteLine("no update folder on this machine; the base with an unknown eboot: " + alone.TableLocations.Summary());
            return;
        }

        foreach (string chosen in new[] { _layouts.Split, _layouts.SplitBase, _layouts.SplitPatch })
        {
            GameLayout layout = GameLayoutResolver.Resolve(chosen);
            Assert.Equal(GameLayoutKind.GameWithUpdate, layout.Kind);
            Assert.Equal(_layouts.SplitBase, layout.BaseFolder);
            Assert.Equal(_layouts.SplitPatch, layout.PatchFolder);
        }

        GameFolderInfo info = GameFolderCheck.Inspect(_layouts.Split, measureSize: false);
        output.WriteLine($"{info.State}: {info.BuildText}; {info.BuildName}; {info.TablesNote}; problems {string.Join(" | ", info.ProblemList)}");
        Assert.Equal(FolderState.Ready, info.State);
        Assert.Equal(_layouts.SplitPatch, info.PatchFolder);
        Assert.Equal("01.01", info.AppVersion);                                              // the update's param.sfo
        Assert.Equal(4, info.LanguageList.Count);

        using GameFolderSource source = GameFolderSource.Open(_layouts.Split);
        output.WriteLine(source.Identity.Report());
        output.WriteLine(source.TableLocations?.Summary() ?? "tables at the documented addresses");
        Assert.Equal(GameLayoutKind.GameWithUpdate, source.Layout.Kind);
        Assert.NotEqual(Tsuki.Engine.Contracts.BuildInfo.AnalysedEbootSha256, source.Build.EbootSha256);                  // the update's eboot, not the base's flipped copy
        byte[] patchEboot = File.ReadAllBytes(Path.Combine(_layouts.SplitPatch, "eboot.bin"));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(patchEboot)), source.Build.EbootSha256);
        Assert.NotNull(source.TableLocations);
        Assert.True(source.TableLocations!.Complete);
        Assert.Equal("01.01", source.ParamSfo?["APP_VER"]);
        Assert.Equal("gp", source.ParamSfo?["CATEGORY"]);
        Assert.Equal(549, source.ScriptCount);
        Assert.Equal(43_969, source.TextLineCount);
        Assert.Equal(14_744, source.Catalog.Images.Count);
        Assert.Equal(134, source.Tables.Movies.Count);
        Assert.Equal(4, source.AvailableLanguages.Count);
        Assert.Equal(_layouts.SplitBase, source.RootDirectory);
        _ = source.Script(3);
        _ = source.TextLine(Tsuki.Engine.Contracts.Language.En, 100);
        _ = source.UiSheet(Tsuki.Engine.Contracts.UiSet.ConfParts, Tsuki.Engine.Contracts.Language.Ja, 0);                           // allpac3 comes from the update
    }

    [GameDataFact]
    public void TheGameStartsHiddenOnTheFolderAboveAndOnTheSplitLayoutAndRefusesTheDamagedOne()
    {
        string app = Path.Combine(AppContext.BaseDirectory, GameLauncher.AppFileName);
        Assert.True(File.Exists(app), "the test output holds Tsukihime.exe");

        string parentLog = Run(app, _layouts.Parent, "parent", out int parentExit);
        Assert.Equal(0, parentExit);
        Assert.Contains("game folder " + Path.Combine(_layouts.Parent, "CUSA47999"), parentLog, StringComparison.Ordinal);
        Assert.Contains("build: known build: " + KnownBuilds.Verified.Name, parentLog, StringComparison.Ordinal);
        Assert.Contains("tables: at the documented addresses", parentLog, StringComparison.Ordinal);
        Assert.Contains("languages: text ja en zc zt", parentLog, StringComparison.Ordinal);

        string damagedLog = Run(app, _layouts.Damaged, "damaged", out int damagedExit);
        Assert.NotEqual(0, damagedExit);
        Assert.Contains("cannot be used", damagedLog, StringComparison.Ordinal);
        Assert.Contains("Haze", damagedLog, StringComparison.Ordinal);
        Assert.Contains("MISSING", damagedLog, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled", damagedLog, StringComparison.Ordinal);

        if (_layouts.SplitPatch is null)
        {
            output.WriteLine("no update folder on this machine: the split run is skipped");
            return;
        }

        string splitLog = Run(app, _layouts.Split, "split", out int splitExit, frames: 600);
        Assert.Equal(0, splitExit);
        Assert.Contains("layout: " + _layouts.SplitBase + " + update folder " + _layouts.SplitPatch, splitLog, StringComparison.Ordinal);
        Assert.Contains("tables: 66 tables located by content", splitLog, StringComparison.Ordinal);
        Assert.Contains("build: known build: CUSA47999 01.01 (1.01 eboot of the 5.05", splitLog, StringComparison.Ordinal);
        Assert.Contains("window title:", splitLog, StringComparison.Ordinal);
        Assert.Contains("window mode:", splitLog, StringComparison.Ordinal);
    }

    /// <summary>Starts the game hidden and muted on <paramref name="folder"/> with its own profile under the layouts root; the log text.</summary>
    private string Run(string app, string folder, string name, out int exitCode, int frames = 120)
    {
        var paths = new LauncherPaths(Path.Combine(_layouts.Root, "profiles", name));
        Directory.CreateDirectory(paths.DataDirectory);
        LaunchPlan plan = GameLauncher.Plan(app, folder, paths, fullscreen: false, ["--hidden", "--mute", "--fast", "--autoplay", "--save-dir", paths.DataDirectory, "--exit-after", frames.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        using Process process = GameLauncher.Start(plan, paths);
        Assert.True(process.WaitForExit(240_000), $"{name}: the game did not exit");
        exitCode = process.ExitCode;
        string log = File.Exists(paths.LastRunLog) ? File.ReadAllText(paths.LastRunLog) : string.Empty;
        output.WriteLine($"--- {name}: exit {exitCode}");
        foreach (string line in log.Split('\n').Where(l => l.Contains("layout:", StringComparison.Ordinal) || l.Contains("build:", StringComparison.Ordinal) || l.Contains("tables:", StringComparison.Ordinal)
                                                        || l.Contains("languages:", StringComparison.Ordinal) || l.Contains("cannot be used", StringComparison.Ordinal) || l.Contains("game folder", StringComparison.Ordinal)))
        {
            output.WriteLine(line.TrimEnd());
        }

        return log;
    }
}
