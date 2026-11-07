using System.Runtime.InteropServices;
using Tsuki.Cli.Commands;
using Tsuki.Launcher.Avalonia;
using Tsuki.Launcher.Core;
using Tsuki.Tests.Data.Cli;
using Tsuki.Tests.Synthetic.Launcher;

namespace Tsuki.Tests.Avalonia;

/// <summary>The macOS/Linux launcher's gates before anything is downloaded: a personal build asks nothing, a folder it cannot write downloads nothing.</summary>
public sealed class UnixUpdateGateTests : IDisposable
{
    private readonly string _root = UnixUpdateTestKit.TempRoot("tsuki-k48-gates-");
    private static ReleaseVersion Version => ReleaseFiles.BuildVersion();

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (string folder in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories).Prepend(_root))
            {
                Chmod(folder, ReleaseRules.Mode755);
            }
        }

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task APersonalBuildNeverAsksGitHub()
    {
        string install = LinuxRelease.Folder(_root, "personal");
        File.WriteAllText(Path.Combine(install, UpdateInstaller.PersonalNote), "personal");
        using var server = new FakeGitHub();
        Serve(server, install);
        UnixLauncherModel model = Model(install, server.Base);
        Assert.True(model.UpdatesOff);
        await model.CheckForUpdatesAsync(manual: true);
        await model.PressUpdateButtonAsync();
        model.CheckOnStart();
        Assert.Null(model.AvailableUpdate);
        Assert.Equal("Personal build: updates are off.", model.UpdateStatus);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task AFolderTheLauncherCannotWriteDownloadsNothing()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("a read-only folder is a Unix permission"); }
        string install = LinuxRelease.Folder(_root, "readonly");
        // a staging folder the account may write sits inside the read-only folder: only the folder check itself stops the download
        Directory.CreateDirectory(new UpdateStaging(install).Root);
        using var server = new FakeGitHub();
        string asset = Serve(server, install);
        UnixLauncherModel model = Model(install, server.Base);
        model.RestartRequested = (_, _) => throw new InvalidOperationException("no restart is expected");
        await model.CheckForUpdatesAsync(manual: true);
        Assert.NotNull(model.AvailableUpdate);
        Chmod(install, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        if (CanWriteIn(install)) { Assert.Skip("this account ignores folder permissions (root)"); }
        await model.RunUpdateAsync();
        Assert.Equal(UnixUpdateStage.Failed, model.UpdateStage);
        Assert.NotEmpty(model.UpdateMessage);
        Assert.DoesNotContain(server.Requests, request => request.Path.StartsWith("/assets/", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Requests, request => request.Path.EndsWith("/" + asset, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFolderThatCannotBeChangedIsNamedWithoutATrailingSeparator()
    {
        // review launcher-ui-8: the launcher's folder is AppContext.BaseDirectory, which ends in '/', and the sentence read ".../install/."
        string missing = Path.Combine(_root, "not-there", "install");
        using var server = new FakeGitHub();
        string asset = Serve(server, missing);
        UnixLauncherModel model = Model(missing + Path.DirectorySeparatorChar, server.Base);
        model.RestartRequested = (_, _) => throw new InvalidOperationException("no restart is expected");
        await model.CheckForUpdatesAsync(manual: true);
        await model.RunUpdateAsync();
        Assert.Equal(UnixUpdateStage.Failed, model.UpdateStage);
        Assert.StartsWith($"The launcher's folder cannot be changed by your account: {missing}. Download {asset} from the Releases page", model.UpdateMessage, StringComparison.Ordinal);
    }

    private string Serve(FakeGitHub server, string install)
    {
        string name = PlatformUpdate.AssetNameFor(Version, "linux", Architecture.X64);
        string archive = LinuxRelease.Tar(LinuxRelease.Folder(_root, Path.GetFileName(install) + "-release"), Path.Combine(_root, Path.GetFileName(install) + "-assets", name));
        string sums = ReleaseFiles.Sums(Path.Combine(Path.GetDirectoryName(archive)!, UpdateSource.SumsAssetName), archive);
        server.List(UpdateSource.DefaultRepository, server.Release(UpdateSource.DefaultRepository, Version.Tag, [(name, archive), (UpdateSource.SumsAssetName, sums)]));
        return name;
    }

    private UnixLauncherModel Model(string install, Uri api) => new(new LauncherPaths(Path.Combine(_root, Path.GetFileName(install) + "-profile")), install, Path.Combine(_root, "no-game"))
    {
        UpdateSource = UpdateSource.ForLoopback(api),
        AssetName = version => PlatformUpdate.AssetNameFor(version, "linux", Architecture.X64),
        CurrentVersionOverride = new ReleaseVersion(0, 0, 1),
        MacBundleUpdate = false,
    };

    private static void Chmod(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, mode); }
    }

    private static bool CanWriteIn(string folder)
    {
        string probe = Path.Combine(folder, ".probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
