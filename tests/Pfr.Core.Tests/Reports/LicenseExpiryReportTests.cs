using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;
using Pfr.Core.Reports;
using Pfr.Core.Tests.Nios;

namespace Pfr.Core.Tests.Reports;

public class LicenseExpiryReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static GridInventory Fixture() => BackupParser.Parse(OneDbFixture.XmlStream());

    [Fact]
    public void Lists_every_license_with_state_soonest_first()
    {
        var result = LicenseExpiryReport.Run(Fixture(), 90, Now);

        // Fixture: sw_tp on the GMC expired 2020-01-01; dtc on the GM runs to 2100; the rest are permanent.
        var first = result.Rows[0];
        Assert.Equal((ExpiryState.Expired, "gmc.lab.test", "sw_tp"), (first.State, first.Scope, first.Code));
        Assert.True(first.DaysLeft < 0);

        Assert.Equal(1, result.Expired);
        Assert.Equal(0, result.Expiring);
        Assert.Equal(1, result.Later);
        Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), result.NextExpiry);
        Assert.All(result.Rows.Where(r => r.State == ExpiryState.Permanent), r => Assert.Null(r.Expires));
        Assert.Equal(ExpiryState.Permanent, result.Rows[^1].State); // permanent last
    }

    [Theory]
    [InlineData(10, 30, ExpiryState.Expiring)]
    [InlineData(45, 30, ExpiryState.Later)]
    [InlineData(45, 60, ExpiryState.Expiring)]
    [InlineData(-1, 30, ExpiryState.Expired)]
    public void Window_decides_expiring_vs_later(int daysFromNow, int window, ExpiryState expected)
    {
        var inv = Fixture();
        inv.Licenses.Clear();
        inv.Licenses.Add(new GridLicense { Type = "dns", MemberOid = 0, Expires = Now.AddDays(daysFromNow) });
        foreach (var m in inv.Members) m.NextLicenseExpiry = null;

        Assert.Equal(expected, Assert.Single(LicenseExpiryReport.Run(inv, window, Now).Rows).State);
    }

    [Fact]
    public void Adds_the_nios_summary_expiry_when_member_licenses_dont_explain_it()
    {
        // Like the lab grid: node licenses permanent, but NIOS's summary says subscriptions end in 2028.
        var inv = Fixture();
        foreach (var l in inv.Licenses) l.Expires = null;
        foreach (var m in inv.Members) m.NextLicenseExpiry = null;
        var ns1 = inv.Members.Single(m => m.Oid == 16);
        ns1.NextLicenseExpiry = new DateTimeOffset(2028, 4, 8, 0, 0, 0, TimeSpan.Zero);

        var rows = LicenseExpiryReport.Run(inv, 90, Now).Rows;
        var summary = Assert.Single(rows, r => r.Code == "");
        Assert.Equal(("ns1.lab.test", ExpiryState.Later), (summary.Scope, summary.State));
        Assert.Contains("NIOS license summary", summary.License);
    }

    [Fact]
    public void No_summary_row_when_it_matches_a_member_license()
    {
        // The GM's summary (2100) equals its dtc license date: no duplicate row.
        var rows = LicenseExpiryReport.Run(Fixture(), 90, Now).Rows;
        Assert.DoesNotContain(rows, r => r.Scope == "gm.lab.test" && r.Code == "");
    }
}
