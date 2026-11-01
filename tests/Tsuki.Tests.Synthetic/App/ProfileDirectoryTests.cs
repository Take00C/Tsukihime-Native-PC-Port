using Tsuki.App;
using Tsuki.App.Play;
using Tsuki.App.Services;

namespace Tsuki.Tests.Synthetic.App;

[CollectionDefinition("Profile environment", DisableParallelization = true)]
public sealed class ProfileEnvironmentCollection;

[Collection("Profile environment")]
public sealed class ProfileDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-profile-" + Guid.NewGuid().ToString("N"));
    private readonly string? _home = Environment.GetEnvironmentVariable("HOME");
    private readonly string? _config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private readonly string _workingDirectory = Environment.CurrentDirectory;

    public static bool IsLinux => OperatingSystem.IsLinux();

    public static bool IsWindows => OperatingSystem.IsWindows();

    public ProfileDirectoryTests()
    {
        Directory.CreateDirectory(_root);
        Environment.CurrentDirectory = _root;
        if (IsLinux)
        {
            Environment.SetEnvironmentVariable("HOME", Path.Combine(_root, "home"));
        }
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _workingDirectory;
        Environment.SetEnvironmentVariable("HOME", _home);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _config);
        Directory.Delete(_root, recursive: true);
    }

    [Fact(Skip = "XDG profiles are a Linux convention.", SkipUnless = nameof(IsLinux))]
    public void CrashLoggingCreatesAnAbsentXdgDirectoryBeforeSettingsLoad()
    {
        string config = Path.Combine(_root, "new-crash-config");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        Assert.False(Directory.Exists(config));

        string? path = CrashLog.Append(null, null, "early crash test");

        Assert.Equal(Path.Combine(config, "Tsuki", CrashLog.FileName), path);
        Assert.False(Directory.Exists(Path.Combine(_root, "Tsuki")));
    }

    [Fact(Skip = "XDG profiles are a Linux convention.", SkipUnless = nameof(IsLinux))]
    public void MissingXdgDirectoryIsCreatedForSettingsAndCrashLogging()
    {
        string config = Path.Combine(_root, "new-config");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        Assert.False(Directory.Exists(config));
        Assert.True(AppArgs.TryParse([], out AppArgs? args, out _));

        string profile = StartupSettings.DataDirectory(args!);

        Assert.Equal(Path.Combine(config, "Tsuki"), profile);
        Assert.True(Path.IsPathFullyQualified(profile));
        Assert.True(Directory.Exists(profile));
        Assert.Equal(Path.Combine(profile, AppSettings.FileName), StartupSettings.Load(args!).Path);
        Assert.Equal(Path.Combine(profile, CrashLog.FileName), CrashLog.Append(null, null, "profile test"));
    }

    [Theory(Skip = "XDG profiles are a Linux convention.", SkipUnless = nameof(IsLinux))]
    [InlineData(null)]
    [InlineData("relative-config")]
    public void CrashLogCreatesTheHomeFallbackWithoutDependingOnTheWorkingDirectory(string? config)
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        string expected = Path.Combine(_root, "home", ".config", "Tsuki");

        Assert.Equal(expected, ProfileDirectory.ForUser());
        Assert.Equal(Path.Combine(expected, CrashLog.FileName), CrashLog.Append(null, null, "fallback test"));
        Assert.True(Directory.Exists(expected));
    }

    [Fact]
    public void ExplicitProfilePathsKeepTheirExistingMeaning()
    {
        Assert.True(AppArgs.TryParse(["--save-dir", "relative-profile"], out AppArgs? args, out _));
        Assert.Equal("relative-profile", StartupSettings.DataDirectory(args!));
        string explicitDirectory = Path.Combine(_root, "explicit");
        Assert.Equal(Path.Combine(explicitDirectory, CrashLog.FileName), CrashLog.Append(explicitDirectory, null, "explicit test"));
    }

    [Fact(Skip = "Windows uses its existing roaming profile.", SkipUnless = nameof(IsWindows))]
    public void WindowsKeepsTheRoamingProfile()
    {
        string existing = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        Assert.Equal(Path.Combine(existing, "Tsuki"), ProfileDirectory.ForUser());
    }
}
