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

public class RecordDetailsParserTests
{
    private static string P(string name, string value) =>
        $"<PROPERTY NAME=\"{name}\" VALUE=\"{System.Security.SecurityElement.Escape(value)}\"/>";

    private static string O(string type, params (string Name, string Value)[] props) =>
        "<OBJECT>" + P("__type", ".com.infoblox." + type) + string.Concat(props.Select(p => P(p.Name, p.Value))) + "</OBJECT>";

    private const string Zone = "._default.test.lab";
    private const string ReverseZone = "._default.arpa.in-addr.10.0.0";

    private static string Attribute(string target, string name, string value, string? inheritedFrom = null) =>
        inheritedFrom is null
            ? O("one.extensible_attributes_value", ("position", "0"), ("object", target), ("tag", "." + name), ("value", value))
            : O("one.extensible_attributes_value", ("position", "0"), ("object", target), ("tag", "." + name), ("value", value), ("inheritance_source", inheritedFrom));

    /// <summary>
    /// 1466090556 is 2016-06-16, 1537306666 is 2018-09-18, 1638384344 is 2021-12-01 and 1600000000 is 2020-09-13 (UTC).
    /// </summary>
    private static string Backup() => string.Join("\n",
    [
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
        "<DATABASE NAME=\"onedb\" VERSION=\"9.0.8-1\">",
        O("one.cluster", ("name", "Lab")),
        O("dns.zone", ("zone_type", "View"), ("zone", ".."), ("name", "_default"), ("fqdn", "_default")),
        O("dns.view", ("zone", "._default"), ("displayname", "default"), ("network_view", "0")),
        O("dns.zone", ("zone_type", "Authoritative"), ("zone", "._default"), ("name", "test.lab"), ("fqdn", "lab.test"), ("primary_type", "Grid")),
        O("dns.zone", ("zone_type", "Authoritative"), ("zone", "._default"), ("name", "arpa.in-addr.10.0.0"), ("fqdn", "0.0.10.in-addr.arpa"),
            ("primary_type", "Grid"), ("is_reverse_zone", "1")),
        O("dns.zone", ("zone_type", "NonDNSHost"), ("zone", ".."), ("name", "non_DNS_host_root"), ("fqdn", "non_DNS_host_root")),
        O("dns.view", ("zone", ".non_DNS_host_root"), ("displayname", " "), ("network_view", "0")),

        // A records: one with a creation time, a comment and two attributes (one empty); one with neither.
        O("dns.bind_a", ("zone", Zone), ("name", "apple"), ("address", "10.0.0.5"), ("creator", "SYSTEM"),
            ("creation_timestamp", "1466090556"), ("comment", "Auto-created by Add Zone")),
        O("dns.bind_a", ("zone", Zone), ("name", "win10-vm"), ("address", "10.9.16.18"), ("creator", "DYNAMIC"), ("creation_timestamp", "1537306666")),
        O("dns.bind_a", ("zone", Zone), ("name", "old"), ("address", "10.0.0.9"), ("creator", "STATIC")),
        Attribute(".com.infoblox.dns.bind_a$" + Zone + ",win10-vm,10.9.16.18", "Mnemonic", "WIN"),
        Attribute(".com.infoblox.dns.bind_a$" + Zone + ",win10-vm,10.9.16.18", "VM ID", "502c6633"),
        Attribute(".com.infoblox.dns.bind_a$" + Zone + ",win10-vm,10.9.16.18", "Site", ""),                  // no value: skipped
        Attribute(".com.infoblox.dns.bind_a$" + Zone + ",apple,10.0.0.99", "Mnemonic", "WRONG-ADDRESS"),     // names no A record

        // A DNS host with two addresses, a comment, a creation time and an attribute it inherits from a network.
        O("dns.host", ("zone", Zone), ("name", "server1"), ("creator", "STATIC"), ("creation_timestamp", "1638384344"), ("comment", "B1 Host")),
        O("dns.host_address", ("host", Zone + ".server1"), ("address", "10.0.0.20"), ("is_ipv4", "true"), ("reverse_zone", ReverseZone)),
        O("dns.host_address", ("host", Zone + ".server1"), ("address", "2001:db8::20"), ("is_ipv4", "false")),
        Attribute(".com.infoblox.dns.host$" + Zone + ".server1", "Mnemonic", "SRV", inheritedFrom: ".com.infoblox.dns.network$10.0.0.0/24/0"),

        // An IPAM-only host created with a timestamp: its addresses and attributes name it "zone.creator.timestamp.name".
        O("dns.host", ("zone", ".non_DNS_host_root"), ("name", "printer"), ("creator_member", "0"), ("created_timestamp", "1600000000")),
        O("dns.host_address", ("host", ".non_DNS_host_root.0.1600000000.printer"), ("address", "10.0.0.30"), ("is_ipv4", "true")),
        Attribute(".com.infoblox.dns.host$.non_DNS_host_root.0.1600000000.printer", "Mnemonic", "PRN"),

        // Attributes on other objects are not kept.
        Attribute(".com.infoblox.dns.network$10.0.0.0/24/0", "Mnemonic", "NET"),
        Attribute(".com.infoblox.dns.fixed_address$10.0.0.40.0..", "Mnemonic", "FIX"),

        "</DATABASE>",
    ]);

