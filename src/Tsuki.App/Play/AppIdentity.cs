// The port's identity on the player's machine (package K20): the product name, the executable's name, the window title in the game's language (from the user's own
// param.sfo), the embedded icon for the window, and the language the start-up messages use before the game data is open.
using System.Globalization;
using System.Reflection;
using Tsuki.App.Services;
using Tsuki.Data;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;
using Tsuki.Ui;

namespace Tsuki.App.Play;

/// <summary>Name, title, icon and start-up language of the game program.</summary>
internal static class AppIdentity
{
    /// <summary>"Tsukihime": the name of the program as the player sees it (dialog titles, the launcher, the shortcuts). The project and namespace names stay Tsuki.App.</summary>
    public const string ProductName = GameTitles.ProductName;

    /// <summary>The file name of the game program (the project's AssemblyName).</summary>
    public const string ExecutableName = "Tsukihime.exe";

    /// <summary>The name of the embedded 64 x 64 RGBA icon (assets/icon/tsukihime_64.rgba, drawn by tools/setup/make_icon.py).</summary>
    public const string IconResourceName = "Tsuki.App.Icon64.rgba";

    /// <summary>The side of the embedded icon.</summary>
    public const int IconSize = 64;

    /// <summary>The window title for <paramref name="language"/>: the game's own name from <c>sce_sys/param.sfo</c> of the open folder, else the analysed copy's.</summary>
    public static string WindowTitle(GameFolderSource? source, Language language) =>
        source is null ? GameTitles.Fallback(language) : source.Title(language);

    /// <summary>The embedded icon's pixels (straight RGBA, row-major, <see cref="IconSize"/> squared); null when the resource is not in the assembly.</summary>
    public static byte[]? EmbeddedIcon()
    {
        using Stream? stream = typeof(AppIdentity).Assembly.GetManifestResourceStream(IconResourceName);
        if (stream is null || stream.Length != IconSize * IconSize * 4)
        {
            return null;
        }

        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// The language of the messages the game shows before (or without) its data: <c>--lang</c>, else the launcher's pending <c>language=</c> line, else the profile's
    /// config block (the language the player reads the game in), else the Windows display language mapped to one of the four (<see cref="PortText.FromCultureName"/>).
    /// </summary>
    public static Language StartupLanguage(AppArgs args, AppSettingsData settings, SaveStore? store)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Language is { } forced && IsSelectable((Language)forced))
        {
            return (Language)forced;
        }

        if (settings.GameLanguage is { } pending && IsSelectable(pending))
        {
            return pending;
        }

        if (store is not null && store.SystemData.Block.Config.Language is >= 0 and <= 3 and var saved)
        {
            return (Language)saved;
        }

        return PortText.FromCultureName(CultureInfo.CurrentUICulture.Name);
    }

    private static bool IsSelectable(Language language) => language is Language.Ja or Language.En or Language.Zc or Language.Zt;

    /// <summary>The assembly's informational version for the log ("1.0.0" when none).</summary>
    public static string Version =>
        typeof(AppIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppIdentity).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>
    /// True when this is a PERSONAL build (package K28: built with <c>-p:TsukiPersonalIcon=...</c>, so the executable carries the Arcueid icon cut from the owner's
    /// copy of the game and must never be shared): the assembly carries <c>AssemblyMetadata("TsukiPersonalBuild", "true")</c> (src/Tsuki.PersonalIcon.targets).
    /// </summary>
    public static bool IsPersonalBuild =>
        typeof(AppIdentity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(a => a.Key == Tsuki.Data.Icons.PeIcons.PersonalBuildKey && string.Equals(a.Value, "true", StringComparison.OrdinalIgnoreCase));
}
