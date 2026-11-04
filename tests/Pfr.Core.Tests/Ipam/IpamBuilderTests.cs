using Pfr.Core.Ipam;
using Pfr.Core.Nios.Model;

namespace Pfr.Core.Tests.Ipam;

public class IpamBuilderTests
{
    private static IpamPlan Plan(params GridInventory[] grids) => IpamBuilder.Build(grids.Select(g => g.Input()));

    private static IpamView View(IpamPlan plan, string name) =>
        plan.Views.Single(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

    private static IpamNode Node(IpamPlan plan, string key, string view = "default") =>
        View(plan, view).AllNodes().Single(n => n.Key == key);

    private static string Join(IEnumerable<string> values) => string.Join(",", values);

    // ---- One network per prefix, with where it is present ----

    [Fact]
    public void The_same_prefix_on_three_grids_is_one_network_present_in_three()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.1.0.0", 16),
            IpamFixtures.Grid("B").AddNet("10.1.0.0", 16),
            IpamFixtures.Grid("C").AddNet("10.1.0.0", 16));

        var network = Assert.Single(plan.Networks);
        Assert.Equal("10.1.0.0/16", network.Key);
        Assert.Equal("A,B,C", Join(network.PresentIn));
        Assert.Equal(3, network.Presences.Count);
        Assert.Equal(1, plan.NetworkCount); // counted once, not three times

