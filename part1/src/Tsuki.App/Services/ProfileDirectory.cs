namespace Tsuki.App.Services;

/// <summary>The default profile shared by settings, saves and crash logging.</summary>
internal static class ProfileDirectory
{
    public static string ForUser() => ForUser(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
        OperatingSystem.IsWindows(),
        OperatingSystem.IsMacOS());

    /// <summary>The profile decision for one platform (tests); a missing application-data folder falls back to the native home convention.</summary>
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
