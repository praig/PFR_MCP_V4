using System.Text.Json;
using Pfr.Core.Ipam;
using Pfr.Core.Nios.Model;

namespace Pfr.Core.Tests.Ipam;

public class IpamKeysTests
{
    [Theory]
    [InlineData("20.0.0.10.in-addr.arpa", "10.0.0.20")]
    [InlineData("20.0.0.10.IN-ADDR.ARPA.", "10.0.0.20")]
    [InlineData("1.1.168.192.in-addr.arpa", "192.168.1.1")]
    [InlineData("255.255.255.255.in-addr.arpa", "255.255.255.255")]
    public void A_reverse_name_gives_the_address_it_stands_for(string name, string address)
    {
        Assert.Equal(address, IpamKeys.AddressFromReverseName(name));
    }

    [Fact]
    public void An_ipv6_reverse_name_is_read_from_its_nibbles()
    {
        // 2001:db8::20 is 2001 0db8 0000 0000 0000 0000 0000 0020; the name lists the nibbles last first.
        var nibbles = "20010db8000000000000000000000020".Reverse().Select(c => c.ToString());
        var name = string.Join('.', nibbles) + ".ip6.arpa";

        Assert.Equal("2001:db8::20", IpamKeys.AddressFromReverseName(name));
    }

    [Theory]
    [InlineData("0.0.10.in-addr.arpa")]          // a zone apex, not an address
    [InlineData("5.0-25.0.0.10.in-addr.arpa")]   // a classless delegation
    [InlineData("300.0.0.10.in-addr.arpa")]      // not an octet
    [InlineData("x.0.0.10.in-addr.arpa")]
    [InlineData("www.lab.test")]
    [InlineData("1.0.0.0.ip6.arpa")]             // too few nibbles
    [InlineData("")]
    [InlineData(null)]
    public void A_name_that_is_not_one_plain_address_gives_none(string? name)
    {
        Assert.Null(IpamKeys.AddressFromReverseName(name));
    }

    [Theory]
    [InlineData("2001:0DB8:0000::0001", "2001:db8::1")]
    [InlineData("10.0.0.1", "10.0.0.1")]
    public void An_address_has_one_canonical_form(string address, string canonical)
    {
        Assert.Equal(canonical, IpamKeys.Canonical(address));
    }

    [Fact]
    public void Text_that_is_not_an_address_has_no_canonical_form()
    {
        Assert.Null(IpamKeys.Canonical("not-an-ip"));
        Assert.Null(IpamKeys.Canonical(""));
        Assert.Null(IpamKeys.Canonical(null));
    }
}

public class IpamHostTests
{
    private static IpamPlan Plan(params GridInventory[] grids) => IpamBuilder.Build(grids.Select(g => g.Input()));

    private static IpamView View(IpamPlan plan, string name = "default") => plan.Views.Single(v => v.Name == name);

    private static IpamNode Node(IpamPlan plan, string key, string view = "default") =>
        View(plan, view).AllNodes().Single(n => n.Key == key);

    private static string Join(IEnumerable<string> values) => string.Join(",", values);

    // ---- Hosts ----

    [Fact]
    public void A_host_is_listed_under_each_of_its_addresses()
    {
        var plan = Plan(IpamFixtures.Grid("Lab").WithDns().AddNet("10.0.0.0", 24)
            .AddHost("server1.lab.test", ["10.0.0.20", "10.9.9.9"]));

        var inside = Assert.Single(Node(plan, "10.0.0.0/24").Hosts);
        Assert.Equal("10.0.0.20", inside.Address);
        Assert.Equal("server1.lab.test", inside.Name);
        Assert.Equal("10.9.9.9", Join(inside.OtherAddresses));

        var outside = Assert.Single(View(plan).UnplacedHosts); // its other address is in no network
        Assert.Equal("10.9.9.9", outside.Address);
        Assert.Equal("10.0.0.20", Join(outside.OtherAddresses));
        Assert.Equal(2, plan.HostCount);
    }

    [Fact]
    public void An_IPAM_only_host_is_included_and_has_no_records()
    {
        var plan = Plan(IpamFixtures.Grid("Lab").AddNet("10.0.0.0", 24)
            .AddHost("printer", ["10.0.0.30"], dns: false, dhcp: true, mac: "00:11:22:33:44:55", comment: "Floor 2"));

        var host = Assert.Single(Node(plan, "10.0.0.0/24").Hosts);
        Assert.False(host.IsDns);
        Assert.True(host.ConfigureForDhcp);
        Assert.Equal("00:11:22:33:44:55", host.MacAddress);
        Assert.Equal("Floor 2", host.Comment);
        Assert.Null(host.TtlSeconds);
        Assert.Equal("", host.TtlSource);
        Assert.Empty(host.Records);
        Assert.Equal(0, plan.RecordCount);
    }

