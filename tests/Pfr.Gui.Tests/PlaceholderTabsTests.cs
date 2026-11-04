using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Pfr.Core;
using Pfr.Gui.Views;

namespace Pfr.Gui.Tests;

public class PlaceholderTabsTests
{
    private static List<TabItem> Tabs(MainWindow window) =>
        window.FindControl<TabControl>("ModuleTabs")!.Items.OfType<TabItem>().ToList();

    [AvaloniaFact]
    public void The_menu_shows_a_tab_for_every_module_in_the_catalog_in_order()
    {
        var window = TestWindows.New();
        window.Show();

        Assert.Equal(ModuleCatalog.All.Select(m => m.Title), Tabs(window).Select(t => (string)t.Header!));
        Assert.Equal(
            ["Clients", "Inventory", "IPAM", "Network tools", "Reports", "Cloudflare", "Forward Networks", "Splunk"],
            Tabs(window).Select(t => (string)t.Header!));
    }

    [AvaloniaTheory]
    [InlineData(AppModule.Cloudflare, "Cloudflare")]
    [InlineData(AppModule.ForwardNetworks, "Forward Networks")]
    [InlineData(AppModule.Splunk, "Splunk")]
    public void A_placeholder_tab_says_it_is_reserved_and_coming_in_a_later_version(AppModule module, string title)
    {
        var window = TestWindows.New();
        window.Show();

        window.ShowModule(module);

        var tabs = window.FindControl<TabControl>("ModuleTabs")!;
        var tab = Assert.IsType<TabItem>(tabs.SelectedItem);
        Assert.Equal(title, tab.Header);
        var text = Assert.IsType<TextBlock>(tab.Content);   // just a message: no page, no controls, nothing to run
        Assert.Contains(title, text.Text);
        Assert.Contains("Reserved for a future", text.Text);
        Assert.EndsWith("Coming in a later version.", text.Text);
    }

    [AvaloniaFact]
    public void The_working_tabs_are_still_their_own_pages_and_not_placeholders()
    {
        var window = TestWindows.New();
        window.Show();
        var content = Tabs(window).ToDictionary(t => (string)t.Header!, t => t.Content);

        Assert.IsType<InventoryView>(content["Inventory"]);
        Assert.IsType<IpamView>(content["IPAM"]);
        Assert.IsType<ReportsView>(content["Reports"]);
        Assert.IsType<NetworkToolsView>(content["Network tools"]);
        Assert.IsType<ClientsView>(content["Clients"]);
    }

    [AvaloniaFact]
    public void Visiting_a_placeholder_and_coming_back_leaves_the_app_as_it_was()
    {
        var window = TestWindows.New();
        window.Show();
        var status = window.StatusMessage;

        window.ShowModule(AppModule.Splunk);
        window.ShowModule(AppModule.ForwardNetworks);
        window.ShowModule(AppModule.Reports);

        Assert.Equal("Reports", (string)((TabItem)window.FindControl<TabControl>("ModuleTabs")!.SelectedItem!).Header!);
        Assert.Equal(status, window.StatusMessage);
        Assert.False(window.IsBusy);
    }
}
