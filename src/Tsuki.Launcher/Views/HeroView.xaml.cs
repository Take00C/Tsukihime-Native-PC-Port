using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tsuki.Launcher.Views;

/// <summary>The banner of the Play page: the game's title art with the port's title over it, or a drawn moon until a game folder is set.</summary>
public partial class HeroView : UserControl
{
    /// <summary>How much wider than the banner the picture is drawn.</summary>
    private const double ArtScale = 1.25;

    /// <summary>How far the picture is moved right, as a fraction of the banner width: the moon of the title picture sits at its horizontal middle and belongs on the right, away from the text.</summary>
    private const double ArtShift = 0.116;

    /// <summary>Creates the banner.</summary>
    public HeroView()
    {
        InitializeComponent();
        Frame.SizeChanged += (_, e) =>
        {
            Frame.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 10, 10);
            PlaceArt();
        };
        DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image)).AddValueChanged(Art, (_, _) => PlaceArt());
    }

    private void PlaceArt()
    {
        if (Art.Source is not BitmapSource bitmap || Frame.ActualWidth <= 0)
        {
            return;
        }

        double width = Frame.ActualWidth * ArtScale;
        Art.Width = width;
        Art.Height = width * bitmap.PixelHeight / bitmap.PixelWidth;
        Art.Margin = new Thickness(Frame.ActualWidth * ArtShift, 0, 0, 0);
    }
}
