using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Tsuki.Launcher.Avalonia.Views;

public partial class PlayPage : UserControl
{
    /// <summary>The page width below which Quick settings goes under the ready check (the smallest window: a Steam Deck at 150 %), where its four-way choices have room.</summary>
    internal const double StackBelow = 600;

    public PlayPage()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Stack(e.NewSize.Width < StackBelow);
    }

    private void Stack(bool stacked)
    {
        if (stacked == (Grid.GetRow(QuickSettings) == 2)) { return; }
        Cards.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "1.05*,18,0.95*");
        Cards.RowDefinitions = stacked ? new RowDefinitions("Auto,18,Auto") : new RowDefinitions();
        Grid.SetColumn(QuickSettings, stacked ? 0 : 2);
        Grid.SetRow(QuickSettings, stacked ? 2 : 0);
    }
}
