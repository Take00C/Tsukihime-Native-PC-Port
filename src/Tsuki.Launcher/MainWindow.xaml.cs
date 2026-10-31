using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher;

/// <summary>The launcher window: custom frame, the shell inside, the folder and file dialogs.</summary>
public partial class MainWindow : Window
{
    private readonly LauncherModel _model;

    /// <summary>Creates the window over a model.</summary>
    public MainWindow(LauncherModel model)
    {
        _model = model;
        InitializeComponent();
        // the default 1200 x 780 DIPs do not fit a 1080p screen at 150 % or a 1366 x 768 one at 125 %: the window starts no larger than the work area (every page
        // scrolls, so nothing needs the full height) and the minimum lets it shrink to 960 x 600 (package R1)
        Rect work = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, work.Width - 24));
        Height = Math.Max(MinHeight, Math.Min(Height, work.Height - 24));
        DataContext = model;
        ShellView.DataContext = model;
        // the icon is the .ico of the executable (package K20: the same picture Explorer and the taskbar show for TsukihimeLauncher.exe); the drawn crescent is the fallback,
        // and the Arcueid icon built from the player's files replaces both while the game folder is valid (package K23)
        Icon ??= MoonIcon.Create();
        _crescent = Icon;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(LauncherModel.GameIconPath))
            {
                ApplyGameIcon(model.GameIconPath);
            }
        };
        ApplyGameIcon(model.GameIconPath);
        StateChanged += (_, _) =>
        {
            // a maximised borderless window is larger than the screen by its resize border
            Frame.Margin = WindowState == WindowState.Maximized ? new Thickness(SystemParameters.WindowResizeBorderThickness.Left + 1) : default;
            ShellView.SetMaximized(WindowState == WindowState.Maximized);
        };
        model.BrowseRequested += OnBrowse;
        model.ImportRequested += OnImport;
        // the launcher comes back the way it was (a maximised one stays maximised) and takes the foreground only when the game failed and the toast needs eyes
        WindowState before = WindowState.Normal;
        model.GameStarted += () =>
        {
            before = WindowState;
            WindowState = WindowState.Minimized;
        };
        model.GameExited += code =>
        {
            WindowState = before == WindowState.Minimized ? WindowState.Normal : before;
            if (code != 0)
            {
                Activate();
            }
        };
    }

    private readonly ImageSource _crescent;
    private string? _gameIconApplied;

    /// <summary>The icon on show: the cached Arcueid .ico (its frames; WPF picks the title-bar and taskbar sizes), else the crescent.</summary>
    public string? GameIconApplied => _gameIconApplied;

    private void ApplyGameIcon(string? path)
    {
        if (string.Equals(path, _gameIconApplied, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ImageSource? icon = path is null ? null : GameIcon.Load(path);
        _gameIconApplied = icon is null ? null : path;
        Icon = icon ?? _crescent;
    }

    private async void OnBrowse()
    {
        var dialog = new OpenFolderDialog { Title = "Choose the CUSA47999 folder (it holds eboot.bin and image\\)", Multiselect = false };
        string current = GameFolderCheck.Resolve(_model.FolderText);
        if (Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(this) == true)
        {
            await _model.SetGameFolderAsync(dialog.FolderName);
        }
    }

    private void OnImport()
    {
        var dialog = new OpenFileDialog { Title = "Choose a PS4 save image (savedata000)", CheckFileExists = true, Filter = "PS4 save image (4 MiB)|*|All files|*.*" };
        string? shad = _model.ShadImage;
        if (shad is not null && Path.GetDirectoryName(shad) is { } dir)
        {
            dialog.InitialDirectory = dir;
        }

        if (dialog.ShowDialog(this) == true)
        {
            _model.AskImport(dialog.FileName);
        }
    }
}

/// <summary>The Arcueid icon file as a WPF image (package K23): the first frame of the .ico with its decoder attached, so the window picks the frame of each size.</summary>
public static class GameIcon
{
    /// <summary>Loads <paramref name="path"/> (read whole, the file is not kept open); null when it is missing or not an icon.</summary>
    public static BitmapFrame? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var decoder = new IconBitmapDecoder(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
            return decoder.Frames.Count == 0 ? null : decoder.Frames[0];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or UriFormatException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>The fallback window icon: the crescent, drawn once at run time, for a build whose .ico resource could not be loaded (the shipped one is assets/icon/tsukihime.ico, package K20).</summary>
public static class MoonIcon
{
    /// <summary>A 64 x 64 bitmap of the crescent on a dark disc.</summary>
    public static BitmapSource Create()
    {
        var group = new DrawingGroup();
        using (DrawingContext dc = group.Open())
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x0E, 0x15, 0x32)), new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0x8F, 0xA6, 0xFF)), 1.5), new Point(32, 32), 30, 30);
            var brush = new LinearGradientBrush(Color.FromRgb(0x9C, 0xD2, 0xFF), Color.FromRgb(0x5E, 0x8B, 0xFF), 90);
            var moon = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(new Point(28, 33), 16.5, 16.5), new EllipseGeometry(new Point(36, 28), 14, 14));
            dc.DrawGeometry(brush, null, moon);
        }

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawDrawing(group);
        }

        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
