using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;

namespace Pfr.Core.Tests.Nios;

public class LicenseTests
{
    private static GridInventory Parse() => BackupParser.Parse(OneDbFixture.XmlStream());

    [Fact]
    public void Reads_node_and_grid_wide_licenses_grid_wide_first()
    {
        var inv = Parse();

        var first = inv.Licenses[0];
        Assert.True(first.IsGridWide);
        Assert.Equal("rpt_sub", first.Type);
        Assert.Equal("Reporting Subscription", first.Name);
        Assert.Null(first.MemberOid);

        // 5 node licenses + sw_tp on the GMC + 1 grid-wide.
        Assert.Equal(7, inv.Licenses.Count);
    }

    [Fact]
    public void Node_licenses_map_to_members_and_ha_nodes()
    {
        var inv = Parse();

        var haDtc = inv.Licenses.Where(l => l.MemberOid == 16).OrderBy(l => l.NodePosition).ToList();
        Assert.Equal([0, 1], haDtc.Select(l => l.NodePosition!.Value));
        Assert.Equal(["SN-A", "SN-B"], haDtc.Select(l => l.NodeSerial));

        var gmDtc = inv.Licenses.Single(l => l.MemberOid == 0 && l.Type == "dtc");
        Assert.Equal("Static", gmDtc.Kind);
        Assert.Equal(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), gmDtc.Expires);
        Assert.Null(inv.Licenses.Single(l => l.MemberOid == 0 && l.Type == "dns").Expires); // permanent

        Assert.Equal(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero),
            inv.Members.Single(m => m.Oid == 0).NextLicenseExpiry);
    }

    [Fact]
    public void Member_next_expiry_includes_its_own_licenses()
    {
        // gmc has no NIOS summary object, but holds an expired sw_tp license.
        var gmc = Parse().Members.Single(m => m.Oid == 7);
        Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), gmc.NextLicenseExpiry);
    }

    [Fact]
    public void License_keys_are_not_in_the_inventory()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Parse());
        Assert.DoesNotContain("LICENSEKEY", json);
    }

    [Theory]
    [InlineData(-1, "Expired")]
    [InlineData(0, "Expired")]
    [InlineData(1, "Expires in 1 day")]
    [InlineData(30, "Expires in 30 days")]
    [InlineData(90, "Expires in 90 days")]
    [InlineData(91, "")]
    public void Expiry_status(int daysFromNow, string expected)
    {
        var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, GridLicense.ExpiryStatus(now.AddDays(daysFromNow), now));
    }

    [Fact]
    public void Permanent_licenses_have_no_status() =>
        Assert.Equal("", GridLicense.ExpiryStatus(null, DateTimeOffset.UtcNow));

    [Theory]
    [InlineData("dtc", "DNS Traffic Control")]
    [InlineData("sw_tp", "Threat Protection (Software)")]
    [InlineData("some_new_thing", "SOME NEW THING")]
    public void Friendly_names(string code, string expected) =>
        Assert.Equal(expected, LicenseNames.Describe(code));
}
