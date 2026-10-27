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
}
