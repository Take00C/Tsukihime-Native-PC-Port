namespace Tsuki.App.Services;

/// <summary>The default profile shared by settings, saves and crash logging.</summary>
internal static class ProfileDirectory
{
    public static string ForUser()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (!Path.IsPathFullyQualified(root))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
            if (!Path.IsPathFullyQualified(home))
            {
                throw new IOException("The user profile has no absolute path. Use --save-dir to choose a profile.");
            }

            root = OperatingSystem.IsWindows() ? Path.Combine(home, "AppData", "Roaming") : Path.Combine(home, ".config");
        }

        return Path.Combine(root, "Tsuki");
    }
}