    [Fact]
    public void A_dns_host_has_its_ttl_aliases_and_the_view_of_its_dns_view()
    {
        var plan = Plan(IpamFixtures.Grid("Lab", (1, "Guest")).WithDns(networkView: 1).AddNet("10.0.0.0", 24, view: 1)
            .AddHost("server1.lab.test", ["10.0.0.20"], networkView: 1, ttl: 600, aliases: ["files.lab.test"]));

        var host = Assert.Single(Node(plan, "10.0.0.0/24", "Guest").Hosts);
        Assert.Equal("Guest", host.ViewName);
        Assert.Equal(600, host.TtlSeconds);
        Assert.Equal("Host", host.TtlSource);
        Assert.Equal("files.lab.test", Join(host.Aliases));
    }

    [Fact]
    public void A_host_without_a_ttl_of_its_own_says_it_comes_from_the_zone()
    {
        var plan = Plan(IpamFixtures.Grid("Lab").WithDns().AddNet("10.0.0.0", 24).AddHost("server1.lab.test", ["10.0.0.20"]));

        var host = Assert.Single(Node(plan, "10.0.0.0/24").Hosts);
        Assert.Equal(28800, host.TtlSeconds);
        Assert.Equal("Zone", host.TtlSource);
    }

    [Fact]
    public void The_same_host_on_two_grids_is_two_rows_each_with_its_own_source()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").WithDns().AddNet("10.0.0.0", 24).AddHost("server1.lab.test", ["10.0.0.20"]),
            IpamFixtures.Grid("B").WithDns().AddNet("10.0.0.0", 24).AddHost("server1.lab.test", ["10.0.0.20"]));

        var hosts = Node(plan, "10.0.0.0/24").Hosts;
        Assert.Equal("A,B", Join(hosts.Select(h => h.Source.Name)));
        Assert.Equal(2, plan.HostCount);
    }

    [Fact]
    public void A_host_sits_under_a_network_that_only_another_grid_has()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.0.0.0", 24),
            IpamFixtures.Grid("B").AddHost("printer", ["10.0.0.30"], dns: false));

        var node = Node(plan, "10.0.0.0/24");
        Assert.Equal("A", Join(node.PresentIn));
        Assert.Equal("B", Assert.Single(node.Hosts).Source.Name);
    }

    [Fact]
    public void An_ipv6_host_is_placed_in_an_ipv6_network()
    {
        var plan = Plan(IpamFixtures.Grid("Lab").WithDns().AddNet("10.0.0.0", 8, container: true).AddNet("2001:db8::", 64)
            .AddHost("server1.lab.test", ["2001:db8::20"]));

        var host = Assert.Single(Node(plan, "2001:db8::/64").Hosts);
        Assert.True(host.IsIpv6);
        Assert.Empty(Node(plan, "10.0.0.0/8").Hosts);
    }

    // ---- Records ----

    [Fact]
    public void The_records_a_host_generates_are_listed_under_it_and_not_again_on_their_own()
    {
        var plan = Plan(IpamFixtures.Grid("Lab").WithDns().AddNet("10.0.0.0", 24).AddHost("server1.lab.test", ["10.0.0.20"]));

        var node = Node(plan, "10.0.0.0/24");
        var host = Assert.Single(node.Hosts);
        Assert.Equal("A,PTR", Join(host.Records.Select(r => r.Type.ToString())));
        Assert.Empty(node.Records);            // not listed a second time
        Assert.Equal(2, node.RecordCount);     // but counted
        Assert.Equal(1, node.HostCount);
        Assert.Equal(2, plan.RecordCount);
        Assert.Equal(1, plan.HostCount);
    }

    [Fact]
    public void A_ptr_record_is_placed_by_the_address_in_its_reverse_name()
    {
        var plan = Plan(IpamFixtures.Grid("Lab").WithDns().AddNet("10.0.0.0", 24)
            .AddRecord(DnsRecordType.A, "www.lab.test", "10.0.0.10")
            .AddRecord(DnsRecordType.PTR, IpamFixtures.ReverseName("10.0.0.12"), "mail.lab.test"));

        var records = Node(plan, "10.0.0.0/24").Records;
        Assert.Equal("10.0.0.10,10.0.0.12", Join(records.Select(r => r.Address)));
        var ptr = records.Single(r => r.Type == DnsRecordType.PTR);
        Assert.Equal("12.0.0.10.in-addr.arpa", ptr.Name);
        Assert.Equal("mail.lab.test", ptr.Value);   // the target, not the address
        Assert.Equal("Internal", ptr.DnsViewName);
        Assert.Equal("0.0.10.in-addr.arpa", ptr.Zone);
    }

    [Fact]
    public void A_record_keeps_its_ttl_and_where_it_is_set()
    {
        var plan = Plan(IpamFixtures.Grid("Lab").WithDns().AddNet("10.0.0.0", 24)
            .AddRecord(DnsRecordType.A, "a.lab.test", "10.0.0.1", ttl: 300)
            .AddRecord(DnsRecordType.A, "b.lab.test", "10.0.0.2"));

        var records = Node(plan, "10.0.0.0/24").Records;
        Assert.Equal(300, records[0].TtlSeconds);
        Assert.Equal("Record", records[0].TtlSource);
        Assert.Equal(28800, records[1].TtlSeconds);
        Assert.Equal("Zone", records[1].TtlSource);
    }

    [Fact]
    public void A_record_is_in_the_network_view_of_its_dns_view()
    {
        var plan = Plan(IpamFixtures.Grid("Lab", (1, "Guest")).WithDns(networkView: 1)
            .AddNet("10.0.0.0", 24, view: 0).AddNet("10.0.0.0", 24, view: 1)
            .AddRecord(DnsRecordType.A, "www.lab.test", "10.0.0.10"));

        Assert.Empty(Node(plan, "10.0.0.0/24", "default").Records);
        Assert.Single(Node(plan, "10.0.0.0/24", "Guest").Records);
    }

    [Fact]
    public void Record_types_without_an_address_are_not_part_of_the_plan()
    {
        var plan = Plan(IpamFixtures.Grid("Lab").WithDns().AddNet("10.0.0.0", 24)
            .AddRecord(DnsRecordType.CNAME, "web.lab.test", "www.lab.test")
            .AddRecord(DnsRecordType.TXT, "lab.test", "\"v=spf1 -all\"")
            .AddRecord(DnsRecordType.A, "www.lab.test", "10.0.0.10"));

        Assert.Equal(1, plan.RecordCount);
    }

    [Fact]
    public void A_ptr_record_for_no_single_address_is_left_out_and_reported_once()
    {
        var plan = Plan(IpamFixtures.Grid("Lab").WithDns().AddNet("10.0.0.0", 24)
            .AddRecord(DnsRecordType.PTR, "5.0-25.0.0.10.in-addr.arpa", "host.lab.test")
            .AddRecord(DnsRecordType.PTR, "6.0-25.0.0.10.in-addr.arpa", "host2.lab.test"));

        Assert.Equal(0, plan.RecordCount);
        Assert.Single(plan.Problems, p => p.StartsWith("Lab: 2 PTR records") && p.Contains("left out"));
    }

    [Fact]
    public void An_a_record_on_two_grids_is_two_rows()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").WithDns().AddNet("10.0.0.0", 24).AddRecord(DnsRecordType.A, "www.lab.test", "10.0.0.10"),
            IpamFixtures.Grid("B").WithDns().AddNet("10.0.0.0", 24).AddRecord(DnsRecordType.A, "www.lab.test", "10.0.0.10"));

        Assert.Equal("A,B", Join(Node(plan, "10.0.0.0/24").Records.Select(r => r.Source.Name)));
        Assert.Equal(2, plan.RecordCount);
    }

    // ---- No network, and counts ----

    [Fact]
    public void Hosts_and_records_in_no_network_are_listed_under_no_network()
    {
        var plan = Plan(IpamFixtures.Grid("Lab").WithDns().AddNet("10.0.0.0", 24)
            .AddHost("far.lab.test", ["192.168.1.1"])
            .AddRecord(DnsRecordType.A, "other.lab.test", "172.16.0.5"));

        var view = View(plan);
        Assert.Equal("192.168.1.1", Assert.Single(view.UnplacedHosts).Address);
        Assert.Equal("172.16.0.5", Assert.Single(view.UnplacedRecords).Address);
        Assert.Equal(1, view.HostCount);
        Assert.Equal(2, view.RecordCount); // the host's A record, and the standalone one
    }

    [Fact]
    public void Counts_roll_up_to_every_level_above_for_hosts_and_records()
    {
        var plan = Plan(IpamFixtures.Grid("Lab").WithDns()
            .AddNet("10.0.0.0", 8, container: true).AddNet("10.0.0.0", 24)
            .AddHost("a.lab.test", ["10.0.0.10"])
            .AddHost("b.lab.test", ["10.0.0.11"], dns: false)
            .AddRecord(DnsRecordType.A, "www.lab.test", "10.0.0.12")
            .AddRecord(DnsRecordType.A, "wide.lab.test", "10.200.0.1")); // in the container, in no network

        Assert.Equal(2, Node(plan, "10.0.0.0/24").HostCount);
        Assert.Equal(3, Node(plan, "10.0.0.0/24").RecordCount); // a's A and PTR, www
        Assert.Equal(2, Node(plan, "10.0.0.0/8").HostCount);
        Assert.Equal(4, Node(plan, "10.0.0.0/8").RecordCount);
        Assert.Equal(2, View(plan).HostCount);
        Assert.Equal(4, View(plan).RecordCount);
        Assert.Equal(4, plan.RecordCount);
    }

    [Fact]
    public void A_view_with_only_hosts_still_exists()
    {
        var plan = Plan(IpamFixtures.Grid("Lab", (2, "Lab view")).AddHost("printer", ["10.9.0.1"], dns: false, networkView: 2));

        Assert.Single(View(plan, "Lab view").UnplacedHosts);
    }

    // ---- The filter ----

    [Fact]
    public void The_filter_lists_only_the_kinds_asked_for()
    {
        var plan = Plan(IpamFixtures.Grid("Lab").WithDns().AddNet("10.0.0.0", 24)
            .AddHost("a.lab.test", ["10.0.0.10"]).AddRecord(DnsRecordType.A, "www.lab.test", "10.0.0.12")
            .AddRange("10.0.0.100", "10.0.0.110"));
        var node = Node(plan, "10.0.0.0/24");

        var hostsOnly = new IpamFilter(kinds: IpamLeafKinds.Hosts);
        Assert.Single(hostsOnly.HostsShown(node));
        Assert.Empty(hostsOnly.RangesShown(node));
        Assert.Empty(hostsOnly.RecordsShown(node));
        Assert.Empty(hostsOnly.HostRecordsShown(node.Hosts[0])); // records off: not under the host either

        var everything = new IpamFilter();
        Assert.Single(everything.RangesShown(node));
        Assert.Equal(2, everything.HostRecordsShown(node.Hosts[0]).Count());
        Assert.Single(everything.RecordsShown(node));
    }

    [Fact]
    public void A_grid_filter_keeps_a_network_that_holds_one_of_its_hosts()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.0.0.0", 24),
            IpamFixtures.Grid("B").AddHost("printer", ["10.0.0.30"], dns: false));
        var node = Node(plan, "10.0.0.0/24");

        Assert.True(new IpamFilter("B").Shows(node));   // A's network, kept because B has a host in it
        Assert.False(new IpamFilter("B", IpamLeafKinds.Ranges).Shows(node)); // with hosts not shown, there is no reason to keep it
    }

    [Fact]
    public void Asking_for_shared_networks_hides_what_is_in_no_network()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.0.0.0", 24).AddHost("far", ["192.168.1.1"], dns: false),
            IpamFixtures.Grid("B").AddNet("10.0.0.0", 24));

        var filter = new IpamFilter(sharedOnly: true);

        Assert.Empty(filter.UnplacedHosts(View(plan)));
        Assert.Single(new IpamFilter().UnplacedHosts(View(plan)));
    }
}