    private static GridInventory Parse() => BackupParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(Backup())));

    private static DnsRecord ARecord(GridInventory inventory, string fqdn) =>
        inventory.Records.Single(r => r.Fqdn == fqdn && r.Type == DnsRecordType.A && !r.IsFromHost);

    private static HostObject Host(GridInventory inventory, string name) => inventory.Hosts.Single(h => h.Name == name);

    [Fact]
    public void An_A_record_has_its_comment_and_the_time_it_was_created()
    {
        var apple = ARecord(Parse(), "apple.lab.test");

        Assert.Equal("Auto-created by Add Zone", apple.Comment);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1466090556), apple.Created);
        Assert.Equal(new DateTime(2016, 6, 16, 15, 22, 36, DateTimeKind.Utc), apple.Created!.Value.UtcDateTime);
    }

    [Fact]
    public void An_A_record_with_no_creation_time_or_comment_has_neither()
    {
        var old = ARecord(Parse(), "old.lab.test");

        Assert.Null(old.Created);
        Assert.Null(old.Comment);
        Assert.Null(old.ExtensibleAttributes);
    }

    [Fact]
    public void An_A_records_extensible_attributes_are_found_by_its_zone_name_and_address()
    {
        var vm = ARecord(Parse(), "win10-vm.lab.test");

        Assert.Equal("WIN", vm.ExtensibleAttributes!["Mnemonic"]);
        Assert.Equal("502c6633", vm.ExtensibleAttributes["VM ID"]);
        Assert.DoesNotContain("Site", vm.ExtensibleAttributes.Keys);   // it had no value
    }

    [Fact]
    public void An_attribute_naming_an_address_no_record_has_is_attached_to_nothing()
    {
        var apple = ARecord(Parse(), "apple.lab.test");

        Assert.Null(apple.ExtensibleAttributes);   // the "WRONG-ADDRESS" one names apple at a different address
    }

    [Fact]
    public void Attribute_names_are_looked_up_whatever_their_case()
    {
        var vm = ARecord(Parse(), "win10-vm.lab.test");

        Assert.Equal("WIN", vm.ExtensibleAttributes!["mnemonic"]);
    }

    [Fact]
    public void A_host_has_its_comment_creation_time_and_attributes_including_an_inherited_one()
    {
        var host = Host(Parse(), "server1.lab.test");

        Assert.Equal("B1 Host", host.Comment);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1638384344), host.Created);
        Assert.Equal("SRV", host.ExtensibleAttributes!["Mnemonic"]);   // the value that applies, though a network supplied it
        Assert.Equal(2, host.Addresses.Count);
    }

    [Fact]
    public void A_host_created_with_a_timestamp_is_found_by_the_name_that_includes_it()
    {
        var printer = Host(Parse(), "printer");

        Assert.False(printer.IsDns);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1600000000), printer.Created);   // from created_timestamp
        Assert.Equal("PRN", printer.ExtensibleAttributes!["Mnemonic"]);
    }

    [Fact]
    public void The_records_a_host_makes_have_the_hosts_comment_and_creation_time()
    {
        var made = Parse().Records.Single(r => r.Fqdn == "server1.lab.test" && r.Type == DnsRecordType.A);

        Assert.True(made.IsFromHost);
        Assert.Equal(("B1 Host", DateTimeOffset.FromUnixTimeSeconds(1638384344)), (made.Comment, made.Created!.Value));
    }

    [Fact]
    public void Attributes_set_on_networks_and_fixed_addresses_are_not_kept()
    {
        var inventory = Parse();

        var every = inventory.Records.Select(r => r.ExtensibleAttributes).Concat(inventory.Hosts.Select(h => h.ExtensibleAttributes))
            .Where(a => a is not null).SelectMany(a => a!.Values).ToList();

        Assert.DoesNotContain("NET", every);
        Assert.DoesNotContain("FIX", every);
        Assert.DoesNotContain("WRONG-ADDRESS", every);
        Assert.Equal(["PRN", "SRV", "WIN"], every.Where(v => v is "WIN" or "SRV" or "PRN").Order());
    }

    [Fact]
    public void A_backup_read_by_this_version_says_it_read_the_details_and_a_new_inventory_says_it_did_not()
    {
        Assert.True(Parse().RecordDetailsRead);
        Assert.False(new GridInventory().RecordDetailsRead);
    }

    [Fact]
    public void The_shared_fixtures_records_and_hosts_still_parse_with_no_attributes()
    {
        var inventory = BackupParser.Parse(OneDbFixture.XmlStream());

        Assert.NotEmpty(inventory.Records);
        Assert.All(inventory.Records, r => Assert.Null(r.ExtensibleAttributes));
        Assert.All(inventory.Hosts, h => Assert.Null(h.ExtensibleAttributes));
        Assert.True(inventory.RecordDetailsRead);
    }

    [Fact]
    public void The_details_survive_being_saved_as_a_snapshot()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"pfr-creation-{Guid.NewGuid():N}");
        try
        {
            var store = new SecureStore(folder);
            store.Initialize("correct horse battery", SecureStoreTests.TestIterations);
            var workspace = new ClientWorkspace(store);
            var client = workspace.CreateClient("Acme");

            var (_, info) = workspace.AddSnapshot(client.Id, Parse(), "lab.bak", 1, "sha");
            var back = workspace.LoadSnapshot(client.Id, workspace.GetClient(client.Id).Grids.Single().Id, info.Id);

            Assert.True(back.RecordDetailsRead);
            var vm = ARecord(back, "win10-vm.lab.test");
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1537306666), vm.Created);
            Assert.Equal("WIN", vm.ExtensibleAttributes!["Mnemonic"]);
            Assert.Equal("Auto-created by Add Zone", ARecord(back, "apple.lab.test").Comment);
            var host = Host(back, "server1.lab.test");
            Assert.Equal(("B1 Host", DateTimeOffset.FromUnixTimeSeconds(1638384344), "SRV"), (host.Comment, host.Created!.Value, host.ExtensibleAttributes!["Mnemonic"]));
            store.Dispose();
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void The_sql_tables_carry_the_comment_the_creation_date_and_the_attributes()
    {
        using var database = SnapshotDatabase.Load(Parse());

        var records = database.Execute("SELECT fqdn, comment, created FROM records WHERE type = 'A' AND is_from_host = 0 ORDER BY fqdn").Rows;
        Assert.Contains(records, r => (string)r[0]! == "apple.lab.test" && (string?)r[1] == "Auto-created by Add Zone" && (string?)r[2] == "2016-06-16");
        Assert.Contains(records, r => (string)r[0]! == "old.lab.test" && r[1] is null && r[2] is null);

        var hosts = database.Execute("SELECT DISTINCT host_name, created FROM hosts WHERE host_name = 'server1.lab.test'").Rows;
        Assert.Equal("2021-12-01", Assert.Single(hosts)[1]);

        var attributes = database.Execute("SELECT kind, fqdn, attribute, value FROM record_attributes WHERE attribute = 'Mnemonic' ORDER BY kind, fqdn").Rows
            .Select(r => $"{r[0]}|{r[1]}|{r[3]}").ToList();
        Assert.Equal(["A|win10-vm.lab.test|WIN", "Host|printer|PRN", "Host|server1.lab.test|SRV"], attributes);
    }
}

