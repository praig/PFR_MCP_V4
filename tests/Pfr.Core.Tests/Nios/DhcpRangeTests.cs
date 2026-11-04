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

public class DhcpRangeParserTests
{
    private static string P(string name, string value) =>
        $"<PROPERTY NAME=\"{name}\" VALUE=\"{System.Security.SecurityElement.Escape(value)}\"/>";

    private static string O(string type, params (string Name, string Value)[] props) =>
        "<OBJECT>" + P("__type", ".com.infoblox." + type) + string.Concat(props.Select(p => P(p.Name, p.Value))) + "</OBJECT>";

    private static string Network(string address, int cidr, int view, string parent, bool overrides = false, int lease = 43200) =>
        O("dns.network", ("address", address), ("cidr", cidr.ToString()), ("network_view", view.ToString()), ("is_ipv4", "true"),
            ("parent", parent), ("override_lease_time", overrides ? "true" : "false"), ("lease_time", lease.ToString()));

    private static string Range(string network, string start, string end, int view = 0, bool overrides = false, int lease = 43200, string assoc = "None") =>
        O("dns.dhcp_range", ("network", network), ("start_address", start), ("end_address", end), ("network_view", view.ToString()),
            ("server_association_type", assoc), ("member", "0"), ("ms_server", "."),
            ("override_lease_time", overrides ? "true" : "false"), ("lease_time", lease.ToString()));

    private static string Option(string parent, string definition, string value, bool ipv4 = true) =>
        O("dns.option", ("is_ipv4", ipv4 ? "true" : "false"), ("parent", parent), ("option_definition", definition), ("value", value), ("ms_user_class", "."));

    private static string RangeRef(string key) => ".com.infoblox.dns.dhcp_range$" + key;

    private static string Definition(string space, int code, string name) =>
        O("dns.option_definition", ("space", space), ("code", code.ToString()), ("name", name), ("type", "string"), ("is_ipv4", "true"));

    /// <summary>
    /// A small grid with one of each thing that matters: a lease set on a container, on a network, on the grid and on a range;
    /// the same range address in two network views; a prefix range; and options that belong elsewhere or can't be placed.
    /// </summary>
    private static string Backup() => string.Join("\n",
    [
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
        "<DATABASE NAME=\"onedb\" VERSION=\"9.0.8-1\">",
        O("one.cluster", ("name", "Lab")),
        O("dns.cluster_dhcp_properties", ("lease_time", "86400")),

        // 10.0.0.0/8 sets 7200; 10.1.0.0/24 inherits it. 10.2.0.0/24 sets 600. 10.3.0.0/24 inherits the grid's.
        O("dns.network_container", ("address", "10.0.0.0"), ("cidr", "8"), ("network_view", "0"), ("is_ipv4", "true"),
            ("parent", ".com.infoblox.dns.network_view$0"), ("override_lease_time", "true"), ("lease_time", "7200")),
        Network("10.1.0.0", 24, 0, ".com.infoblox.dns.network_container$10.0.0.0/8/0"),
        Network("10.2.0.0", 24, 0, ".com.infoblox.dns.network_view$0", overrides: true, lease: 600),
        Network("10.3.0.0", 24, 0, ".com.infoblox.dns.network_view$0"),
        Network("10.4.0.0", 24, 0, ".com.infoblox.dns.network_view$0"),
        Network("10.4.0.0", 24, 5, ".com.infoblox.dns.network_view$5"),

        // Every one of these stores 43200, which is right only for the last two.
        Range("10.1.0.0/24/0", "10.1.0.10", "10.1.0.99"),
        Range("10.2.0.0/24/0", "10.2.0.10", "10.2.0.99"),
        Range("10.3.0.0/24/0", "10.3.0.10", "10.3.0.99"),
        Range("10.3.0.0/24/0", "10.3.0.100", "10.3.0.199", overrides: true, lease: 120),
        Range("10.4.0.0/24/0", "10.4.0.1", "10.4.0.50", view: 0),
        Range("10.4.0.0/24/5", "10.4.0.1", "10.4.0.50", view: 5),
        O("dns.dhcp_range", ("network", "2001:db8:0:1::/64/0"), ("address_type", "PREFIX"), ("start_address", ""), ("end_address", ""),
            ("v6_start_prefix", "2001:db8:0:1000::"), ("v6_end_prefix", "2001:db8:0:1fff::"), ("v6_prefix_bits", "64"),
            ("network_view", "0"), ("server_association_type", "None"), ("member", "0"), ("ms_server", "."),
            ("override_lease_time", "false"), ("lease_time", "43200")),

        Definition("DHCP..false", 3, "routers"),
        Definition("DHCP..false", 6, "domain-name-servers"),
        Definition("DHCP..false", 66, "tftp-server-name"),
        Definition("DHCPv6..false", 23, "dns-servers"),
        Definition("DHCP..false", 4, "time-servers"),
        Definition("DHCP..false", 15, "domain-name"),
        Definition("DHCP..false", 42, "ntp-servers"),

        // On ranges. The order here is not the order they are listed in.
        Option(RangeRef("10.1.0.10/10.1.0.99///0/"), "DHCP..false.6", "10.0.0.53,10.0.0.54"),
        Option(RangeRef("10.1.0.10/10.1.0.99///0/"), "DHCP..false.3", "10.1.0.1"),
        Option(RangeRef("10.2.0.10/10.2.0.99///0/"), "DHCP Standard Options.12.false.66", "tftp.lab.test"),
        Option(RangeRef("10.4.0.1/10.4.0.50///0/"), "DHCP..false.150", "10.9.9.9"),   // no definition for 150
        Option(RangeRef("10.4.0.1/10.4.0.50///5/"), "DHCP..false.150", "10.8.8.8"),   // the same addresses, in network view 5
        Option(RangeRef("2001:db8:0:1000::/2001:db8:0:1fff::///0/"), "DHCPv6..false.23", "2001:db8::53", ipv4: false),

        // Not on ranges and not kept: an option on a fixed address (a big grid has very many of these).
        Option(".com.infoblox.dns.fixed_address$10.1.0.5.0..", "DHCP..false.67", "boot.efi"),

        // What a range can inherit. The container 10.0.0.0/8 sets 3, 15 and 42; the network 10.1.0.0/24 in it sets 15 again; the
        // network 10.2.0.0/24 sets 3; the grid sets 4 and 6.
        Option(".com.infoblox.dns.network_container$10.0.0.0/8/0", "DHCP..false.3", "10.0.0.254"),
        Option(".com.infoblox.dns.network_container$10.0.0.0/8/0", "DHCP..false.15", "container.test"),
        Option(".com.infoblox.dns.network_container$10.0.0.0/8/0", "DHCP..false.42", "10.0.0.123"),
        Option(".com.infoblox.dns.network$10.1.0.0/24/0", "DHCP..false.15", "net.test"),
        Option(".com.infoblox.dns.network$10.2.0.0/24/0", "DHCP..false.3", "10.2.0.1"),
        Option(".com.infoblox.dns.cluster_dhcp_properties$0", "DHCP..false.6", "8.8.8.8"),
        Option(".com.infoblox.dns.cluster_dhcp_properties$0", "DHCP..false.4", "10.0.0.123"),

        // On ranges, but cannot be placed: an unknown range, and an option number that isn't one.
        Option(RangeRef("10.77.0.1/10.77.0.9///0/"), "DHCP..false.3", "10.77.0.1"),
        Option(RangeRef("10.1.0.10/10.1.0.99///0/"), "DHCP..false.abc", "x"),

        "</DATABASE>",
    ]);

