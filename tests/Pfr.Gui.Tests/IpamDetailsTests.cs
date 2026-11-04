using Pfr.Core.Ipam;
using Pfr.Core.Nios.Model;
using Pfr.Core.Tests.Ipam;
using Pfr.Gui.Tables;

namespace Pfr.Gui.Tests;

public class IpamDetailsTests
{
    private static IpamPlan Plan(params GridInventory[] grids) => IpamBuilder.Build(grids.Select(g => g.Input()));

    private static IpamNode Node(IpamPlan plan, string key, string view = "default") =>
        plan.Views.Single(v => v.Name == view).AllNodes().Single(n => n.Key == key);

    private static string Row(IReadOnlyList<string> row, int count) => string.Join("|", row.Take(count));

    [Fact]
    public void A_network_shows_one_row_per_grid_with_what_that_grid_says()
    {
        var plan = Plan(
            IpamFixtures.Grid("Lab").AddNet("10.1.0.0", 24, comment: "Servers"),
            IpamFixtures.Grid("DR").AddNet("10.1.0.0", 24, comment: "Server LAN", disabled: true));

        var detail = IpamDetails.For(Node(plan, "10.1.0.0/24"))!;

        Assert.Equal("10.1.0.0/24 · Network · IPv4", detail.Title);
        Assert.Equal("Grid,Network view,Kind,Comment,Disabled,Snapshot", string.Join(",", detail.Headers));
        Assert.Equal(2, detail.Rows.Count);
        Assert.Equal("DR|default|Network|Server LAN|Yes", Row(detail.Rows[0], 5));
        Assert.Equal("Lab|default|Network|Servers|", Row(detail.Rows[1], 5));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", detail.Rows[0][5]); // the snapshot each came from
        Assert.Equal("", detail.Note);
    }

    [Fact]
    public void A_network_in_several_views_lists_them_all_and_says_it_is_one_network()
    {
        var plan = Plan(IpamFixtures.Grid("A", (1, "Guest")).AddNet("10.0.0.0", 24).AddNet("10.0.0.0", 24, view: 1));

        var detail = IpamDetails.For(Node(plan, "10.0.0.0/24"))!; // the one under "default"

        Assert.Equal("A|default,A|Guest", string.Join(",", detail.Rows.Select(r => r[0] + "|" + r[1])));
        Assert.Equal(2, detail.Rows.Count); // both views' presences, not only this view's
        Assert.Contains("2 network views (default, Guest)", detail.Note);
        Assert.Contains("one network", detail.Note);
    }

