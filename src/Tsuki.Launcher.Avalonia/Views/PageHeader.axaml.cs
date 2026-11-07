using Avalonia;
using Avalonia.Controls;

namespace Tsuki.Launcher.Avalonia.Views;

/// <summary>The heading block of a page: overline, title, description (the Windows launcher's PageHeader).</summary>
public partial class PageHeader : UserControl
{
    public static readonly StyledProperty<string> OverlineProperty = AvaloniaProperty.Register<PageHeader, string>(nameof(Overline), string.Empty);
    public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<PageHeader, string>(nameof(Title), string.Empty);
    public static readonly StyledProperty<string> DescriptionProperty = AvaloniaProperty.Register<PageHeader, string>(nameof(Description), string.Empty);

    public PageHeader() => InitializeComponent();

    public string Overline { get => GetValue(OverlineProperty); set => SetValue(OverlineProperty, value); }
    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OverlineProperty) { Over.Text = Overline; }
        else if (change.Property == TitleProperty) { Head.Text = Title; }
        else if (change.Property == DescriptionProperty) { Desc.Text = Description; }
    }
}
