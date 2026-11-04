using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Pfr.Core.Clients;
using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;
using Pfr.Core.Query;
using Pfr.Core.Reports;
using Pfr.Core.Storage;
using Pfr.Core.Tests.Clients;

namespace Pfr.Core.Tests.Nios;

public class RecordOriginParserTests
{
    private static string P(string name, string value) =>
        $"<PROPERTY NAME=\"{name}\" VALUE=\"{System.Security.SecurityElement.Escape(value)}\"/>";

    private static string O(string type, params (string Name, string Value)[] props) =>
        "<OBJECT>" + P("__type", ".com.infoblox." + type) + string.Concat(props.Select(p => P(p.Name, p.Value))) + "</OBJECT>";

    private const string Zone = "._default.test.lab";
    private const string ReverseZone = "._default.arpa.in-addr.10.0.0";

    private static string Address(string name, string address, string creator, string? lastQueried, string type = "dns.bind_a") =>
        lastQueried is null
            ? O(type, ("zone", Zone), ("name", name), ("address", address), ("creator", creator))
            : O(type, ("zone", Zone), ("name", name), ("address", address), ("creator", creator), ("last_queried", lastQueried));

    /// <summary>1700000000 is 2023-11-14, 1600000000 is 2020-09-13 and 1553078740 is 2019-03-20 (UTC).</summary>
    private static string Backup() => string.Join("\n",
    [
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
        "<DATABASE NAME=\"onedb\" VERSION=\"9.0.8-1\">",
        O("one.cluster", ("name", "Lab")),
        O("dns.zone", ("zone_type", "View"), ("zone", ".."), ("name", "_default"), ("fqdn", "_default")),
        O("dns.view", ("zone", "._default"), ("displayname", "default"), ("network_view", "0")),
        O("dns.zone", ("zone_type", "Authoritative"), ("zone", "._default"), ("name", "test.lab"), ("fqdn", "lab.test"),
            ("primary_type", "Grid"), ("is_reverse_zone", "0")),
        O("dns.zone", ("zone_type", "Authoritative"), ("zone", "._default"), ("name", "arpa.in-addr.10.0.0"), ("fqdn", "0.0.10.in-addr.arpa"),
            ("primary_type", "Grid"), ("is_reverse_zone", "1")),

        Address("dyn1", "10.0.0.50", "DYNAMIC", "1700000000"),
        Address("static1", "10.0.0.60", "STATIC", null),
        Address("v6", "2001:db8::1", "SYSTEM", "1553078740", type: "dns.bind_aaaa"),
        Address("zero", "10.0.0.70", "DYNAMIC", "0"),         // zero is "no time", not 1970
        Address("bad", "10.0.0.71", "DYNAMIC", "not-a-time"),
        O("dns.bind_cname", ("zone", Zone), ("name", "alias"), ("canonical_name", "dyn1.lab.test"), ("creator", "STATIC")),
        O("dns.bind_ptr", ("zone", ReverseZone), ("name", "50"), ("dname", "dyn1.lab.test"), ("creator", "DYNAMIC"), ("last_queried", "1700000000")),

        // A host: its A and PTR records are made by it, so they have its creator and last-queried time.
        O("dns.host", ("zone", Zone), ("name", "server1"), ("creator", "STATIC"), ("last_queried", "1600000000")),
        O("dns.host_address", ("host", Zone + ".server1"), ("address", "10.0.0.20"), ("is_ipv4", "true"), ("reverse_zone", ReverseZone)),

        "</DATABASE>",
    ]);