public class RecordCreationReportTests
{
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static DateTimeOffset Utc(string text) => DateTimeOffset.Parse(text, null, System.Globalization.DateTimeStyles.AssumeUniversal);

    private static GridInventory Grid(bool detailsRead = true)
    {
        var inventory = new GridInventory { GridName = "Lab", RecordDetailsRead = detailsRead };

        void AddA(string fqdn, string address, string? comment, string? created, params (string Name, string Value)[] attributes) =>
            inventory.Records.Add(new DnsRecord
            {
                Type = DnsRecordType.A, Fqdn = fqdn, Name = fqdn, Value = address, ZoneKey = "z", ViewKey = "v", Comment = comment,
                Created = created is null ? null : Utc(created),
                ExtensibleAttributes = attributes.Length == 0 ? null : attributes.ToDictionary(a => a.Name, a => a.Value),
            });

        void AddHost(string name, bool isDns, string? comment, string? created, string[] addresses, params (string Name, string Value)[] attributes)
        {
            var host = new HostObject
            {
                Name = name, IsDns = isDns, ZoneKey = "z", ViewKey = "v", Comment = comment, Created = created is null ? null : Utc(created),
                ExtensibleAttributes = attributes.Length == 0 ? null : attributes.ToDictionary(a => a.Name, a => a.Value),
            };
            foreach (var address in addresses) host.Addresses.Add(new HostAddress { Address = address, IsIpv6 = address.Contains(':') });
            inventory.Hosts.Add(host);
        }

        AddA("www.lab.test", "10.0.0.10", "front page", "2024-03-02T10:00:00Z", ("Mnemonic", "WEB"), ("Site", "HQ"));
        AddA("apple.lab.test", "10.0.0.5", null, null);
        AddA("db1.lab.test", "10.0.0.21", "", "2023-01-15T23:59:59Z", ("mnemonic", "DBA"));
        AddHost("server1.lab.test", true, "B1 Host", "2021-12-01T00:00:00Z", ["10.0.0.20", "2001:db8::20"], ("Mnemonic", "SRV"));
        AddHost("empty.lab.test", true, null, null, []);
        AddHost("printer", false, null, "2020-09-13T12:00:00Z", ["10.0.0.30"], ("Mnemonic", "PRN"));

        // An A record a host object makes: listed as the host, not again as a record.
        inventory.Records.Add(new DnsRecord
        {
            Type = DnsRecordType.A, Fqdn = "server1.lab.test", Value = "10.0.0.20", ZoneKey = "z", ViewKey = "v", HostName = "server1.lab.test",
        });
        // Not an A record.
        inventory.Records.Add(new DnsRecord { Type = DnsRecordType.CNAME, Fqdn = "alias.lab.test", Value = "www.lab.test", ZoneKey = "z", ViewKey = "v" });
        return inventory;
    }