public class IpamHostOutputTests
{
    private static IpamPlan SamplePlan() => IpamBuilder.Build(
    [
        IpamFixtures.Grid("Lab").WithDns().AddNet("10.0.0.0", 24, comment: "Servers")
            .AddHost("server1.lab.test", ["10.0.0.20"], ttl: 600, aliases: ["files.lab.test"], dhcp: true, mac: "00:11:22:33:44:55")
            .AddHost("printer", ["10.0.0.30"], dns: false)
            .AddRecord(DnsRecordType.A, "www.lab.test", "10.0.0.10")
            .Input(),
    ]);

    [Fact]
    public void The_text_lists_hosts_by_default_with_what_they_are()
    {
        var text = IpamTextReport.Render(SamplePlan(), "Acme");

        Assert.Contains("1 network, 0 containers, 0 DHCP ranges, 2 hosts, 3 DNS records in 1 network view", text);
        Assert.Contains("host 10.0.0.20  server1.lab.test  Lab  DNS  DHCP  mac 00:11:22:33:44:55  aliases files.lab.test  TTL 600 (Host)", text);
        Assert.Contains("host 10.0.0.30  printer  Lab  IPAM only", text);

        // Records are not listed unless asked for, and the network row doesn't count them either.
        Assert.DoesNotContain("record A", text);
        Assert.DoesNotContain("PTR", text);
        var network = text.Split('\n').Single(l => l.Contains("10.0.0.0/24"));
        Assert.Contains("2 hosts", network);
        Assert.DoesNotContain("record", network);
    }