    private static GridInventory Parse() => BackupParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(Backup())));

    private static DhcpRange RangeAt(GridInventory inventory, string start, int view = 0) =>
        inventory.DhcpRanges.Single(r => r.StartAddress == start && r.NetworkViewId == view);

    // ---- Lease time: the one the range really has ----

    [Fact]
    public void A_range_that_does_not_set_a_lease_takes_its_containers_or_networks_or_the_grids()
    {
        var inventory = Parse();

        var container = RangeAt(inventory, "10.1.0.10");
        Assert.Equal((7200, "Network container"), (container.LeaseTimeSeconds, container.LeaseTimeSource));

        var network = RangeAt(inventory, "10.2.0.10");
        Assert.Equal((600, "Network"), (network.LeaseTimeSeconds, network.LeaseTimeSource));

        var grid = RangeAt(inventory, "10.3.0.10");
        Assert.Equal((86400, "Grid"), (grid.LeaseTimeSeconds, grid.LeaseTimeSource));
    }

    [Fact]
    public void A_range_that_sets_its_own_lease_uses_it()
    {
        var range = RangeAt(Parse(), "10.3.0.100");

        Assert.Equal((120, "Range", true), (range.LeaseTimeSeconds, range.LeaseTimeSource, range.LeaseTimeOverridden));
    }

    [Fact]
    public void The_lease_a_range_stores_when_it_does_not_override_is_ignored_because_it_can_be_stale()
    {
        // All of these store 43200; none of them has it.
        var inventory = Parse();

        Assert.NotEqual(43200, RangeAt(inventory, "10.1.0.10").LeaseTimeSeconds);
        Assert.NotEqual(43200, RangeAt(inventory, "10.2.0.10").LeaseTimeSeconds);
        Assert.NotEqual(43200, RangeAt(inventory, "10.3.0.10").LeaseTimeSeconds);
    }

    [Fact]
    public void Without_a_grid_default_the_stored_value_is_the_best_there_is_and_is_marked_so()
    {
        var xml = Backup().Replace(O("dns.cluster_dhcp_properties", ("lease_time", "86400")), "");

        var inventory = BackupParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        var range = RangeAt(inventory, "10.3.0.10");
        Assert.Equal((43200, "Backup"), (range.LeaseTimeSeconds, range.LeaseTimeSource));
    }

    [Fact]
    public void A_loop_in_the_container_chain_does_not_hang_the_parser()
    {
        var xml = string.Join("\n",
        [
            "<DATABASE NAME=\"onedb\" VERSION=\"9.0.8-1\">",
            O("dns.network_container", ("address", "10.0.0.0"), ("cidr", "8"), ("network_view", "0"), ("parent", ".com.infoblox.dns.network_container$10.0.0.0/8/0"),
                ("override_lease_time", "false"), ("lease_time", "1")),
            Network("10.1.0.0", 24, 0, ".com.infoblox.dns.network_container$10.0.0.0/8/0"),
            Range("10.1.0.0/24/0", "10.1.0.10", "10.1.0.99"),
            "</DATABASE>",
        ]);

        var inventory = BackupParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        Assert.Equal(43200, Assert.Single(inventory.DhcpRanges).LeaseTimeSeconds); // gave up going up, and used what the range stores
    }

    // ---- Options ----

    [Fact]
    public void Options_set_on_a_range_are_listed_with_it_by_number()
    {
        var range = RangeAt(Parse(), "10.1.0.10");

        Assert.Equal([3, 6], range.Options.Select(o => o.Code));
        Assert.Equal(["10.1.0.1", "10.0.0.53,10.0.0.54"], range.Options.Select(o => o.Value));
        Assert.Equal(["routers", "domain-name-servers"], range.Options.Select(o => o.Name));
        Assert.All(range.Options, o => Assert.Equal("DHCP..false", o.Space));
    }

    [Fact]
    public void The_number_is_the_last_part_of_the_definitions_name_whatever_the_space_is_called()
    {
        var option = Assert.Single(RangeAt(Parse(), "10.2.0.10").Options);

        Assert.Equal((66, "tftp.lab.test", "DHCP Standard Options.12.false"), (option.Code, option.Value, option.Space));
        Assert.Null(option.Name); // that space has no definition in this backup: the number and value still show
    }

    [Fact]
    public void An_option_with_no_definition_is_still_listed_with_no_name()
    {
        var option = Assert.Single(RangeAt(Parse(), "10.4.0.1", view: 0).Options);

        Assert.Equal((150, "10.9.9.9", null), (option.Code, option.Value, option.Name));
    }

    [Fact]
    public void Two_ranges_with_the_same_addresses_in_different_network_views_each_get_their_own_options()
    {
        var inventory = Parse();

        Assert.Equal("10.9.9.9", Assert.Single(RangeAt(inventory, "10.4.0.1", view: 0).Options).Value);
        Assert.Equal("10.8.8.8", Assert.Single(RangeAt(inventory, "10.4.0.1", view: 5).Options).Value);
    }

    [Fact]
    public void A_prefix_range_is_found_by_its_prefixes_and_keeps_its_option()
    {
        var range = Assert.Single(Parse().DhcpRanges, r => r.AddressType == "PREFIX");

        Assert.Equal(("2001:db8:0:1000::", "2001:db8:0:1fff::", 64), (range.PrefixStart, range.PrefixEnd, range.PrefixBits));
        Assert.True(range.IsIpv6);
        var option = Assert.Single(range.Options);
        Assert.Equal((23, "2001:db8::53", false, "dns-servers"), (option.Code, option.Value, option.IsIpv4, option.Name));
    }

    [Fact]
    public void Options_on_fixed_addresses_are_not_kept_and_not_counted_as_lost()
    {
        var inventory = Parse();

        Assert.DoesNotContain(inventory.DhcpRanges.SelectMany(r => r.Options), o => o.Value == "boot.efi");
        Assert.DoesNotContain(inventory.Networks.SelectMany(n => n.Options), o => o.Value == "boot.efi");
        Assert.DoesNotContain(inventory.GridDhcpOptions, o => o.Value == "boot.efi");
        // Only the two that name a range or number that can't be used are counted: not this one.
        Assert.Equal(2, inventory.UnmatchedRangeOptions);
    }

    // ---- Options a range inherits ----

    private static IpNetwork NetworkAt(GridInventory inventory, string key) => inventory.Networks.Single(n => n.Key == key);

    [Fact]
    public void Options_set_on_a_network_or_a_container_are_kept_with_it_by_number()
    {
        var inventory = Parse();

        var container = NetworkAt(inventory, "10.0.0.0/8/0");
        Assert.True(container.IsContainer);
        Assert.Equal([3, 15, 42], container.Options.Select(o => o.Code));
        Assert.Equal(["routers", "domain-name", "ntp-servers"], container.Options.Select(o => o.Name));
        Assert.Equal(["10.0.0.254", "container.test", "10.0.0.123"], container.Options.Select(o => o.Value));

        Assert.Equal(["net.test"], NetworkAt(inventory, "10.1.0.0/24/0").Options.Select(o => o.Value));
        Assert.Equal(["10.2.0.1"], NetworkAt(inventory, "10.2.0.0/24/0").Options.Select(o => o.Value));
        Assert.Empty(NetworkAt(inventory, "10.3.0.0/24/0").Options);
    }

    [Fact]
    public void A_network_knows_the_container_it_sits_in()
    {
        var inventory = Parse();

        Assert.Equal("10.0.0.0/8/0", NetworkAt(inventory, "10.1.0.0/24/0").ParentKey);
        Assert.Null(NetworkAt(inventory, "10.0.0.0/8/0").ParentKey);   // the container is directly in its network view
        Assert.Null(NetworkAt(inventory, "10.2.0.0/24/0").ParentKey);
    }

    [Fact]
    public void Options_set_at_the_grid_level_are_kept_by_number()
    {
        var inventory = Parse();

        Assert.Equal([4, 6], inventory.GridDhcpOptions.Select(o => o.Code));
        Assert.Equal(["10.0.0.123", "8.8.8.8"], inventory.GridDhcpOptions.Select(o => o.Value));
        Assert.Equal(["time-servers", "domain-name-servers"], inventory.GridDhcpOptions.Select(o => o.Name));
    }

    [Fact]
    public void An_option_on_a_network_that_is_not_there_is_counted_not_guessed_at()
    {
        var xml = Backup().Replace("</DATABASE>", Option(".com.infoblox.dns.network$10.250.0.0/24/0", "DHCP..false.3", "x") + "\n</DATABASE>");

        var inventory = BackupParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        Assert.Equal(3, inventory.UnmatchedRangeOptions);
    }

    [Fact]
    public void Options_that_cannot_be_placed_are_counted_not_guessed_at()
    {
        var inventory = Parse();

        Assert.Equal(2, inventory.UnmatchedRangeOptions);
        Assert.Equal(8, inventory.DhcpRanges.Sum(r => r.Options.Count) + inventory.UnmatchedRangeOptions); // 6 placed, 2 not
        Assert.Equal(6, inventory.DhcpRanges.Sum(r => r.Options.Count));
    }

    [Fact]
    public void A_backup_read_by_this_version_says_it_read_options_and_a_new_inventory_says_it_did_not()
    {
        Assert.True(Parse().DhcpOptionsRead);
        Assert.False(new GridInventory().DhcpOptionsRead);
    }

    [Fact]
    public void The_existing_shared_fixtures_ranges_still_parse_and_have_no_options()
    {
        var inventory = BackupParser.Parse(OneDbFixture.XmlStream());

        Assert.Equal(3, inventory.DhcpRanges.Count);
        Assert.All(inventory.DhcpRanges, r => Assert.Empty(r.Options));
        Assert.True(inventory.DhcpOptionsRead);
    }

    // ---- Stored and queried ----

    [Fact]
    public void Lease_times_and_options_survive_being_saved_as_a_snapshot()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"pfr-dhcp-{Guid.NewGuid():N}");
        try
        {
            var store = new SecureStore(folder);
            store.Initialize("correct horse battery", SecureStoreTests.TestIterations);
            var workspace = new ClientWorkspace(store);
            var client = workspace.CreateClient("Acme");
            var original = Parse();

            var (_, info) = workspace.AddSnapshot(client.Id, original, "lab.bak", 1, "sha");
            var grid = workspace.GetClient(client.Id).Grids.Single();
            var back = workspace.LoadSnapshot(client.Id, grid.Id, info.Id);

            Assert.True(back.DhcpOptionsRead);
            Assert.Equal(2, back.UnmatchedRangeOptions);
            var range = back.DhcpRanges.Single(r => r.StartAddress == "10.1.0.10");
            Assert.Equal((7200, "Network container", false), (range.LeaseTimeSeconds, range.LeaseTimeSource, range.LeaseTimeOverridden));
            Assert.Equal(["routers:10.1.0.1", "domain-name-servers:10.0.0.53,10.0.0.54"], range.Options.Select(o => $"{o.Name}:{o.Value}"));
            Assert.Equal(["domain-name:net.test"], back.Networks.Single(n => n.Key == "10.1.0.0/24/0").Options.Select(o => $"{o.Name}:{o.Value}"));
            Assert.Equal("10.0.0.0/8/0", back.Networks.Single(n => n.Key == "10.1.0.0/24/0").ParentKey);
            Assert.Equal([4, 6], back.GridDhcpOptions.Select(o => o.Code));
            store.Dispose();
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void A_snapshot_saved_without_lease_times_or_options_loads_and_says_it_has_none()
    {
        // What a snapshot saved before this version holds: a range with no lease time, no options, and no flag saying they were read.
        var folder = Path.Combine(Path.GetTempPath(), $"pfr-dhcp-{Guid.NewGuid():N}");
        try
        {
            var store = new SecureStore(folder);
            store.Initialize("correct horse battery", SecureStoreTests.TestIterations);
            var workspace = new ClientWorkspace(store);
            var client = workspace.CreateClient("Acme");
            var old = new GridInventory { GridName = "Old" };
            old.DhcpRanges.Add(new DhcpRange { NetworkKey = "10.0.0.0/24/0", StartAddress = "10.0.0.10", EndAddress = "10.0.0.20" });

            var (_, info) = workspace.AddSnapshot(client.Id, old, "old.bak", 1, "sha");
            var back = workspace.LoadSnapshot(client.Id, workspace.GetClient(client.Id).Grids.Single().Id, info.Id);

            Assert.False(back.DhcpOptionsRead);
            var range = Assert.Single(back.DhcpRanges);
            Assert.Null(range.LeaseTimeSeconds);
            Assert.Empty(range.Options);
            Assert.Equal("ADDRESS", range.AddressType);
            store.Dispose();
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void The_sql_tables_carry_the_lease_time_and_one_row_per_option()
    {
        using var database = SnapshotDatabase.Load(Parse());

        var ranges = database.Execute(
            "SELECT start_address, lease_time, lease_time_source, option_count FROM dhcp_ranges WHERE address_type = 'ADDRESS' ORDER BY network_view_id, ip_key(start_address)");
        var first = ranges.Rows.First(r => (string?)r[0] == "10.1.0.10");
        Assert.Equal((7200L, "Network container", 2L), ((long)first[1]!, (string)first[2]!, (long)first[3]!));

        var options = database.Execute(
            "SELECT o.option_code, o.option_name, o.value FROM dhcp_range_options o JOIN dhcp_ranges r " +
            "ON r.network_key = o.network_key AND r.start_address = o.start_address AND r.end_address = o.end_address " +
            "WHERE r.start_address = '10.1.0.10' ORDER BY o.option_code");
        Assert.Equal(["3|routers|10.1.0.1", "6|domain-name-servers|10.0.0.53,10.0.0.54"],
            options.Rows.Select(r => $"{r[0]}|{r[1]}|{r[2]}"));

        Assert.Equal(6L, (long)database.Execute("SELECT COUNT(*) FROM dhcp_range_options").Rows[0][0]!);

        // Options on networks and containers: 3 on the container, 1 on each of two networks.
        var network = database.Execute("SELECT network_key, is_container, option_code, option_name, value FROM dhcp_network_options ORDER BY network_key, option_code").Rows;
        Assert.Equal(5, network.Count);
        Assert.Contains(network, r => (string)r[0]! == "10.0.0.0/8/0" && (long)r[1]! == 1 && (long)r[2]! == 15 && (string?)r[3] == "domain-name");
        Assert.Contains(network, r => (string)r[0]! == "10.1.0.0/24/0" && (long)r[1]! == 0 && (string)r[4]! == "net.test");
    }
}

public class DhcpRangeReportTests
{
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>Network views "default" (0) and "Corp" (5), and ranges in them.</summary>
    private static GridInventory Grid()
    {
        var inventory = new GridInventory { GridName = "Lab", DhcpOptionsRead = true };
        inventory.NetworkViews.Add(new NetworkView { Id = 0, Name = "default" });
        inventory.NetworkViews.Add(new NetworkView { Id = 5, Name = "Corp" });

        inventory.DhcpRanges.AddRange(
        [
            Range("10.9.16.0/24/0", "10.9.16.100", "10.9.16.199", 43200, "Grid", ("6", "10.0.0.53")),
            Range("10.9.2.0/24/0", "10.9.2.10", "10.9.2.20", 600, "Network"),
            Range("10.9.16.0/24/0", "10.9.16.10", "10.9.16.50", 3600, "Range", ("3", "10.9.16.1"), ("66", "tftp.lab.test"), ("6", "10.0.0.53,10.0.0.54")),
            Range("10.9.16.0/24/5", "10.9.16.10", "10.9.16.50", 7200, "Range", ("150", "10.8.8.8")),
            Range("2603:3007:1a01:310e::/64/0", "2603:3007:1a01:310e::1", "2603:3007:1a01:310e::ff", 43200, "Grid", ("23", "2603::53")),
        ]);
        var prefix = Range("2001:db8:0:1::/64/0", "", "", 43200, "Grid");
        prefix.AddressType = "PREFIX";
        prefix.PrefixStart = "2001:db8:0:1000::";
        prefix.PrefixEnd = "2001:db8:0:1fff::";
        inventory.DhcpRanges.Add(prefix);
        return inventory;
    }

    private static DhcpRange Range(string network, string start, string end, int lease, string source, params (string Code, string Value)[] options)
    {
        var slash = network.LastIndexOf('/');
        var range = new DhcpRange
        {
            NetworkKey = network, StartAddress = start, EndAddress = end, NetworkViewId = int.Parse(network[(slash + 1)..]),
            IsIpv6 = network.Contains(':'), LeaseTimeSeconds = lease, LeaseTimeSource = source,
        };
        foreach (var (code, value) in options) range.Options.Add(new DhcpOption { Code = int.Parse(code), Value = value, Space = "DHCP..false" });
        return range;
    }

    [Fact]
    public void The_columns_are_the_six_that_were_asked_for_in_order_and_then_where_the_option_is_set()
    {
        Assert.Equal(["Network/CIDR/View", "Start address", "End address", "Default lease", "Option number", "Option value", "Set on"],
            DhcpRangeResult.Columns);
        Assert.Equal(DhcpRangeResult.Columns, DhcpRangeReport.Run(Grid()).ToResult().Columns);
    }

    [Fact]
    public void There_is_a_line_for_each_option_with_the_network_cidr_and_view_name_in_the_first_column()
    {
        var rows = DhcpRangeReport.Run(Grid()).Rows;

        var range = rows.Where(r => r.Start == "10.9.16.10" && r.Network == "10.9.16.0/24/default").ToList();
        Assert.Equal(["3|10.9.16.1", "6|10.0.0.53,10.0.0.54", "66|tftp.lab.test"], range.Select(r => $"{r.OptionCode}|{r.OptionValue}"));
        Assert.All(range, r => Assert.Equal(("10.9.16.50", 3600), (r.End, r.LeaseSeconds)));  // the range's own details repeat on each line
    }

    [Fact]
    public void The_view_is_named_not_numbered_and_the_same_addresses_in_another_view_are_separate()
    {
        var rows = DhcpRangeReport.Run(Grid()).Rows;

        Assert.Contains(rows, r => r.Network == "10.9.16.0/24/Corp" && r.OptionValue == "10.8.8.8");
        Assert.Contains(rows, r => r.Network == "10.9.16.0/24/default" && r.OptionValue == "10.0.0.53");
    }

    [Fact]
    public void A_view_with_no_name_is_shown_by_its_number()
    {
        var inventory = new GridInventory { DhcpOptionsRead = true };
        inventory.DhcpRanges.Add(Range("10.0.0.0/24/9", "10.0.0.10", "10.0.0.20", 100, "Grid"));

        Assert.Equal("10.0.0.0/24/9", Assert.Single(DhcpRangeReport.Run(inventory).Rows).Network);
    }

    [Fact]
    public void A_range_with_no_options_has_one_line_with_the_option_columns_empty()
    {
        var line = Assert.Single(DhcpRangeReport.Run(Grid()).Rows, r => r.Start == "10.9.2.10");

        Assert.Equal(("10.9.2.0/24/default", "10.9.2.20", 600, null, null), (line.Network, line.End, line.LeaseSeconds, line.OptionCode, line.OptionValue));
    }

    [Fact]
    public void Ranges_without_options_can_be_left_out()
    {
        var result = DhcpRangeReport.Run(Grid(), new DhcpRangeReportOptions { IncludeRangesWithoutOptions = false });

        Assert.All(result.Rows, r => Assert.NotNull(r.OptionCode));
        Assert.DoesNotContain(result.Rows, r => r.Start == "10.9.2.10");
        Assert.Equal(6, result.RangeCount);           // every range is still counted
        Assert.Equal(2, result.RangesWithoutOptions);
    }

    [Fact]
    public void Ipv6_ranges_can_be_left_out()
    {
        var result = DhcpRangeReport.Run(Grid(), new DhcpRangeReportOptions { IncludeIpv6 = false });

        Assert.DoesNotContain(result.Rows, r => r.Network.Contains(':'));
        Assert.Equal(4, result.RangeCount);
    }

    [Fact]
    public void A_prefix_range_shows_its_prefixes_where_the_start_and_end_would_be()
    {
        var line = Assert.Single(DhcpRangeReport.Run(Grid()).Rows, r => r.Network == "2001:db8:0:1::/64/default");

        Assert.Equal(("2001:db8:0:1000::", "2001:db8:0:1fff::"), (line.Start, line.End));
    }

    [Fact]
    public void Lines_are_in_view_then_network_then_address_order_and_ipv4_addresses_sort_as_numbers()
    {
        var networks = DhcpRangeReport.Run(Grid()).Rows.Select(r => r.Network).Distinct().ToList();

        // Corp before default (by name); within default, 10.9.2.0 before 10.9.16.0 (2 < 16, not "16" < "2"), then IPv6.
        Assert.Equal(["10.9.16.0/24/Corp", "10.9.2.0/24/default", "10.9.16.0/24/default", "2001:db8:0:1::/64/default", "2603:3007:1a01:310e::/64/default"], networks);

        var starts = DhcpRangeReport.Run(Grid()).Rows.Where(r => r.Network == "10.9.16.0/24/default").Select(r => r.Start).Distinct().ToList();
        Assert.Equal(["10.9.16.10", "10.9.16.100"], starts);
    }

    [Fact]
    public void The_counts_describe_the_report()
    {
        var result = DhcpRangeReport.Run(Grid());

        Assert.Equal(6, result.RangeCount);
        Assert.Equal(4, result.RangesWithOptions);
        Assert.Equal(2, result.RangesWithoutOptions);
        Assert.Equal(6, result.OptionCount);
        Assert.Equal(8, result.Rows.Count);      // 6 option lines and 2 for ranges with none
        Assert.True(result.OptionsRead);
    }

    [Fact]
    public void A_snapshot_saved_before_options_were_read_says_so_rather_than_reporting_no_options()
    {
        var inventory = Grid();
        inventory.DhcpOptionsRead = false;

        var result = DhcpRangeReport.Run(inventory);

        Assert.False(result.OptionsRead);
    }

    [Fact]
    public void Options_that_could_not_be_placed_are_carried_to_the_result()
    {
        var inventory = Grid();
        inventory.UnmatchedRangeOptions = 3;

        Assert.Equal(3, DhcpRangeReport.Run(inventory).UnmatchedOptions);
    }

    [Fact]
    public void The_lease_and_option_number_are_numbers_and_the_rest_text()
    {
        var result = DhcpRangeReport.Run(Grid()).ToResult();

        var withOption = result.Rows.First(r => (string?)r[5] == "10.9.16.1");
        Assert.Equal(3600L, withOption[3]);
        Assert.Equal(3L, withOption[4]);
        Assert.IsType<string>(withOption[0]);
        var without = result.Rows.First(r => (string?)r[1] == "10.9.2.10");
        Assert.Null(without[4]);
        Assert.Null(without[5]);
    }

    [Fact]
    public void A_grid_with_no_ranges_gives_an_empty_report()
    {
        var result = DhcpRangeReport.Run(new GridInventory { DhcpOptionsRead = true });

        Assert.Empty(result.Rows);
        Assert.Equal(0, result.RangeCount);
    }

    // ---- Inherited options ----

    private static DhcpOption Option(int code, string value, bool ipv4 = true) =>
        new() { Code = code, Value = value, Space = "DHCP..false", IsIpv4 = ipv4 };

    /// <summary>
    /// The container 10.0.0.0/8 sets 3, 15 and 42. The network 10.1.0.0/24 in it sets 15 again. A range in that network sets 3.
    /// The grid sets 3, 4 and 6.
    /// </summary>
    private static GridInventory Inheriting()
    {
        var inventory = new GridInventory { GridName = "Lab", DhcpOptionsRead = true };
        inventory.NetworkViews.Add(new NetworkView { Id = 0, Name = "default" });

        var container = new IpNetwork { Address = "10.0.0.0", Cidr = 8, NetworkViewId = 0, IsContainer = true };
        container.Options.AddRange([Option(3, "10.0.0.254"), Option(15, "container.test"), Option(42, "10.0.0.123")]);
        var network = new IpNetwork { Address = "10.1.0.0", Cidr = 24, NetworkViewId = 0, ParentKey = container.Key };
        network.Options.Add(Option(15, "net.test"));
        inventory.Networks.AddRange([container, network]);

        inventory.GridDhcpOptions.AddRange([Option(3, "8.8.8.1"), Option(4, "10.9.9.9"), Option(6, "8.8.8.8")]);
        inventory.DhcpRanges.Add(Range("10.1.0.0/24/0", "10.1.0.10", "10.1.0.99", 3600, "Range", ("3", "10.1.0.1")));
        return inventory;
    }

    private static List<string> Lines(DhcpRangeResult result) =>
        result.Rows.Select(r => $"{r.OptionCode}|{r.OptionValue}|{r.SetOn}").ToList();

    [Fact]
    public void A_range_lists_the_options_it_inherits_from_its_network_its_containers_and_the_grid_nearest_setting_first()
    {
        var result = DhcpRangeReport.Run(Inheriting());

        // 3 is the range's own (the container's and the grid's are overridden); 15 is the network's (the container's is overridden).
        Assert.Equal(
            ["3|10.1.0.1|Range", "4|10.9.9.9|Grid", "6|8.8.8.8|Grid", "15|net.test|Network 10.1.0.0/24", "42|10.0.0.123|Network container 10.0.0.0/8"],
            Lines(result));
    }

    [Fact]
    public void Turning_inheritance_off_lists_only_what_is_set_on_the_range()
    {
        var result = DhcpRangeReport.Run(Inheriting(), new DhcpRangeReportOptions { IncludeInheritedOptions = false });

        Assert.Equal(["3|10.1.0.1|Range"], Lines(result));
        Assert.Equal(0, result.InheritedOptionCount);
    }

    [Fact]
    public void A_range_with_no_options_of_its_own_still_shows_what_it_inherits()
    {
        var inventory = Inheriting();
        inventory.DhcpRanges[0].Options.Clear();

        var result = DhcpRangeReport.Run(inventory);

        Assert.Equal(
            ["3|10.0.0.254|Network container 10.0.0.0/8", "4|10.9.9.9|Grid", "6|8.8.8.8|Grid", "15|net.test|Network 10.1.0.0/24", "42|10.0.0.123|Network container 10.0.0.0/8"],
            Lines(result));
        Assert.All(result.Rows, r => Assert.True(r.IsInherited));
        Assert.Equal((1, 0, 5, 5), (result.RangesWithOptions, result.RangesWithoutOptions, result.OptionCount, result.InheritedOptionCount));
    }

    [Fact]
    public void The_counts_say_how_many_lines_are_inherited()
    {
        var result = DhcpRangeReport.Run(Inheriting());

        Assert.Equal((1, 5, 4), (result.RangeCount, result.OptionCount, result.InheritedOptionCount));
        Assert.False(result.Rows.Single(r => r.OptionCode == 3).IsInherited);
    }

    [Fact]
    public void An_ipv4_range_does_not_inherit_dhcpv6_options_and_the_reverse()
    {
        var inventory = new GridInventory { DhcpOptionsRead = true };
        inventory.GridDhcpOptions.AddRange([Option(6, "8.8.8.8"), Option(23, "2001:db8::53", ipv4: false)]);
        inventory.DhcpRanges.Add(Range("10.0.0.0/24/0", "10.0.0.10", "10.0.0.20", 100, "Grid"));
        inventory.DhcpRanges.Add(Range("2001:db8::/64/0", "2001:db8::10", "2001:db8::20", 100, "Grid"));

        var rows = DhcpRangeReport.Run(inventory).Rows;

        Assert.Equal(["6|8.8.8.8|Grid"], rows.Where(r => r.Network.StartsWith("10.")).Select(r => $"{r.OptionCode}|{r.OptionValue}|{r.SetOn}"));
        Assert.Equal(["23|2001:db8::53|Grid"], rows.Where(r => r.Network.StartsWith("2001:")).Select(r => $"{r.OptionCode}|{r.OptionValue}|{r.SetOn}"));
    }

    [Fact]
    public void An_option_set_twice_at_one_level_is_listed_twice_and_a_nearer_setting_hides_both()
    {
        var inventory = new GridInventory { DhcpOptionsRead = true };
        var network = new IpNetwork { Address = "10.0.0.0", Cidr = 24, NetworkViewId = 0 };
        network.Options.AddRange([Option(6, "10.0.0.53"), Option(6, "10.0.0.54")]);
        inventory.Networks.Add(network);
        inventory.DhcpRanges.Add(Range("10.0.0.0/24/0", "10.0.0.10", "10.0.0.20", 100, "Grid"));
        inventory.DhcpRanges.Add(Range("10.0.0.0/24/0", "10.0.0.30", "10.0.0.40", 100, "Grid", ("6", "1.1.1.1")));

        var rows = DhcpRangeReport.Run(inventory).Rows;

        Assert.Equal(["6|10.0.0.53|Network 10.0.0.0/24", "6|10.0.0.54|Network 10.0.0.0/24"],
            rows.Where(r => r.Start == "10.0.0.10").Select(r => $"{r.OptionCode}|{r.OptionValue}|{r.SetOn}"));
        Assert.Equal(["6|1.1.1.1|Range"], rows.Where(r => r.Start == "10.0.0.30").Select(r => $"{r.OptionCode}|{r.OptionValue}|{r.SetOn}"));
    }

    [Fact]
    public void A_range_whose_network_is_not_known_still_inherits_the_grids_options()
    {
        var inventory = new GridInventory { DhcpOptionsRead = true };
        inventory.GridDhcpOptions.Add(Option(6, "8.8.8.8"));
        inventory.DhcpRanges.Add(Range("10.99.0.0/24/0", "10.99.0.10", "10.99.0.20", 100, "Grid"));

        Assert.Equal(["6|8.8.8.8|Grid"], Lines(DhcpRangeReport.Run(inventory)));
    }

    [Fact]
    public void A_loop_in_the_container_chain_does_not_hang_the_report()
    {
        var inventory = new GridInventory { DhcpOptionsRead = true };
        var container = new IpNetwork { Address = "10.0.0.0", Cidr = 8, NetworkViewId = 0, IsContainer = true };
        container.ParentKey = container.Key;                                  // contains itself
        container.Options.Add(Option(15, "loop.test"));
        var network = new IpNetwork { Address = "10.1.0.0", Cidr = 24, NetworkViewId = 0, ParentKey = container.Key };
        inventory.Networks.AddRange([container, network]);
        inventory.DhcpRanges.Add(Range("10.1.0.0/24/0", "10.1.0.10", "10.1.0.20", 100, "Grid"));

        Assert.Equal(["15|loop.test|Network container 10.0.0.0/8"], Lines(DhcpRangeReport.Run(inventory)));
    }

    [Fact]
    public void A_range_with_no_options_anywhere_is_one_line_with_nothing_in_the_option_columns()
    {
        var inventory = new GridInventory { DhcpOptionsRead = true };
        inventory.DhcpRanges.Add(Range("10.0.0.0/24/0", "10.0.0.10", "10.0.0.20", 100, "Grid"));

        var line = Assert.Single(DhcpRangeReport.Run(inventory).Rows);

        Assert.Equal((null, null, ""), (line.OptionCode, line.OptionValue, line.SetOn));
    }

    // ---- As a file ----

    [Fact]
    public void Written_as_excel_it_has_the_six_headers_and_numbers_where_they_are_numbers()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"pfr-dhcp-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, "dhcp.xlsx");
        try
        {
            var result = DhcpRangeReport.Run(Grid());

            ResultExport.Write(result.ToResult(), path, spreadsheetSafe: false, title: DhcpRangeReport.Title);

            using var zip = ZipFile.OpenRead(path);
            using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var cells = XDocument.Load(stream).Descendants(Sheet + "c").ToDictionary(
                c => c.Attribute("r")!.Value,
                c => (Type: c.Attribute("t")?.Value, Value: c.Element(Sheet + "is")?.Element(Sheet + "t")?.Value ?? c.Element(Sheet + "v")?.Value ?? ""));

            Assert.Equal(DhcpRangeResult.Columns, "ABCDEFG".Select(column => cells[column + "1"].Value).ToArray());
            Assert.Equal(result.Rows.Count + 1, cells.Keys.Count(k => k[0] == 'A'));
            Assert.Equal("10.9.16.0/24/Corp", cells["A2"].Value);
            Assert.Null(cells["D2"].Type);                     // the lease: a number
            Assert.Equal("7200", cells["D2"].Value);
            Assert.Null(cells["E2"].Type);                     // the option number: a number
            Assert.Equal("150", cells["E2"].Value);
            Assert.Equal("inlineStr", cells["F2"].Type);       // the value: text
            Assert.Equal("Range", cells["G2"].Value);          // where the option is set
            using var book = zip.GetEntry("xl/workbook.xml")!.Open();
            Assert.Equal("DHCP range report", XDocument.Load(book).Descendants(Sheet + "sheet").Single().Attribute("name")!.Value);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