    private static List<string> Lines(RecordCreationResult result) =>
        result.Rows.Select(r => $"{r.RecordType}|{r.Fqdn}|{r.IpAddress}|{r.Comment}|{r.CreatedText}|{r.AttributeValue}").ToList();

    [Fact]
    public void The_columns_are_the_six_asked_for_with_the_last_named_after_the_attribute()
    {
        var result = RecordCreationReport.Run(Grid());

        Assert.Equal(["Record type", "FQDN", "IP address", "Comment", "Creation date", "Mnemonic"], result.Columns);
        Assert.Equal(result.Columns, result.ToResult().Columns);
        Assert.Equal(["Record type", "FQDN", "IP address", "Comment", "Creation date", "Site"],
            RecordCreationReport.Run(Grid(), new RecordCreationOptions { Attribute = "Site" }).Columns);
    }

    [Fact]
    public void An_A_record_and_a_host_address_each_get_a_line_with_comment_date_and_attribute()
    {
        var lines = Lines(RecordCreationReport.Run(Grid()));

        Assert.Contains("A|www.lab.test|10.0.0.10|front page|2024-03-02|WEB", lines);
        Assert.Contains("Host|server1.lab.test|10.0.0.20|B1 Host|2021-12-01|SRV", lines);
    }

    [Fact]
    public void A_host_with_several_addresses_has_a_line_for_each_and_one_with_none_has_one_with_no_address()
    {
        var lines = Lines(RecordCreationReport.Run(Grid()));

        Assert.Contains("Host|server1.lab.test|2001:db8::20|B1 Host|2021-12-01|SRV", lines);
        Assert.Equal(2, lines.Count(l => l.StartsWith("Host|server1.lab.test|")));
        Assert.Contains("Host|empty.lab.test||||", lines);
    }

