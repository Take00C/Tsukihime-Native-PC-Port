// The whole window content: sidebar, title bar buttons, the page on show, footer, overlays (package K17). The window itself only hosts it, so that the offscreen render
// check can draw the same visual tree.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Tsuki.Launcher.Core;
using Tsuki.Launcher.Views;

namespace Tsuki.Launcher;

/// <summary>The launcher's visual tree below the window frame.</summary>
public partial class Shell : UserControl
{
    private readonly Dictionary<LauncherPage, FrameworkElement> _pages = [];
    private LauncherModel? _model;

    /// <summary>Creates the shell.</summary>
    public Shell()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_model is not null)
            {
                _model.PropertyChanged -= OnModelChanged;
            }

            _model = e.NewValue as LauncherModel;
            if (_model is not null)
            {
                _model.PropertyChanged += OnModelChanged;
                ShowPage(_model.Page, animate: false);
            }
        };
    }

    /// <summary>Slide the page in when it changes (off for the offscreen render, where no clock runs).</summary>
    public bool Animate { get; set; } = true;

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LauncherModel.Page) && _model is not null)
        {
            ShowPage(_model.Page, Animate);
        }

        if (e.PropertyName is nameof(LauncherModel.ConfirmVisible) && _model is { ConfirmVisible: true })
        {
            // the question takes the keyboard: Cancel is focused (Enter still answers yes through IsDefault, Esc no through IsCancel), and the content behind is disabled by its style
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => ConfirmCancelButton.Focus());
        }
    }

    private void ShowPage(LauncherPage page, bool animate)
    {
        if (!_pages.TryGetValue(page, out FrameworkElement? view))
        {
            view = page switch
            {
                LauncherPage.Play => new PlayPage(),
                LauncherPage.GameFiles => new GameFilesPage(),
                LauncherPage.Display => new DisplayPage(),
                LauncherPage.Language => new LanguagePage(),
                LauncherPage.Controls => new ControlsPage(),
                LauncherPage.Saves => new SavesPage(),
                _ => new HelpPage(),
            };
            _pages[page] = view;
        }

        PageHost.Content = view;
        if (animate)
        {
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            var slide = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            PageHost.BeginAnimation(OpacityProperty, fade);
            PageShift.BeginAnimation(TranslateTransform.YProperty, slide);
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } w)
        {
            SystemCommands.MinimizeWindow(w);
        }
    }

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } w)
        {
            if (w.WindowState == WindowState.Maximized)
            {
                SystemCommands.RestoreWindow(w);
            }
            else
            {
                SystemCommands.MaximizeWindow(w);
            }
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } w)
        {
            SystemCommands.CloseWindow(w);
        }
    }

    /// <summary>Shows the maximise or restore glyph.</summary>
    public void SetMaximized(bool maximized) => MaxButton.Content = maximized ? "" : "";
}