    private static GridInventory Parse() => BackupParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(Backup())));

    private static DnsRecord Find(GridInventory inventory, string fqdn, DnsRecordType type) =>
        inventory.Records.Single(r => r.Fqdn == fqdn && r.Type == type);

    [Fact]
    public void A_record_has_its_creator_and_the_time_it_was_last_queried()
    {
        var record = Find(Parse(), "dyn1.lab.test", DnsRecordType.A);

        Assert.Equal("DYNAMIC", record.Creator);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), record.LastQueried);
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), record.LastQueried!.Value.UtcDateTime);
    }

    [Fact]
    public void A_record_NIOS_has_no_time_for_has_none()
    {
        var inventory = Parse();

        Assert.Null(Find(inventory, "static1.lab.test", DnsRecordType.A).LastQueried);
        Assert.Equal("STATIC", Find(inventory, "static1.lab.test", DnsRecordType.A).Creator);
    }

    [Fact]
    public void A_time_of_zero_or_one_that_is_not_a_time_is_no_time_rather_than_1970_or_a_failure()
    {
        var inventory = Parse();

        Assert.Null(Find(inventory, "zero.lab.test", DnsRecordType.A).LastQueried);
        Assert.Null(Find(inventory, "bad.lab.test", DnsRecordType.A).LastQueried);
        Assert.Equal("DYNAMIC", Find(inventory, "bad.lab.test", DnsRecordType.A).Creator);
    }

    [Fact]
    public void The_creator_and_time_are_read_for_every_record_type_that_has_them()
    {
        var inventory = Parse();

        Assert.Equal(("SYSTEM", DateTimeOffset.FromUnixTimeSeconds(1553078740)),
            (Find(inventory, "v6.lab.test", DnsRecordType.AAAA).Creator, Find(inventory, "v6.lab.test", DnsRecordType.AAAA).LastQueried!.Value));
        Assert.Equal("STATIC", Find(inventory, "alias.lab.test", DnsRecordType.CNAME).Creator);
        Assert.Equal(("DYNAMIC", DateTimeOffset.FromUnixTimeSeconds(1700000000)),
            (Find(inventory, "50.0.0.10.in-addr.arpa", DnsRecordType.PTR).Creator, Find(inventory, "50.0.0.10.in-addr.arpa", DnsRecordType.PTR).LastQueried!.Value));
    }

    [Fact]
    public void The_records_a_host_object_makes_have_the_hosts_creator_and_time()
    {
        var inventory = Parse();

        var a = Find(inventory, "server1.lab.test", DnsRecordType.A);
        var ptr = Find(inventory, "20.0.0.10.in-addr.arpa", DnsRecordType.PTR);
        Assert.Equal(("STATIC", DateTimeOffset.FromUnixTimeSeconds(1600000000), "server1.lab.test"), (a.Creator, a.LastQueried!.Value, a.HostName));
        Assert.Equal(("STATIC", DateTimeOffset.FromUnixTimeSeconds(1600000000)), (ptr.Creator, ptr.LastQueried!.Value));
    }

    [Fact]
    public void A_backup_read_by_this_version_says_it_read_creators_and_a_new_inventory_says_it_did_not()
    {
        Assert.True(Parse().RecordOriginsRead);
        Assert.False(new GridInventory().RecordOriginsRead);
    }

    [Fact]
    public void The_shared_fixtures_records_still_parse_and_have_no_creator()
    {
        var inventory = BackupParser.Parse(OneDbFixture.XmlStream());

        Assert.NotEmpty(inventory.Records);
        Assert.All(inventory.Records, r => Assert.Null(r.LastQueried));
        Assert.True(inventory.RecordOriginsRead);
    }

    [Fact]
    public void Creators_and_times_survive_being_saved_as_a_snapshot()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"pfr-scavenge-{Guid.NewGuid():N}");
        try
        {
            var store = new SecureStore(folder);
            store.Initialize("correct horse battery", SecureStoreTests.TestIterations);
            var workspace = new ClientWorkspace(store);
            var client = workspace.CreateClient("Acme");

            var (_, info) = workspace.AddSnapshot(client.Id, Parse(), "lab.bak", 1, "sha");
            var back = workspace.LoadSnapshot(client.Id, workspace.GetClient(client.Id).Grids.Single().Id, info.Id);

            Assert.True(back.RecordOriginsRead);
            var record = Find(back, "dyn1.lab.test", DnsRecordType.A);
            Assert.Equal("DYNAMIC", record.Creator);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), record.LastQueried);
            Assert.Null(Find(back, "static1.lab.test", DnsRecordType.A).LastQueried);
            store.Dispose();
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void The_sql_records_table_has_the_creator_and_the_date()
    {
        using var database = SnapshotDatabase.Load(Parse());

        var rows = database.Execute("SELECT fqdn, creator, last_queried FROM records WHERE type = 'A' ORDER BY fqdn").Rows;

        Assert.Contains(rows, r => (string)r[0]! == "dyn1.lab.test" && (string?)r[1] == "DYNAMIC" && (string?)r[2] == "2023-11-14");
        Assert.Contains(rows, r => (string)r[0]! == "static1.lab.test" && (string?)r[1] == "STATIC" && r[2] is null);
        Assert.Equal(1L, (long)database.Execute("SELECT COUNT(*) FROM records WHERE creator = 'DYNAMIC' AND last_queried < '2024-01-01' AND type = 'PTR'").Rows[0][0]!);
    }
}