    [Fact]
    public void The_A_records_a_host_object_makes_are_not_listed_again_and_other_record_types_are_not_listed()
    {
        var rows = RecordCreationReport.Run(Grid()).Rows;

        Assert.DoesNotContain(rows, r => r.RecordType == "A" && r.Fqdn == "server1.lab.test");
        Assert.DoesNotContain(rows, r => r.Fqdn == "alias.lab.test");
    }

    [Fact]
    public void Hosts_that_are_not_in_dns_are_left_out_unless_asked_for()
    {
        Assert.DoesNotContain(RecordCreationReport.Run(Grid()).Rows, r => r.Fqdn == "printer");

        var with = RecordCreationReport.Run(Grid(), new RecordCreationOptions { IncludeIpamOnlyHosts = true });
        Assert.Contains("Host|printer|10.0.0.30||2020-09-13|PRN", Lines(with));
    }

    [Fact]
    public void A_records_or_hosts_can_be_left_out()
    {
        var onlyA = RecordCreationReport.Run(Grid(), new RecordCreationOptions { IncludeHosts = false });
        Assert.All(onlyA.Rows, r => Assert.Equal("A", r.RecordType));
        Assert.Equal(3, onlyA.Rows.Count);

        var onlyHosts = RecordCreationReport.Run(Grid(), new RecordCreationOptions { IncludeARecords = false });
        Assert.All(onlyHosts.Rows, r => Assert.Equal("Host", r.RecordType));
        Assert.Equal(3, onlyHosts.Rows.Count);   // server1 twice, and the one with no address
    }

    [Fact]
    public void The_attribute_can_be_named_in_any_case_and_a_blank_name_means_the_default()
    {
        Assert.Contains("A|db1.lab.test|10.0.0.21||2023-01-15|DBA", Lines(RecordCreationReport.Run(Grid())));   // stored as "mnemonic"
        Assert.Contains("A|www.lab.test|10.0.0.10|front page|2024-03-02|WEB", Lines(RecordCreationReport.Run(Grid(), new RecordCreationOptions { Attribute = "MNEMONIC" })));
        Assert.Equal("Mnemonic", RecordCreationReport.Run(Grid(), new RecordCreationOptions { Attribute = "  " }).Attribute);
        Assert.Contains("A|www.lab.test|10.0.0.10|front page|2024-03-02|HQ", Lines(RecordCreationReport.Run(Grid(), new RecordCreationOptions { Attribute = "site" })));
    }

    [Fact]
    public void The_dates_are_the_utc_day_and_an_object_with_no_time_has_none()
    {
        var rows = RecordCreationReport.Run(Grid()).Rows;

        Assert.Equal("2023-01-15", rows.Single(r => r.Fqdn == "db1.lab.test").CreatedText);   // 23:59:59 UTC stays that day
        Assert.Equal("", rows.Single(r => r.Fqdn == "apple.lab.test").CreatedText);
        Assert.Null(rows.Single(r => r.Fqdn == "apple.lab.test").Created);
    }