    [Fact]
    public void With_records_the_ones_a_host_generates_are_under_it_and_the_rest_are_listed_on_their_own()
    {
        var text = IpamTextReport.Render(SamplePlan(), "Acme", show: IpamLeafKinds.All);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        var host = lines.FindIndex(l => l.Contains("host 10.0.0.20"));
        Assert.Contains("A  server1.lab.test  10.0.0.20  TTL 600 (Host)", lines[host + 1]);
        Assert.Contains("PTR  20.0.0.10.in-addr.arpa  server1.lab.test  TTL 600 (Host)", lines[host + 2]);
        Assert.True(lines[host + 1].Length - lines[host + 1].TrimStart().Length > lines[host].Length - lines[host].TrimStart().Length,
            "the generated records are indented under their host");

        Assert.Contains("record A 10.0.0.10  www.lab.test  Lab  TTL 28800 (Zone)", text);
        Assert.Contains("3 records", lines.Single(l => l.Contains("10.0.0.0/24")));
    }

    [Fact]
    public void Hosts_can_be_left_out()
    {
        var text = IpamTextReport.Render(SamplePlan(), "Acme", show: IpamLeafKinds.None);

        Assert.DoesNotContain("host 10.0.0", text);
    }

    [Fact]
    public void Hosts_and_records_in_no_network_are_listed_under_no_network()
    {
        var plan = IpamBuilder.Build(
        [
            IpamFixtures.Grid("Lab").WithDns().AddNet("10.0.0.0", 24)
                .AddHost("far.lab.test", ["192.168.1.1"]).AddRecord(DnsRecordType.A, "x.lab.test", "172.16.0.5").Input(),
        ]);

        var text = IpamTextReport.Render(plan, "Acme", show: IpamLeafKinds.All);

        Assert.Contains("(no network): 1 host, 1 DNS record outside every network of this view", text);
        Assert.Contains("host 192.168.1.1  far.lab.test  Lab  DNS", text);
        Assert.Contains("record A 172.16.0.5  x.lab.test", text);
    }

