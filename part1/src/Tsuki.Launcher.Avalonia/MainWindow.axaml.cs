using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Tsuki.App.Services;
using Tsuki.Launcher.Core;
using Tsuki.Launcher.Avalonia.Views;

namespace Tsuki.Launcher.Avalonia;

public partial class MainWindow : Window
{
    private readonly UnixLauncherModel _model;
    private TaskCompletionSource<bool>? _question;
    private WindowState _beforeGame = WindowState.Normal;
    private bool _docked;
    private string? _dockPicture;

    public MainWindow() : this(new UnixLauncherModel(new LauncherPaths(ProfileDirectory.ForUser()), AppContext.BaseDirectory)) { }

    public MainWindow(UnixLauncherModel model)
    {
        _model = model;
        DataContext = model;
        InitializeComponent();
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } screen) { FitTo(screen.WorkingArea, screen.Scaling); }
        SizeChanged += (_, e) => SidebarNote.IsVisible = e.NewSize.Height >= SidebarNoteMinHeight;
        QuestionYes.Command = new RelayCommand(_ => Answer(true));
        QuestionNo.Command = new RelayCommand(_ => Answer(false));
        ApplyUpdateButtonLook();
        ApplyIcon();
        _model.PropertyChanged += ModelChanged;
        _model.BrowseRequested += BrowseForGame;
        _model.ImportRequested += ChooseSaveImage;
        _model.QuestionRequested += AskAsync;
        _model.GameStarted += OnGameStarted;
        _model.GameExited += OnGameExited;
        // closing the window during an update's download cancels it (nothing of the launcher's folder was changed yet)
        Closing += (_, _) => _model.CancelUpdate();
        // back in front after a game started from the application menu or Steam: the switches show what that game left in settings.ini
        Activated += (_, _) => _model.ReadProfileAgain(onlyIfChanged: true);
        Closed += (_, _) => Detach();
        ShowPage(_model.Page);
        if (global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime)
        {
            _ = _model.RefreshAsync();
            _model.CheckOnStart();
        }
    }

    /// <summary>The window height below which the sidebar drops the version and the fan-made note, so that the seven pages keep their entries.</summary>
    internal const double SidebarNoteMinHeight = 600;

    /// <summary>True while the yes / no question is up.</summary>
    internal bool QuestionVisible => QuestionOverlay.IsVisible;

    /// <summary>
    /// The window starts no larger than the work area of its screen, with room for the frame the window manager adds, as the Windows launcher does: the default
    /// 1200 × 780 does not fit a Steam Deck's desktop (1280 × 800 with a panel, 853 × 504 at 150 %), where PLAY and the update button would sit below the screen.
    /// Every page scrolls, so nothing needs the full height.
    /// </summary>
    internal void FitTo(PixelRect workingArea, double scaling)
    {
        double scale = scaling > 0 ? scaling : 1;
        Width = Math.Max(MinWidth, Math.Min(Width, (workingArea.Width / scale) - 24));
        Height = Math.Max(MinHeight, Math.Min(Height, (workingArea.Height / scale) - 60));
    }

    private void Detach()
    {
        _model.PropertyChanged -= ModelChanged;
        _model.BrowseRequested -= BrowseForGame;
        _model.ImportRequested -= ChooseSaveImage;
        _model.QuestionRequested -= AskAsync;
        _model.GameStarted -= OnGameStarted;
        _model.GameExited -= OnGameExited;
        Answer(false);
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UnixLauncherModel.Page)) { ShowPage(_model.Page); }
        if (e.PropertyName is null or nameof(UnixLauncherModel.UpdateButtonAccent)) { ApplyUpdateButtonLook(); }
        if (e.PropertyName is null or nameof(UnixLauncherModel.GameIcon)) { ApplyIcon(); }
        if (e.PropertyName == nameof(UnixLauncherModel.UpdateStage)) { OverlayChanged(); }
    }

    /// <summary>The footer button: Log's glass with a sync sign ("Check for updates"), or the gold glass with a download sign once a newer release is known.</summary>
    private void ApplyUpdateButtonLook() => UpdateButton.Classes.Set("Gold", _model.UpdateButtonAccent);

    /// <summary>Arcueid's face from the player's files once the game folder is ready; the port's crescent before.</summary>
    private void ApplyIcon()
    {
        try
        {
            Icon = _model.GameIcon is { } face ? new WindowIcon(face) : new WindowIcon(AssetLoader.Open(new Uri("avares://TsukihimeLauncher/Assets/tsukihime_256.png")));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // the window keeps the icon the system gives it
        }

        // macOS draws the Dock tile from the application, not from the window: the face goes there (the Finder icon stays the bundle's), once per picture
        if (_model.IsMac && (!_docked || _dockPicture != _model.GameIconPng))
        {
            (_docked, _dockPicture) = (true, _model.GameIconPng);
            _model.DockIcon(_dockPicture);
        }
    }

    /// <summary>While a question or the update overlay is up, the page and the footer behind take no keys or clicks; the overlay takes the keyboard.</summary>
    private void OverlayChanged()
    {
        bool overlay = _model.UpdateOverlayVisible || QuestionOverlay.IsVisible;
        ContentArea.IsEnabled = !overlay;
        if (!_model.UpdateOverlayVisible) { return; }
        Dispatcher.UIThread.Post(() =>
        {
            if (UpdateCancelButton.IsVisible) { UpdateCancelButton.Focus(); }
            else if (UpdateLaterButton.IsVisible) { UpdateLaterButton.Focus(); }
        }, DispatcherPriority.Input);
    }

    private void ShowPage(LauncherPage page)
    {
        PageHost.Content = page switch
        {
            LauncherPage.GameFiles => new GameFilesPage(),
            LauncherPage.Display => new DisplayPage(),
            LauncherPage.Language => new LanguagePage(),
            LauncherPage.Controls => new ControlsPage(),
            LauncherPage.Saves => new SavesPage(),
            LauncherPage.Help => new HelpPage(),
            _ => new PlayPage(),
        };

        if (PageHost.Content is Control view) { view.DataContext = _model; }
    }

    // the launcher steps aside while the game runs and comes back the way it was; it takes the front only when the game failed and the toast needs eyes
    private void OnGameStarted()
    {
        _beforeGame = WindowState;
        WindowState = WindowState.Minimized;
    }

    private void OnGameExited(int code)
    {
        WindowState = _beforeGame == WindowState.Minimized ? WindowState.Normal : _beforeGame;
        if (code != 0) { Activate(); }
    }

    /// <summary>Shows the question over the window; Cancel has the keyboard (Enter answers yes, Esc no).</summary>
    private Task<bool> AskAsync(LauncherQuestion question)
    {
        _question?.TrySetResult(false);
        var answer = new TaskCompletionSource<bool>();
        _question = answer;
        QuestionTitle.Text = question.Title;
        QuestionBody.Text = question.Body;
        QuestionYes.Content = question.ConfirmLabel;
        QuestionOverlay.IsVisible = true;
        OverlayChanged();
        Dispatcher.UIThread.Post(() => QuestionNo.Focus(), DispatcherPriority.Input);
        return answer.Task;
    }

    private void Answer(bool yes)
    {
        TaskCompletionSource<bool>? answer = _question;
        _question = null;
        QuestionOverlay.IsVisible = false;
        OverlayChanged();
        answer?.TrySetResult(yes);
    }
}