public class ScavengeReportTests
{
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static GridInventory Grid(bool originsRead = true)
    {
        var inventory = new GridInventory { GridName = "Lab", RecordOriginsRead = originsRead };

        void Add(DnsRecordType type, string fqdn, string value, string? creator, string? queried)
        {
            inventory.Records.Add(new DnsRecord
            {
                Type = type, Fqdn = fqdn, Name = fqdn, Value = value, ZoneKey = "z", ViewKey = "v", Creator = creator,
                LastQueried = queried is null ? null : DateTimeOffset.Parse(queried, null, System.Globalization.DateTimeStyles.AssumeUniversal),
            });
        }

        Add(DnsRecordType.A, "web1.lab.test", "10.0.0.20", "STATIC", "2026-09-30T12:00:00Z");
        Add(DnsRecordType.A, "laptop-7.lab.test", "10.0.0.150", "DYNAMIC", "2026-01-02T23:59:59Z");
        Add(DnsRecordType.A, "laptop-12.lab.test", "10.0.0.9", "DYNAMIC", null);
        Add(DnsRecordType.AAAA, "laptop-7.lab.test", "2001:db8::7", "DYNAMIC", "2026-01-02T23:59:59Z");
        Add(DnsRecordType.A, "ns1.lab.test", "10.0.0.2", "SYSTEM", "2026-10-01T00:00:00Z");
        Add(DnsRecordType.A, "old.lab.test", "10.0.0.3", null, null);              // creator not known
        Add(DnsRecordType.CNAME, "www.lab.test", "web1.lab.test", "STATIC", "2026-09-30T12:00:00Z");
        Add(DnsRecordType.TXT, "lab.test", "\"v=spf1 -all\"", "STATIC", null);
        Add(DnsRecordType.PTR, "20.0.0.10.in-addr.arpa", "web1.lab.test", "STATIC", "2026-09-30T12:00:00Z");
        Add(DnsRecordType.PTR, "0.0.10.in-addr.arpa", "ns1.lab.test", "SYSTEM", null);   // names no single address
        inventory.Records.Add(new DnsRecord   // an A record a host object makes: not a bind_a object
        {
            Type = DnsRecordType.A, Fqdn = "server1.lab.test", Name = "server1", Value = "10.0.0.25", ZoneKey = "z", ViewKey = "v",
            Creator = "STATIC", HostName = "server1.lab.test", LastQueried = DateTimeOffset.Parse("2026-09-30T12:00:00Z"),
        });
        return inventory;
    }

    [Fact]
    public void The_columns_are_the_four_asked_for_and_then_the_record_type()
    {
        Assert.Equal(["Creator", "FQDN", "IP address", "Last queried", "Record type"], ScavengeResult.Columns);
        Assert.Equal(ScavengeResult.Columns, ScavengeReport.Run(Grid()).ToResult().Columns);
    }

    [Fact]
    public void An_A_record_is_listed_with_creator_name_address_date_and_type()
    {
        var web1 = Assert.Single(ScavengeReport.Run(Grid()).Rows, r => r.Fqdn == "web1.lab.test");

        Assert.Equal(("STATIC", "10.0.0.20", "2026-09-30", "A"), (web1.Creator, web1.IpAddress, web1.LastQueriedText, web1.TypeText));
    }

