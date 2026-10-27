using Avalonia.Controls;
using Avalonia.Layout;
using Pfr.Core;

namespace Pfr.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        BannerText.Text = AppInfo.Banner;

        // Placeholder tab per planned module; each will be replaced by its real view.
        foreach (var (_, title, summary) in ModuleCatalog.All)
        {
            ModuleTabs.Items.Add(new TabItem
            {
                Header = title,
                Content = new TextBlock
                {
                    Text = summary + "\n\nNot built yet.",
                    Margin = new Avalonia.Thickness(16),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Top,
                },
            });
        }
    }
}