    [Fact]
    public void Grids_that_disagree_on_the_kind_are_pointed_out()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.0.0.0", 8, container: true),
            IpamFixtures.Grid("B").AddNet("10.0.0.0", 8));

        var detail = IpamDetails.For(Node(plan, "10.0.0.0/8"))!;

        Assert.Equal("Container", detail.Rows[0][2]);
        Assert.Equal("Network", detail.Rows[1][2]);
        Assert.Contains("disagree on whether this is a container", detail.Note);
    }

    [Fact]
    public void A_range_shows_its_details_and_where_it_sits()
    {
        var plan = Plan(IpamFixtures.Grid("A").AddNet("10.1.0.0", 24)
            .AddRange("10.1.0.100", "10.1.0.199", association: "Failover", failover: "gm-gmc", name: "Staff", comment: "wifi", disabled: true));
        var node = Node(plan, "10.1.0.0/24");

        var detail = IpamDetails.For(new IpamRangeTag(node.Ranges.Single(), node))!;

        Assert.Equal("DHCP range 10.1.0.100 – 10.1.0.199", detail.Title);
        Assert.Equal("A|default|gm-gmc|Failover|Staff|wifi|Yes", Row(detail.Rows.Single(), 7));
        Assert.Equal("Sits in 10.1.0.0/24 (network), present in A.", detail.Note);
    }

    [Fact]
    public void A_range_in_a_network_its_own_grid_does_not_have_says_so()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.1.0.0", 24),
            IpamFixtures.Grid("B").AddRange("10.1.0.5", "10.1.0.9"));
        var node = Node(plan, "10.1.0.0/24");

        var detail = IpamDetails.For(new IpamRangeTag(node.Ranges.Single(), node))!;

        Assert.Equal("B", detail.Rows.Single()[0]);
        Assert.Contains("present in A", detail.Note);
        Assert.Contains("B does not have this network", detail.Note);
    }

    [Fact]
    public void A_range_in_no_network_says_so()
    {
        var plan = Plan(IpamFixtures.Grid("A").AddNet("10.0.0.0", 24).AddRange("192.168.1.1", "192.168.1.9"));
        var range = plan.Views.Single().Unplaced.Single();

        var detail = IpamDetails.For(new IpamRangeTag(range, null))!;

        Assert.Equal("Its start address is in no network of the default view.", detail.Note);
    }

    [Fact]
    public void A_network_view_counts_what_each_grid_has_in_it()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.0.0.0", 8, container: true).AddNet("10.1.0.0", 24).AddNet("10.2.0.0", 24)
                .AddRange("10.1.0.10", "10.1.0.20"),
            IpamFixtures.Grid("B").AddNet("10.1.0.0", 24).AddRange("192.168.0.1", "192.168.0.9"));

        var detail = IpamDetails.For(plan.Views.Single())!;

        Assert.Equal("Network view default", detail.Title);
        Assert.Equal("Grid,Networks,Containers,DHCP ranges,Hosts,DNS records", string.Join(",", detail.Headers));
        Assert.Equal("A|2|1|1|0|0", Row(detail.Rows[0], 6));
        Assert.Equal("B|1|0|1|0|0", Row(detail.Rows[1], 6));
    }

    [Fact]
    public void A_host_shows_its_details_its_other_addresses_aliases_and_what_it_generates()
    {
        var plan = Plan(IpamFixtures.Grid("A").WithDns().AddNet("10.0.0.0", 24).AddNet("10.9.9.0", 24)
            .AddHost("server1.lab.test", ["10.0.0.20", "10.9.9.9"], ttl: 600, aliases: ["files.lab.test"], dhcp: true,
                mac: "00:11:22:33:44:55", comment: "Web"));
        var node = Node(plan, "10.0.0.0/24");

        var detail = IpamDetails.For(new IpamHostTag(node.Hosts.Single(), node))!;

        Assert.Equal("Host server1.lab.test · 10.0.0.20", detail.Title);
        Assert.Equal("A|default|server1.lab.test|10.0.0.20|DNS|Yes|00:11:22:33:44:55|600 (Host)|", Row(detail.Rows.Single(), 9));
        Assert.Contains("Aliases: files.lab.test.", detail.Note);
        Assert.Contains("Also has the address 10.9.9.9; a host is listed once under each address it has.", detail.Note);
        Assert.Contains("Comment: Web.", detail.Note);
        Assert.Contains("Generates A and PTR records for this address", detail.Note);
        Assert.Contains("Sits in 10.0.0.0/24 (network), present in A.", detail.Note);
    }

    [Fact]
    public void An_IPAM_only_host_says_it_has_no_dns_records()
    {
        var plan = Plan(IpamFixtures.Grid("A").AddNet("10.0.0.0", 24).AddHost("printer", ["10.0.0.30"], dns: false));
        var node = Node(plan, "10.0.0.0/24");

        var detail = IpamDetails.For(new IpamHostTag(node.Hosts.Single(), node))!;

        Assert.Equal("IPAM only", detail.Rows.Single()[4]);
        Assert.Equal("", detail.Rows.Single()[7]); // no TTL
        Assert.Contains("no DNS records", detail.Note);
    }

    [Fact]
    public void A_host_in_no_network_says_so()
    {
        var plan = Plan(IpamFixtures.Grid("A").AddNet("10.0.0.0", 24).AddHost("far", ["192.168.1.1"], dns: false));
        var host = plan.Views.Single().UnplacedHosts.Single();

        var detail = IpamDetails.For(new IpamHostTag(host, null))!;

        Assert.Contains("Its address is in no network of the default view.", detail.Note);
    }

    [Fact]
    public void A_record_shows_its_details_and_the_host_that_generates_it()
    {
        var plan = Plan(IpamFixtures.Grid("A").WithDns().AddNet("10.0.0.0", 24).AddHost("server1.lab.test", ["10.0.0.20"], ttl: 600));
        var node = Node(plan, "10.0.0.0/24");
        var host = node.Hosts.Single();
        var ptr = host.Records.Single(r => r.Type == DnsRecordType.PTR);

        var detail = IpamDetails.For(new IpamRecordTag(ptr, node, host))!;

        Assert.Equal("PTR record 20.0.0.10.in-addr.arpa", detail.Title);
        Assert.Equal("A|default|Internal|0.0.10.in-addr.arpa|PTR|20.0.0.10.in-addr.arpa|server1.lab.test|10.0.0.20|600 (Host)", Row(detail.Rows.Single(), 9));
        Assert.Contains("its address 10.0.0.20 is the one in its reverse name", detail.Note);
        Assert.Contains("Generated by the host server1.lab.test; it is listed under that host", detail.Note);
    }

    [Fact]
    public void A_record_on_its_own_says_where_it_sits_and_which_grids_have_that_network()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.0.0.0", 24),
            IpamFixtures.Grid("B").WithDns().AddRecord(DnsRecordType.A, "www.lab.test", "10.0.0.10", ttl: 300));
        var node = Node(plan, "10.0.0.0/24");

        var detail = IpamDetails.For(new IpamRecordTag(node.Records.Single(), node, null))!;

        Assert.Equal("A record www.lab.test", detail.Title);
        Assert.Equal("300 (Record)", detail.Rows.Single()[8]);
        Assert.Contains("present in A. B does not have this network.", detail.Note);
        Assert.DoesNotContain("Generated by", detail.Note);
    }

    [Fact]
    public void Rows_with_nothing_to_show_give_no_details()
    {
        Assert.Null(IpamDetails.For(null));
        Assert.Null(IpamDetails.For("not a plan object"));
    }
}