    [Fact]
    public void The_json_has_hosts_with_their_records_and_the_counts()
    {
        using var document = JsonDocument.Parse(IpamJson.ToJson(SamplePlan(), "Acme", show: IpamLeafKinds.All));
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("totals").GetProperty("hosts").GetInt32());
        Assert.Equal(3, root.GetProperty("totals").GetProperty("dnsRecords").GetInt32());

        var network = root.GetProperty("views")[0].GetProperty("networks")[0];
        Assert.Equal(2, network.GetProperty("hostCount").GetInt32());
        Assert.Equal(3, network.GetProperty("recordCount").GetInt32());

        var server = network.GetProperty("hosts")[0];
        Assert.Equal("10.0.0.20", server.GetProperty("address").GetString());
        Assert.Equal("server1.lab.test", server.GetProperty("name").GetString());
        Assert.True(server.GetProperty("dns").GetBoolean());
        Assert.True(server.GetProperty("dhcp").GetBoolean());
        Assert.Equal(600, server.GetProperty("ttl").GetInt32());
        Assert.Equal("Host", server.GetProperty("ttlSource").GetString());
        Assert.Equal("files.lab.test", server.GetProperty("aliases")[0].GetString());
        Assert.Equal("A,PTR", string.Join(",", server.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("type").GetString())));

        var printer = network.GetProperty("hosts")[1];
        Assert.False(printer.GetProperty("dns").GetBoolean());
        Assert.Equal(JsonValueKind.Null, printer.GetProperty("ttl").ValueKind);

        var record = network.GetProperty("records")[0];
        Assert.Equal("www.lab.test", record.GetProperty("name").GetString());
        Assert.Equal("10.0.0.10", record.GetProperty("address").GetString());
    }

    [Fact]
    public void The_json_leaves_out_the_kinds_not_asked_for()
    {
        using var document = JsonDocument.Parse(IpamJson.ToJson(SamplePlan(), "Acme")); // ranges and hosts, no records
        var network = document.RootElement.GetProperty("views")[0].GetProperty("networks")[0];

        Assert.Equal(JsonValueKind.Array, network.GetProperty("hosts").ValueKind);
        Assert.Equal(JsonValueKind.Null, network.GetProperty("records").ValueKind);
        Assert.Equal(JsonValueKind.Null, network.GetProperty("hosts")[0].GetProperty("records").ValueKind);
    }
}
