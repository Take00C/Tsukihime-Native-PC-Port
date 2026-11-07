using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher.Avalonia;

public sealed class UnixLauncherApp : Application
{
    /// <summary>The sentence of a second launcher on the same profile.</summary>
    public const string AlreadyOpenText = "The Tsukihime launcher is already open.";

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new global::Avalonia.Themes.Fluent.FluentTheme());
        Resources.MergedDictionaries.Add(new Themes.Theme());
        Styles.Add(new Themes.Controls());
        Resources["StateMark"] = new ReadyStateConverter();
        Resources["PathData"] = new PathDataConverter();
        if (OperatingSystem.IsMacOS()) { UseMacMenu(this); }
        base.Initialize();
    }

    /// <summary>
    /// macOS: the app menu gets the launcher's own item, so that Avalonia does not put in its default one ("About Avalonia", which opens the toolkit's about box);
    /// the system adds Hide, Show All and Quit to it.
    /// </summary>
    internal static void UseMacMenu(Application app)
    {
        app.Name = "Tsukihime";
        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem("About Tsukihime") { Command = new RelayCommand(_ => { if (Model is { } model) { model.Page = LauncherPage.Help; } }) });
        NativeMenu.SetMenu(app, menu);
    }

    public static UnixLauncherModel? Model { get; set; }

    /// <summary>Another launcher holds the profile's lock: this one shows only <see cref="AlreadyOpenText"/>.</summary>
    public static bool AlreadyOpen { get; set; }

    /// <summary>The version an update restarted this launcher from (--updated-from): the window says "Updated from" once it shows.</summary>
    public static ReleaseVersion? UpdatedFrom { get; set; }

    internal Window CreateMainWindow()
    {
        if (AlreadyOpen) { return AlreadyOpenWindow(); }
        MainWindow window = Model is { } model ? new MainWindow(model) : new MainWindow();
        if (UpdatedFrom is { } from && Model is { } updated)
        {
            window.Opened += (_, _) => updated.SayUpdated(from);
        }

        return window;
    }

    /// <summary>A small window that says another launcher is open (a second start of the desktop entry, say) and closes.</summary>
    internal static Window AlreadyOpenWindow()
    {
        var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0), IsDefault = true, IsCancel = true };
        ok.Classes.Add("GhostButton");
        var window = new Window
        {
            Title = "Tsukihime Launcher",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Brush.Parse("#121A3A"),
            Content = new StackPanel
            {
                Margin = new Thickness(24, 22),
                Children = { new TextBlock { Text = AlreadyOpenText, TextWrapping = TextWrapping.Wrap }, ok },
            },
        };
        ok.Click += (_, _) => window.Close();
        return window;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = CreateMainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
