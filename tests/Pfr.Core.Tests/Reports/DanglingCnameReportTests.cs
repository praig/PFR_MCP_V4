using Pfr.Core.Nios.Model;
using Pfr.Core.Reports;

namespace Pfr.Core.Tests.Reports;

public class DanglingCnameReportTests
{
    /// <summary>A small hand-built grid: one view with an authoritative zone, a delegation, a forward zone and an RPZ.</summary>
    private static GridInventory Grid(params (string Name, DnsRecordType Type, string Value, bool Disabled)[] records)
    {
        var inv = new GridInventory { GridName = "Test" };
        inv.DnsViews.Add(new DnsView { Key = ".v", Name = "Internal" });
        inv.Zones.Add(new DnsZone { Key = ".v.lab", ViewKey = ".v", Fqdn = "lab.test", ZoneType = "Authoritative" });
        inv.Zones.Add(new DnsZone { Key = ".v.off", ViewKey = ".v", Fqdn = "off.lab.test", ZoneType = "Authoritative", Disabled = true });
        inv.Zones.Add(new DnsZone { Key = ".v.aws", ViewKey = ".v", Fqdn = "aws.lab.test", ZoneType = "Delegated" });
        inv.Zones.Add(new DnsZone { Key = ".v.fwd", ViewKey = ".v", Fqdn = "partner.test", ZoneType = "Forward" });
        inv.Zones.Add(new DnsZone { Key = ".v.rpz", ViewKey = ".v", Fqdn = "rpz.local", ZoneType = "ResponsePolicy" });

        foreach (var (name, type, value, disabled) in records)
        {
            var zone = inv.Zones.Where(z => name == z.Fqdn || name.EndsWith("." + z.Fqdn))
                .OrderByDescending(z => z.Fqdn.Length).First();
            inv.Records.Add(new DnsRecord
            {
                Type = type, ZoneKey = zone.Key, ViewKey = ".v", Fqdn = name, Value = value, Disabled = disabled,
                Name = name == zone.Fqdn ? "" : name[..^(zone.Fqdn.Length + 1)],
            });
        }
        return inv;
    }

    private static DanglingCnameRow Row(DanglingCnameResult r, string name) => r.Rows.Single(x => x.Name == name);

    [Fact]
    public void Classifies_every_way_a_chain_can_end()
    {
        var result = DanglingCnameReport.Run(Grid(
            ("www.lab.test", DnsRecordType.A, "10.0.0.10", false),
            ("ok.lab.test", DnsRecordType.CNAME, "www.lab.test", false),
            ("two.lab.test", DnsRecordType.CNAME, "ok.lab.test.", false),            // chain of two, trailing dot
            ("gone.lab.test", DnsRecordType.CNAME, "missing.lab.test", false),
            ("txt.lab.test", DnsRecordType.TXT, "\"hello\"", false),
            ("notxt.lab.test", DnsRecordType.CNAME, "txt.lab.test", false),
            ("off1.lab.test", DnsRecordType.A, "10.0.0.11", true),
            ("toff.lab.test", DnsRecordType.CNAME, "off1.lab.test", false),
            ("inoff.lab.test", DnsRecordType.CNAME, "x.off.lab.test", false),
            ("a.lab.test", DnsRecordType.CNAME, "b.lab.test", false),
            ("b.lab.test", DnsRecordType.CNAME, "a.lab.test", false),
            ("ext.lab.test", DnsRecordType.CNAME, "www.example.com", false),
            ("del.lab.test", DnsRecordType.CNAME, "app.aws.lab.test", false),
            ("fwd.lab.test", DnsRecordType.CNAME, "host.partner.test", false),
            ("dis.lab.test", DnsRecordType.CNAME, "nowhere.lab.test", true)));

        Assert.Equal(CnameOutcome.Resolves, Row(result, "ok.lab.test").Outcome);
        Assert.Equal("two.lab.test → ok.lab.test → www.lab.test", Row(result, "two.lab.test").Chain);
        Assert.Equal(CnameOutcome.Resolves, Row(result, "two.lab.test").Outcome);

        Assert.Equal((CnameOutcome.TargetMissing, ReportSeverity.High), (Row(result, "gone.lab.test").Outcome, Row(result, "gone.lab.test").Severity));
        Assert.Equal((CnameOutcome.NoAddress, ReportSeverity.Medium), (Row(result, "notxt.lab.test").Outcome, Row(result, "notxt.lab.test").Severity));
        Assert.Contains("TXT", Row(result, "notxt.lab.test").Detail);
        Assert.Equal(CnameOutcome.TargetDisabled, Row(result, "toff.lab.test").Outcome);
        Assert.Equal(CnameOutcome.TargetDisabled, Row(result, "inoff.lab.test").Outcome); // disabled zone
        Assert.Equal(CnameOutcome.Loop, Row(result, "a.lab.test").Outcome);
        Assert.Equal(CnameOutcome.Loop, Row(result, "b.lab.test").Outcome);

        Assert.Equal((CnameOutcome.External, ReportSeverity.Info), (Row(result, "ext.lab.test").Outcome, Row(result, "ext.lab.test").Severity));
        Assert.Equal(CnameOutcome.Delegated, Row(result, "del.lab.test").Outcome);
        Assert.Equal(CnameOutcome.Delegated, Row(result, "fwd.lab.test").Outcome);
        Assert.Equal((CnameOutcome.CnameDisabled, ReportSeverity.Info), (Row(result, "dis.lab.test").Outcome, Row(result, "dis.lab.test").Severity));

        Assert.Equal(6, result.Dangling);   // gone, notxt, toff, inoff, a, b
        Assert.Equal(3, result.NotVerified);
        Assert.Equal(2, result.Resolving);

        // Worst first.
        Assert.Equal(ReportSeverity.High, result.Rows[0].Severity);
    }

