namespace Tsuki.App.Services;

/// <summary>The default profile shared by settings, saves and crash logging.</summary>
internal static class ProfileDirectory
{
    internal const string PortableFolderName = "save";

    public static string ForUser() => Select(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
        OperatingSystem.IsWindows(),
        OperatingSystem.IsMacOS(),
        AppContext.BaseDirectory);

    internal static string Select(string applicationData, string home, bool windows, bool macOs, string programDirectory) =>
        !macOs && Directory.Exists(PortableFolder(programDirectory)) ? PortableFolder(programDirectory) : ForUser(applicationData, home, windows, macOs);

    /// <summary>The per-user profile, without the Windows / Linux portable-folder rule.</summary>
    internal static string PerUser() => ForUser(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
        OperatingSystem.IsWindows(),
        OperatingSystem.IsMacOS());

    internal static string PortableFolder(string programDirectory) => Path.Combine(programDirectory, PortableFolderName);

    /// <summary>True when <paramref name="profile"/> is the portable folder beside the programs in <paramref name="programDirectory"/>.</summary>
    internal static bool IsPortable(string profile, string programDirectory)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(PortableFolder(programDirectory))),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The per-user profile decision for one platform (tests); a missing application-data folder falls back to the native home convention.</summary>
    internal static string ForUser(string applicationData, string home, bool windows, bool macOs)
    {
        string root = applicationData;
        if (!Path.IsPathFullyQualified(root))
        {
            if (!Path.IsPathFullyQualified(home))
            {
                throw new IOException("The user profile has no absolute path. Use --save-dir to choose a profile.");
            }

            root = windows
                ? Path.Combine(home, "AppData", "Roaming")
                : macOs ? Path.Combine(home, "Library", "Application Support") : Path.Combine(home, ".config");
        }

        return Path.Combine(root, "Tsuki");
    }
}