    [Fact]
    public void Only_A_CNAME_and_PTR_records_entered_on_their_own_are_listed()
    {
        var rows = ScavengeReport.Run(Grid()).Rows;

        Assert.Equal(8, rows.Count);                                                     // 5 A, 1 CNAME and 2 PTR
        Assert.Single(rows, r => r.Fqdn == "laptop-7.lab.test");                         // its AAAA is not listed
        Assert.DoesNotContain(rows, r => r.Fqdn == "lab.test");                          // no TXT
        Assert.DoesNotContain(rows, r => r.Fqdn == "server1.lab.test");                  // not the A record a host object makes
    }

    [Fact]
    public void A_cname_is_listed_with_no_ip_address()
    {
        var www = Assert.Single(ScavengeReport.Run(Grid()).Rows, r => r.Type == DnsRecordType.CNAME);

        Assert.Equal(("STATIC", "www.lab.test", "", "2026-09-30"), (www.Creator, www.Fqdn, www.IpAddress, www.LastQueriedText));
    }

    [Fact]
    public void A_ptr_has_the_address_its_name_stands_for_and_one_that_names_none_is_listed_with_none()
    {
        var rows = ScavengeReport.Run(Grid()).Rows.Where(r => r.Type == DnsRecordType.PTR).ToList();

        var ptr = Assert.Single(rows, r => r.Fqdn == "20.0.0.10.in-addr.arpa");
        Assert.Equal(("10.0.0.20", "STATIC", "2026-09-30"), (ptr.IpAddress, ptr.Creator, ptr.LastQueriedText));
        var apex = Assert.Single(rows, r => r.Fqdn == "0.0.10.in-addr.arpa");     // a zone's own name: still a record, with no single address
        Assert.Equal(("", "SYSTEM", ""), (apex.IpAddress, apex.Creator, apex.LastQueriedText));
    }

    [Fact]
    public void Types_can_be_chosen()
    {
        var onlyA = ScavengeReport.Run(Grid(), new ScavengeReportOptions { Types = new HashSet<DnsRecordType> { DnsRecordType.A } });
        Assert.All(onlyA.Rows, r => Assert.Equal(DnsRecordType.A, r.Type));
        Assert.Equal(5, onlyA.Rows.Count);

        var others = ScavengeReport.Run(Grid(), new ScavengeReportOptions { Types = new HashSet<DnsRecordType> { DnsRecordType.CNAME, DnsRecordType.PTR } });
        Assert.Equal(3, others.Rows.Count);
        Assert.DoesNotContain(others.Rows, r => r.Type == DnsRecordType.A);

        // A type the report does not cover is ignored, not listed.
        var aaaa = ScavengeReport.Run(Grid(), new ScavengeReportOptions { Types = new HashSet<DnsRecordType> { DnsRecordType.AAAA } });
        Assert.Empty(aaaa.Rows);
    }

    [Fact]
    public void The_date_is_the_utc_day_whatever_the_offset_it_was_stored_with()
    {
        var inventory = new GridInventory { RecordOriginsRead = true };
        inventory.Records.Add(new DnsRecord
        {
            Type = DnsRecordType.A, Fqdn = "x.lab.test", Value = "10.0.0.1", Creator = "STATIC",
            LastQueried = new DateTimeOffset(2026, 3, 2, 1, 30, 0, TimeSpan.FromHours(5)), // 2026-03-01 20:30 UTC
        });

        Assert.Equal("2026-03-01", Assert.Single(ScavengeReport.Run(inventory).Rows).LastQueriedText);
    }

    [Fact]
    public void A_record_with_no_time_has_an_empty_date()
    {
        var row = Assert.Single(ScavengeReport.Run(Grid()).Rows, r => r.Fqdn == "laptop-12.lab.test");

        Assert.Null(row.LastQueried);
        Assert.Equal("", row.LastQueriedText);
    }

