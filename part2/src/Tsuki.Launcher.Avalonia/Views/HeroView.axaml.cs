using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Tsuki.Launcher.Avalonia.Views;

/// <summary>The banner of the Play page: the game's title art with the port's title over it, or a drawn moon until a game folder is set.</summary>
public partial class HeroView : UserControl
{
    /// <summary>How much wider than the banner the picture is drawn.</summary>
    private const double ArtScale = 1.25;

    /// <summary>How far the picture is moved right, as a fraction of the banner width: the moon of the title picture sits at its horizontal middle and belongs on the right, away from the text.</summary>
    private const double ArtShift = 0.116;

    public HeroView()
    {
        InitializeComponent();
        Frame.SizeChanged += (_, _) => PlaceArt();
        Art.PropertyChanged += (_, e) =>
        {
            if (e.Property == Image.SourceProperty) { PlaceArt(); }
        };
    }

    /// <summary>The picture keeps its own shape: as wide as 1.25 banners, as high as that width asks, from the top left (the Windows launcher's PlaceArt).</summary>
    internal void PlaceArt()
    {
        double frame = Frame.Bounds.Width;
        if (Art.Source is not Bitmap bitmap || frame <= 0 || bitmap.PixelSize.Width <= 0) { return; }
        double width = frame * ArtScale;
        Art.Width = width;
        Art.Height = width * bitmap.PixelSize.Height / bitmap.PixelSize.Width;
        Art.Margin = new Thickness(frame * ArtShift, 0, 0, 0);
    }
}
