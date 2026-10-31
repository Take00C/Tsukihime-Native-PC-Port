using System.Windows;
using System.Windows.Controls;

namespace Tsuki.Launcher.Views;

/// <summary>The heading block every page below the hero starts with: a gold overline, a serif title and a muted sentence.</summary>
public partial class PageHeader : UserControl
{
    /// <summary>The overline (page name, upper case).</summary>
    public static readonly DependencyProperty OverlineProperty = DependencyProperty.Register(
        nameof(Overline), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty, (d, e) => ((PageHeader)d).Over.Text = (string)e.NewValue));

    /// <summary>The title.</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty, (d, e) => ((PageHeader)d).Head.Text = (string)e.NewValue));

    /// <summary>The sentence under the title.</summary>
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty, (d, e) => ((PageHeader)d).Desc.Text = (string)e.NewValue));

    /// <summary>Creates the header.</summary>
    public PageHeader() => InitializeComponent();

    /// <summary>The overline.</summary>
    public string Overline
    {
        get => (string)GetValue(OverlineProperty);
        set => SetValue(OverlineProperty, value);
    }

    /// <summary>The title.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The description.</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