    [Fact]
    public void Creators_can_be_chosen()
    {
        var dynamicOnly = ScavengeReport.Run(Grid(), new ScavengeReportOptions { Creators = new HashSet<string> { "dynamic" } });  // any case

        Assert.All(dynamicOnly.Rows, r => Assert.Equal("DYNAMIC", r.Creator));
        Assert.Equal(2, dynamicOnly.Rows.Count);

        var two = ScavengeReport.Run(Grid(), new ScavengeReportOptions { Creators = new HashSet<string> { "STATIC", "SYSTEM" } });
        Assert.Equal(["STATIC", "SYSTEM"], two.Rows.Select(r => r.Creator).Distinct());
        Assert.DoesNotContain(two.Rows, r => r.Creator.Length == 0);   // a record whose creator is not known is left out once a choice is made
    }

    [Fact]
    public void Lines_come_dynamic_first_then_by_name_then_by_address_and_unknown_creators_last()
    {
        var rows = ScavengeReport.Run(Grid()).Rows;

        Assert.Equal(
            ["DYNAMIC|laptop-12.lab.test|10.0.0.9", "DYNAMIC|laptop-7.lab.test|10.0.0.150",
             "STATIC|20.0.0.10.in-addr.arpa|10.0.0.20", "STATIC|web1.lab.test|10.0.0.20", "STATIC|www.lab.test|",
             "SYSTEM|0.0.10.in-addr.arpa|", "SYSTEM|ns1.lab.test|10.0.0.2", "|old.lab.test|10.0.0.3"],
            rows.Select(r => $"{r.Creator}|{r.Fqdn}|{r.IpAddress}"));
    }

    [Fact]
    public void The_counts_describe_the_report()
    {
        var result = ScavengeReport.Run(Grid());

        Assert.Equal((2, 3, 2), (result.Count("DYNAMIC"), result.Count("STATIC"), result.Count("SYSTEM")));
        Assert.Equal((5, 1, 2), (result.Count(DnsRecordType.A), result.Count(DnsRecordType.CNAME), result.Count(DnsRecordType.PTR)));
        Assert.Equal(3, result.NeverQueried);   // laptop-12, old and the PTR for the zone's own name have no time
    }

    [Fact]
    public void A_snapshot_saved_before_creators_were_read_says_so()
    {
        Assert.False(ScavengeReport.Run(Grid(originsRead: false)).RecordOriginsRead);
        Assert.True(ScavengeReport.Run(Grid()).RecordOriginsRead);
    }

    [Fact]
    public void A_grid_with_no_records_gives_an_empty_report()
    {
        var result = ScavengeReport.Run(new GridInventory { RecordOriginsRead = true });

        Assert.Empty(result.Rows);
        Assert.Equal(0, result.NeverQueried);
    }

    [Fact]
    public void Written_as_excel_the_five_columns_and_every_cell_is_text()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"pfr-scavenge-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, "scavenge.xlsx");
        try
        {
            var result = ScavengeReport.Run(Grid());

            ResultExport.Write(result.ToResult(), path, spreadsheetSafe: false, title: ScavengeReport.Title);

            using var zip = ZipFile.OpenRead(path);
            using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var cells = XDocument.Load(stream).Descendants(Sheet + "c").ToDictionary(
                c => c.Attribute("r")!.Value,
                c => (Type: c.Attribute("t")?.Value, Value: c.Element(Sheet + "is")?.Element(Sheet + "t")?.Value ?? c.Element(Sheet + "v")?.Value ?? ""));

            Assert.Equal(ScavengeResult.Columns, "ABCDE".Select(column => cells[column + "1"].Value).ToArray());
            Assert.Equal(["DYNAMIC", "laptop-12.lab.test", "10.0.0.9", "A"], new[] { cells["A2"].Value, cells["B2"].Value, cells["C2"].Value, cells["E2"].Value });
            Assert.False(cells.ContainsKey("D2"));                       // no time: no cell
            Assert.Equal(("2026-01-02", "inlineStr"), (cells["D3"].Value, cells["D3"].Type));
            Assert.Equal(result.Rows.Count + 1, cells.Keys.Count(k => k[0] == 'B'));
            using var book = zip.GetEntry("xl/workbook.xml")!.Open();
            Assert.Equal("Scavenge report", XDocument.Load(book).Descendants(Sheet + "sheet").Single().Attribute("name")!.Value);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
