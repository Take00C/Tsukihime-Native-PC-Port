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
    private bool _refocusUpdateButton;

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
                ApplyUpdateButtonStyle();
            }
        };
        UpdateButton.IsEnabledChanged += OnUpdateButtonEnabledChanged;
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

        if (e.PropertyName is nameof(LauncherModel.UpdateStage) && _model is { UpdateOverlayVisible: true })
        {
            // the update overlay takes the keyboard the same way: Later while it asks, Cancel while it downloads (package K40)
            _refocusUpdateButton = false;
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                if (UpdateCancelButton.IsVisible)
                {
                    UpdateCancelButton.Focus();
                }
                else if (UpdateLaterButton.IsVisible)
                {
                    UpdateLaterButton.Focus();
                }
            });
        }
        else if (e.PropertyName is nameof(LauncherModel.UpdateStage) && _model is { OverlayVisible: false })
        {
            // package K42: Later / Close gives the keyboard back to the footer button, unless something of the page has it already
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                if (_model is { OverlayVisible: false } && !ContentArea.IsKeyboardFocusWithin)
                {
                    UpdateButton.Focus();
                }
            });
        }

        if (e.PropertyName is null or nameof(LauncherModel.UpdateButtonAccent))
        {
            ApplyUpdateButtonStyle();
        }
    }

    /// <summary>The footer button's look (package K42): K40's gold UpdateButton once a newer release is known, else the quiet CheckUpdatesButton.</summary>
    private void ApplyUpdateButtonStyle()
    {
        if (_model is not null && FindResource(_model.UpdateButton.StyleKey) is Style style && !ReferenceEquals(UpdateButton.Style, style))
        {
            UpdateButton.Style = style;
        }
    }

    /// <summary>
    /// The footer button is disabled while its check runs, and a disabled button loses the keyboard: when it had it, it takes it back once it can be pressed again
    /// (package K42), unless the overlay opened in between (the overlay then has the keyboard, and gives it back when it closes).
    /// </summary>
    private void OnUpdateButtonEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            _refocusUpdateButton |= UpdateButton.IsKeyboardFocused;
            return;
        }

        if (_refocusUpdateButton)
        {
            _refocusUpdateButton = false;
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                if (_model is { OverlayVisible: false } && UpdateButton.IsEnabled && !ContentArea.IsKeyboardFocusWithin)
                {
                    UpdateButton.Focus();
                }
            });
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
