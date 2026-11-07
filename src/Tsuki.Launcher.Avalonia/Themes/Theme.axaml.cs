using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Tsuki.Launcher.Avalonia.Themes;

public partial class Theme : ResourceDictionary
{
    public Theme() => AvaloniaXamlLoader.Load(this);
}
