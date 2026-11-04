using Pfr.Core;

namespace Pfr.Core.Tests;

public class AppInfoTests
{
    [Fact]
    public void Banner_includes_product_name_and_version()
    {
        Assert.StartsWith(AppInfo.ProductName, AppInfo.Banner);
        Assert.Matches(@"\d+\.\d+\.\d+", AppInfo.Version);
    }

    [Fact]
    public void Module_catalog_covers_every_module()
    {
        var catalogued = ModuleCatalog.All.Select(m => m.Module).ToHashSet();
        Assert.All(Enum.GetValues<AppModule>(), m => Assert.Contains(m, catalogued));
    }

    [Fact]
    public void Every_module_is_in_the_catalog_once_with_its_own_title_and_a_summary()
    {
        var all = ModuleCatalog.All;

        Assert.Equal(Enum.GetValues<AppModule>().Length, all.Count);
        Assert.Equal(all.Count, all.Select(m => m.Module).Distinct().Count());
        Assert.Equal(all.Count, all.Select(m => m.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(all, m => Assert.False(string.IsNullOrWhiteSpace(m.Summary)));
    }

    [Fact]
    public void The_menu_has_the_working_modules_first_and_then_the_placeholders_for_cloudflare_forward_networks_and_splunk()
    {
        var titles = ModuleCatalog.All.Select(m => m.Title).ToList();

        Assert.Equal(
            ["Clients", "Inventory", "IPAM", "Network tools", "Reports", "Cloudflare", "Forward Networks", "Splunk"],
            titles);
        Assert.Equal(
            [AppModule.Cloudflare, AppModule.ForwardNetworks, AppModule.Splunk],
            ModuleCatalog.All.Skip(5).Select(m => m.Module));
    }
}
