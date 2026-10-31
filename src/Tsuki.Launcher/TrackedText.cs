// Text with letter-spacing (package K17). WPF's TextBlock has none, and the small gold labels and the big title need it: this element draws the text one character at a time,
// each at the position it has in the unspaced string plus a fixed extra distance, so kerning pairs survive.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Tsuki.Launcher;

/// <summary>A single line of text with a tracking (extra space after every character, in device-independent pixels).</summary>
public sealed class TrackedText : FrameworkElement
{
    /// <summary>The text.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(TrackedText), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Extra space after each character.</summary>
    public static readonly DependencyProperty TrackingProperty = DependencyProperty.Register(
        nameof(Tracking), typeof(double), typeof(TrackedText), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The font family (inherited).</summary>
    public static readonly DependencyProperty FontFamilyProperty = TextBlock.FontFamilyProperty.AddOwner(typeof(TrackedText));

    /// <summary>The font size (inherited).</summary>
    public static readonly DependencyProperty FontSizeProperty = TextBlock.FontSizeProperty.AddOwner(typeof(TrackedText));

    /// <summary>The weight (inherited).</summary>
    public static readonly DependencyProperty FontWeightProperty = TextBlock.FontWeightProperty.AddOwner(typeof(TrackedText));

    /// <summary>The style (inherited).</summary>
    public static readonly DependencyProperty FontStyleProperty = TextBlock.FontStyleProperty.AddOwner(typeof(TrackedText));

    /// <summary>The brush (inherited).</summary>
    public static readonly DependencyProperty ForegroundProperty = TextBlock.ForegroundProperty.AddOwner(
        typeof(TrackedText), new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>The text.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Extra space after each character.</summary>
    public double Tracking
    {
        get => (double)GetValue(TrackingProperty);
        set => SetValue(TrackingProperty, value);
    }

    /// <summary>The font family.</summary>
    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    /// <summary>The font size.</summary>
    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <summary>The font weight.</summary>
    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    /// <summary>The font style.</summary>
    public FontStyle FontStyle
    {
        get => (FontStyle)GetValue(FontStyleProperty);
        set => SetValue(FontStyleProperty, value);
    }

    /// <summary>The brush.</summary>
    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        string text = Text ?? string.Empty;
        if (text.Length == 0)
        {
            return default;
        }

        FormattedText whole = Format(text);
        return new Size(Math.Ceiling(whole.WidthIncludingTrailingWhitespace + (Tracking * (text.Length - 1))), Math.Ceiling(whole.Height));
    }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext drawingContext)
    {
        string text = Text ?? string.Empty;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                continue;
            }

            // a surrogate pair is one character: draw it from its first half
            int length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            double x = i == 0 ? 0 : Format(text[..i]).WidthIncludingTrailingWhitespace + (Tracking * i);
            drawingContext.DrawText(Format(text.Substring(i, length)), new Point(x, 0));
            i += length - 1;
        }
    }

    private FormattedText Format(string text) => new(
        text,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyle, FontWeight, FontStretches.Normal),
        FontSize,
        Foreground,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