    [Fact]
    public void Long_chains_are_flagged()
    {
        var records = new List<(string, DnsRecordType, string, bool)>();
        for (var i = 0; i < 10; i++) records.Add(($"c{i}.lab.test", DnsRecordType.CNAME, $"c{i + 1}.lab.test", false));
        records.Add(("c10.lab.test", DnsRecordType.A, "10.0.0.1", false));

        var result = DanglingCnameReport.Run(Grid([.. records]), maxChain: 8);
        Assert.Equal(CnameOutcome.ChainTooLong, Row(result, "c0.lab.test").Outcome);
        Assert.Equal(CnameOutcome.Resolves, Row(result, "c5.lab.test").Outcome); // 5 hops: fine
    }

    [Fact]
    public void Cloud_targets_are_flagged_as_takeover_risks()
    {
        var result = DanglingCnameReport.Run(Grid(
            ("shop.lab.test", DnsRecordType.CNAME, "acme-shop.azurewebsites.net", false),
            ("docs.lab.test", DnsRecordType.CNAME, "acme.github.io.", false),
            ("cdn.lab.test", DnsRecordType.CNAME, "d111.cloudfront.net", false),
            ("plain.lab.test", DnsRecordType.CNAME, "www.example.com", false)));

        Assert.Equal("Azure App Service", Row(result, "shop.lab.test").TakeoverProvider);
        Assert.Equal("GitHub Pages", Row(result, "docs.lab.test").TakeoverProvider);
        Assert.Equal("AWS CloudFront", Row(result, "cdn.lab.test").TakeoverProvider);
        Assert.Null(Row(result, "plain.lab.test").TakeoverProvider);

        Assert.Equal(ReportSeverity.Medium, Row(result, "shop.lab.test").Severity);
        Assert.Contains("subdomain takeover", Row(result, "shop.lab.test").Detail);
        Assert.Equal(3, result.TakeoverRisks);
    }

    [Fact]
    public void Rpz_rules_are_not_checked()
    {
        var result = DanglingCnameReport.Run(Grid(
            ("bad.example.rpz.local", DnsRecordType.CNAME, ".", false),
            ("ok.lab.test", DnsRecordType.CNAME, "missing.lab.test", false)));

        Assert.Single(result.Rows);
        Assert.Equal("ok.lab.test", result.Rows[0].Name);
    }

    [Fact]
    public void Views_are_checked_separately()
    {
        var inv = Grid(("www.lab.test", DnsRecordType.A, "10.0.0.10", false));
        // Same zone name in another view, where www doesn't exist.
        inv.DnsViews.Add(new DnsView { Key = ".x", Name = "External" });
        inv.Zones.Add(new DnsZone { Key = ".x.lab", ViewKey = ".x", Fqdn = "lab.test", ZoneType = "Authoritative" });
        inv.Records.Add(new DnsRecord { Type = DnsRecordType.CNAME, ZoneKey = ".x.lab", ViewKey = ".x", Name = "web", Fqdn = "web.lab.test", Value = "www.lab.test" });

        var row = Assert.Single(DanglingCnameReport.Run(inv).Rows);
        Assert.Equal(("External", CnameOutcome.TargetMissing), (row.View, row.Outcome));
    }

    [Fact]
    public void Host_aliases_resolve_through_the_host_records()
    {
        var result = DanglingCnameReport.Run(Pfr.Core.Nios.Backup.BackupParser.Parse(Pfr.Core.Tests.Nios.OneDbFixture.XmlStream()));
        var files = Row(result, "files.lab.test");
        Assert.True(files.FromHost);
        Assert.Equal(CnameOutcome.Resolves, files.Outcome);
        Assert.Equal(CnameOutcome.CnameDisabled, Row(result, "old.lab.test").Outcome);
    }
}