    [Fact]
    public void Lines_are_in_name_order_then_type_then_address()
    {
        var order = RecordCreationReport.Run(Grid()).Rows.Select(r => $"{r.Fqdn}|{r.RecordType}|{r.IpAddress}").ToList();

        Assert.Equal(
            ["apple.lab.test|A|10.0.0.5", "db1.lab.test|A|10.0.0.21", "empty.lab.test|Host|", "server1.lab.test|Host|10.0.0.20",
             "server1.lab.test|Host|2001:db8::20", "www.lab.test|A|10.0.0.10"],
            order);
    }

    [Fact]
    public void The_counts_describe_the_report()
    {
        var result = RecordCreationReport.Run(Grid());

        Assert.Equal((3, 3), (result.Count("A"), result.Count("Host")));
        Assert.Equal(4, result.WithAttribute);          // www, db1 and server1 twice
        Assert.Equal(2, result.WithoutCreationDate);    // apple and empty
        Assert.True(result.AttributeFound);
    }

    [Fact]
    public void When_the_attribute_is_set_on_nothing_the_report_says_so_and_names_what_is_set()
    {
        var result = RecordCreationReport.Run(Grid(), new RecordCreationOptions { Attribute = "Rack" });

        Assert.False(result.AttributeFound);
        Assert.Equal(0, result.WithAttribute);
        Assert.Equal(["Mnemonic", "Site"], result.AttributesSeen);   // set on the records and hosts listed, in order
        Assert.All(result.Rows, r => Assert.Equal("", r.AttributeValue));
    }

    [Fact]
    public void A_snapshot_saved_before_the_details_were_read_says_so()
    {
        Assert.False(RecordCreationReport.Run(Grid(detailsRead: false)).RecordDetailsRead);
        Assert.True(RecordCreationReport.Run(Grid()).RecordDetailsRead);
    }

    [Fact]
    public void A_grid_with_no_records_or_hosts_gives_an_empty_report()
    {
        var result = RecordCreationReport.Run(new GridInventory { RecordDetailsRead = true });

        Assert.Empty(result.Rows);
        Assert.Empty(result.AttributesSeen);
    }

    [Fact]
    public void Written_as_excel_the_six_columns_are_text_and_the_header_names_the_attribute()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"pfr-creation-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, "creation.xlsx");
        try
        {
            var result = RecordCreationReport.Run(Grid());

            ResultExport.Write(result.ToResult(), path, spreadsheetSafe: false, title: RecordCreationReport.Title);

            using var zip = ZipFile.OpenRead(path);
            using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var cells = XDocument.Load(stream).Descendants(Sheet + "c").ToDictionary(
                c => c.Attribute("r")!.Value,
                c => (Type: c.Attribute("t")?.Value, Value: c.Element(Sheet + "is")?.Element(Sheet + "t")?.Value ?? c.Element(Sheet + "v")?.Value ?? ""));

            Assert.Equal(result.Columns, "ABCDEF".Select(column => cells[column + "1"].Value).ToArray());
            Assert.Equal(["A", "apple.lab.test", "10.0.0.5"], new[] { cells["A2"].Value, cells["B2"].Value, cells["C2"].Value });
            Assert.False(cells.ContainsKey("D2"));                         // no comment: no cell
            Assert.False(cells.ContainsKey("E2"));                         // no creation date: no cell
            Assert.Equal(("2023-01-15", "inlineStr", "DBA"), (cells["E3"].Value, cells["E3"].Type, cells["F3"].Value));
            Assert.Equal(result.Rows.Count + 1, cells.Keys.Count(k => k[0] == 'B'));
            using var book = zip.GetEntry("xl/workbook.xml")!.Open();
            Assert.Equal("Record creation report", XDocument.Load(book).Descendants(Sheet + "sheet").Single().Attribute("name")!.Value);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
