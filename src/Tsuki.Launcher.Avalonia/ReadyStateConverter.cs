using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher.Avalonia;

/// <summary>
/// The look of a ready-check row's badge, as the Windows launcher draws it: a green check, or a gold warning sign. The parameter picks what is wanted: "glyph" (the
/// path data), "fill" (the badge), "rim" (its border) or "ink" (the sign); without one, "✓" or "!".
/// </summary>
public sealed class ReadyStateConverter : IValueConverter
{
    public const string Check = "M6.5,12.5 L10.5,16.5 L17.5,8.5";
    public const string Warning = "M12,4.5 L20.5,19.5 L3.5,19.5 Z M12,10 L12,14 M12,16.8 L12,17.2";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool ok = value is CheckState.Ok;
        return parameter switch
        {
            "glyph" => Geometry.Parse(ok ? Check : Warning),
            "fill" => new SolidColorBrush(Color.Parse(ok ? "#2B7ED6A8" : "#2BE8C877")),
            "rim" => new SolidColorBrush(Color.Parse(ok ? "#667ED6A8" : "#66E8C877")),
            "ink" => new SolidColorBrush(Color.Parse(ok ? "#7ED6A8" : "#E8C877")),
            _ => ok ? "✓" : "!",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Path data (a string) to a geometry, for icons bound from the model.</summary>
public sealed class PathDataConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string data && data.Length > 0 ? Geometry.Parse(data) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