        var node = Assert.Single(Assert.Single(plan.Views).Roots);
        Assert.Equal("A,B,C", Join(node.PresentIn));
    }

    [Fact]
    public void Matching_is_by_prefix_alone_so_host_bits_do_not_make_a_different_network()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.0.0.5", 24),
            IpamFixtures.Grid("B").AddNet("10.0.0.0", 24));

        var network = Assert.Single(plan.Networks);
        Assert.Equal("10.0.0.0/24", network.Key);
        Assert.Equal("A,B", Join(network.PresentIn));
    }

    [Fact]
    public void A_network_is_a_container_if_any_source_says_so_and_is_counted_once()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.0.0.0", 8),
            IpamFixtures.Grid("B").AddNet("10.0.0.0", 8, container: true));

        Assert.True(Assert.Single(plan.Networks).IsContainer);
        Assert.Equal(1, plan.ContainerCount);
        Assert.Equal(0, plan.NetworkCount);
    }

    [Fact]
    public void A_value_the_sources_agree_on_is_shown_and_one_they_dont_varies()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.1.0.0", 24, comment: "Servers").AddNet("10.2.0.0", 24, comment: "Voice"),
            IpamFixtures.Grid("B").AddNet("10.1.0.0", 24, comment: "Servers").AddNet("10.2.0.0", 24, comment: "VoIP", disabled: true));

        var agreed = Node(plan, "10.1.0.0/24");
        Assert.False(agreed.Comment.Varies);
        Assert.Equal("Servers", agreed.Comment.Value);
        Assert.False(agreed.Disabled.Varies);
        Assert.False(agreed.Disabled.Value);

        var differing = Node(plan, "10.2.0.0/24");
        Assert.True(differing.Comment.Varies);
        Assert.True(differing.Disabled.Varies);

        // Instead of a bare "varies", each different value is listed, in the order of the sources.
        Assert.Equal("Voice,VoIP", Join(differing.Comments));
        Assert.Equal("Voice, VoIP", differing.Comment.ToString());
        Assert.Equal("B", Join(differing.DisabledIn));
        Assert.Equal("", Join(agreed.DisabledIn));

        // The per-source values are still there, for the row's details.
        Assert.Equal("Voice", differing.Presences.Single(p => p.Source.Name == "A").Comment);
        Assert.Equal("VoIP", differing.Presences.Single(p => p.Source.Name == "B").Comment);
        Assert.True(differing.Presences.Single(p => p.Source.Name == "B").Disabled);
    }

    [Fact]
    public void Each_presence_remembers_which_snapshot_it_came_from()
    {
        var inventory = IpamFixtures.Grid("A").AddNet("10.0.0.0", 24);

        var plan = IpamBuilder.Build([inventory.Input("2026-01-05")]);

        var source = Assert.Single(plan.Networks).Presences.Single().Source;
        Assert.Equal("snap-A", source.SnapshotId);
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero), source.AsOf);
        Assert.Equal(IpamSourceKind.Grid, source.Kind);
    }

    [Fact]
    public void A_prefix_that_is_not_valid_is_left_out_and_reported()
    {
        var plan = Plan(IpamFixtures.Grid("A").AddNet("not-an-address", 24).AddNet("10.0.0.0", 24));

        Assert.Equal("10.0.0.0/24", Assert.Single(plan.Networks).Key);
        Assert.Contains(plan.Problems, p => p.Contains("not-an-address") && p.StartsWith("A:"));
    }

    // ---- Network views ----

    [Fact]
    public void A_prefix_in_two_network_views_is_one_network_shown_under_each_view()
    {
        var plan = Plan(IpamFixtures.Grid("A", (1, "Guest")).AddNet("10.0.0.0", 24).AddNet("10.0.0.0", 24, view: 1));

        var network = Assert.Single(plan.Networks); // one object, counted once
        Assert.Equal("default,Guest", Join(network.Views));
        Assert.Equal(1, plan.NetworkCount);

        Assert.Equal("Guest,default", Join(plan.Views.Select(v => v.Name).Order(StringComparer.Ordinal)));
        foreach (var view in plan.Views)
        {
            var node = Assert.Single(view.Roots);
            Assert.Same(network, node.Network);
            Assert.Equal(view.Name, Assert.Single(node.Presences).ViewName); // only that view's presence
        }
    }

    [Fact]
    public void Views_with_the_same_name_on_different_grids_are_one_view_whatever_their_case_or_id()
    {
        var plan = Plan(
            IpamFixtures.Grid("A", (1, "Internal")).AddNet("10.0.0.0", 24, view: 1),
            IpamFixtures.Grid("B", (5, "internal")).AddNet("10.1.0.0", 24, view: 5));

        var internalView = plan.Views.Single(v => string.Equals(v.Name, "internal", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, internalView.Roots.Count);
        Assert.Single(plan.Views); // no networks in either grid's default view, so there is no other view
    }

    [Fact]
    public void A_view_with_ranges_but_no_networks_still_exists()
    {
        var plan = Plan(IpamFixtures.Grid("A", (2, "Lab")).AddRange("10.9.0.1", "10.9.0.9", view: 2));

        var view = View(plan, "Lab");
        Assert.Empty(view.Roots);
        Assert.Single(view.Unplaced);
    }

    // ---- Nesting ----

    [Fact]
    public void A_network_nests_under_a_container_from_another_grid()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.0.0.0", 8, container: true),
            IpamFixtures.Grid("B").AddNet("10.1.0.0", 24));

        var container = Node(plan, "10.0.0.0/8");
        var network = Node(plan, "10.1.0.0/24");
        Assert.Same(container, network.Parent);
        Assert.Same(network, Assert.Single(container.Children));
        Assert.Same(container, Assert.Single(View(plan, "default").Roots));
    }

    [Fact]
    public void Nesting_follows_containment_through_several_levels_to_the_tightest_container()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.0.0.0", 8, container: true).AddNet("10.1.2.0", 24),
            IpamFixtures.Grid("B").AddNet("10.1.0.0", 16, container: true).AddNet("192.168.0.0", 16));

        Assert.Equal("10.1.0.0/16", Node(plan, "10.1.2.0/24").Parent!.Key);
        Assert.Equal("10.0.0.0/8", Node(plan, "10.1.0.0/16").Parent!.Key);
        Assert.Null(Node(plan, "10.0.0.0/8").Parent);
        Assert.Null(Node(plan, "192.168.0.0/16").Parent); // in no container: top level
        Assert.Equal(2, View(plan, "default").Roots.Count);
    }

    [Fact]
    public void Only_containers_hold_other_networks()
    {
        var plan = Plan(IpamFixtures.Grid("A").AddNet("10.1.0.0", 24).AddNet("10.1.0.0", 25));

        Assert.Equal(2, View(plan, "default").Roots.Count);
        Assert.Empty(Node(plan, "10.1.0.0/24").Children);
    }

    [Fact]
    public void Containment_never_crosses_network_views()
    {
        var plan = Plan(IpamFixtures.Grid("A", (1, "Guest"))
            .AddNet("10.0.0.0", 8, container: true, view: 0)
            .AddNet("10.1.0.0", 24, view: 1));

        Assert.Null(Node(plan, "10.1.0.0/24", "Guest").Parent);
        Assert.Empty(Node(plan, "10.0.0.0/8").Children);
    }

    [Fact]
    public void IPv4_comes_before_IPv6_and_each_nests_within_its_own_family()
    {
        var plan = Plan(IpamFixtures.Grid("A")
            .AddNet("2001:db8::", 32, container: true)
            .AddNet("2001:db8:1::", 48)
            .AddNet("10.0.0.0", 8, container: true));

        Assert.Equal("10.0.0.0/8,2001:db8::/32,2001:db8:1::/48", Join(plan.Networks.Select(n => n.Key)));
        Assert.Equal("2001:db8::/32", Node(plan, "2001:db8:1::/48").Parent!.Key);
        Assert.True(Node(plan, "2001:db8::/32").IsIpv6);
        Assert.False(Node(plan, "10.0.0.0/8").IsIpv6);
        Assert.Equal("10.0.0.0/8,2001:db8::/32", Join(View(plan, "default").Roots.Select(n => n.Key)));
    }

    // ---- DHCP ranges ----

    [Fact]
    public void A_range_goes_under_the_most_specific_network_that_contains_it()
    {
        var plan = Plan(IpamFixtures.Grid("A")
            .AddNet("10.0.0.0", 8, container: true)
            .AddNet("10.1.0.0", 24)
            .AddRange("10.1.0.50", "10.1.0.99")
            .AddRange("10.2.0.1", "10.2.0.9")
            .AddRange("192.168.5.1", "192.168.5.9"));

        Assert.Equal("10.1.0.50", Assert.Single(Node(plan, "10.1.0.0/24").Ranges).Start);
        Assert.Equal("10.2.0.1", Assert.Single(Node(plan, "10.0.0.0/8").Ranges).Start); // no tighter network
        Assert.Equal("192.168.5.1", Assert.Single(View(plan, "default").Unplaced).Start); // "(no network)"
    }

    [Fact]
    public void A_range_sits_under_a_network_that_only_another_grid_has()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.1.0.0", 24),
            IpamFixtures.Grid("B").AddRange("10.1.0.5", "10.1.0.9"));

        var node = Node(plan, "10.1.0.0/24");
        Assert.Equal("A", Join(node.PresentIn));
        Assert.Equal("B", Assert.Single(node.Ranges).Source.Name);
    }

    [Fact]
    public void A_range_is_placed_within_its_own_network_view_only()
    {
        var plan = Plan(IpamFixtures.Grid("A", (1, "Guest"))
            .AddNet("10.1.0.0", 24, view: 0)
            .AddRange("10.1.0.5", "10.1.0.9", view: 1));

        Assert.Empty(Node(plan, "10.1.0.0/24").Ranges);
        Assert.Single(View(plan, "Guest").Unplaced);
    }

    [Fact]
    public void A_range_present_on_two_grids_is_listed_twice_each_with_its_own_source()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.1.0.0", 24).AddRange("10.1.0.100", "10.1.0.199"),
            IpamFixtures.Grid("B").AddNet("10.1.0.0", 24).AddRange("10.1.0.100", "10.1.0.199"));

        var ranges = Node(plan, "10.1.0.0/24").Ranges;
        Assert.Equal("A,B", Join(ranges.Select(r => r.Source.Name)));
        Assert.Equal(2, plan.RangeCount);
    }

    [Fact]
    public void An_IPv6_range_is_placed_in_an_IPv6_network()
    {
        var plan = Plan(IpamFixtures.Grid("A")
            .AddNet("10.0.0.0", 8, container: true)
            .AddNet("2001:db8::", 64)
            .AddRange("2001:db8::1000", "2001:db8::1fff"));

        Assert.Single(Node(plan, "2001:db8::/64").Ranges);
        Assert.Empty(Node(plan, "10.0.0.0/8").Ranges);
    }

    [Fact]
    public void A_range_says_what_serves_it_and_keeps_its_details()
    {
        var inventory = IpamFixtures.Grid("A").AddNet("10.1.0.0", 24)
            .AddRange("10.1.0.10", "10.1.0.19", association: "Member", member: 7, name: "Staff", comment: "wifi", disabled: true)
            .AddRange("10.1.0.20", "10.1.0.29", association: "Failover", failover: "gm-gmc");
        inventory.Members.Add(new GridMember { Oid = 7, HostName = "ns1.lab.test" });

        var plan = Plan(inventory);

        var ranges = Node(plan, "10.1.0.0/24").Ranges;
        var member = ranges[0];
        Assert.Equal("ns1.lab.test", member.ServedBy);
        Assert.Equal("Member", member.AssociationType);
        Assert.Equal("Staff", member.Name);
        Assert.Equal("wifi", member.Comment);
        Assert.True(member.Disabled);
        Assert.Equal("gm-gmc", ranges[1].ServedBy);
    }

    [Fact]
    public void Ranges_are_in_address_order_not_text_order()
    {
        var plan = Plan(IpamFixtures.Grid("A").AddNet("10.0.0.0", 8, container: true)
            .AddRange("10.0.0.100", "10.0.0.110").AddRange("10.0.0.9", "10.0.0.20"));

        Assert.Equal("10.0.0.9,10.0.0.100", Join(Node(plan, "10.0.0.0/8").Ranges.Select(r => r.Start)));
    }

    // ---- Counts ----

    [Fact]
    public void Counts_roll_up_to_every_level_above()
    {
        var plan = Plan(IpamFixtures.Grid("A")
            .AddNet("10.0.0.0", 8, container: true)
            .AddNet("10.1.0.0", 24)
            .AddNet("10.2.0.0", 24)
            .AddRange("10.1.0.10", "10.1.0.20")
            .AddRange("10.1.0.30", "10.1.0.40")
            .AddRange("10.2.0.10", "10.2.0.20")
            .AddRange("10.200.0.1", "10.200.0.5")     // in the container, in no network
            .AddRange("192.168.1.1", "192.168.1.9")); // in nothing

        var container = Node(plan, "10.0.0.0/8");
        Assert.Equal(2, Node(plan, "10.1.0.0/24").RangeCount);
        Assert.Equal(1, Node(plan, "10.2.0.0/24").RangeCount);
        Assert.Equal(4, container.RangeCount);
        Assert.Equal(2, container.NetworkCount);
        Assert.Equal(1, Node(plan, "10.1.0.0/24").NetworkCount);

        var view = View(plan, "default");
        Assert.Equal(2, view.NetworkCount);
        Assert.Equal(5, view.RangeCount);
        Assert.Equal(5, plan.RangeCount);
    }

    [Fact]
    public void No_grids_gives_an_empty_plan()
    {
        var plan = IpamBuilder.Build([]);

        Assert.Empty(plan.Sources);
        Assert.Empty(plan.Networks);
        Assert.Empty(plan.Views);
    }

    [Fact]
    public void All_nodes_lists_parents_before_their_children()
    {
        var plan = Plan(IpamFixtures.Grid("A")
            .AddNet("10.0.0.0", 8, container: true).AddNet("10.1.0.0", 24).AddNet("10.2.0.0", 24).AddNet("172.16.0.0", 24));

        Assert.Equal("10.0.0.0/8,10.1.0.0/24,10.2.0.0/24,172.16.0.0/24",
            Join(View(plan, "default").AllNodes().Select(n => n.Key)));
    }
}
