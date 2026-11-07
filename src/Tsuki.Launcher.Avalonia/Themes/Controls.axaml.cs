using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Tsuki.Launcher.Avalonia.Themes;

public partial class Controls : Styles
{
    public Controls() => AvaloniaXamlLoader.Load(this);
}
