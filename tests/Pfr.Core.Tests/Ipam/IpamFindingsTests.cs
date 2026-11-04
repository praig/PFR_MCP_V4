using System.Text.Json;
using Pfr.Core.Ipam;
using Pfr.Core.Nios.Model;

namespace Pfr.Core.Tests.Ipam;

public class IpamFindingsTests
{
    private static IpamPlan Plan(params GridInventory[] grids) => IpamBuilder.Build(grids.Select(g => g.Input()));

    private static List<IpamFinding> Of(IpamPlan plan, IpamFindingKind kind) =>
        IpamFindings.Analyze(plan).Where(f => f.Kind == kind).ToList();

    // ---- Where each network is, and what the grids say about it ----

    [Fact]
    public void A_network_on_some_of_the_grids_but_not_all_is_a_presence_gap()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.1.0.0", 24),
            IpamFixtures.Grid("B").AddNet("10.1.0.0", 24),
            IpamFixtures.Grid("C").AddNet("10.9.0.0", 24));

        var finding = Assert.Single(Of(plan, IpamFindingKind.PresenceGap));

        Assert.Equal("10.1.0.0/24", finding.Subject);
        Assert.Equal("10.1.0.0/24 is on A, B but not on C.", finding.Summary);
        Assert.Equal(IpamSeverity.Info, finding.Severity);
        Assert.Equal(["A", "B", "C"], finding.Sources);
    }

    [Fact]
    public void A_network_on_every_grid_or_on_only_one_is_not_a_gap()
    {
        var everywhere = Plan(IpamFixtures.Grid("A").AddNet("10.1.0.0", 24), IpamFixtures.Grid("B").AddNet("10.1.0.0", 24));
        Assert.Empty(Of(everywhere, IpamFindingKind.PresenceGap));

        // Each grid has its own networks: that is how most clients are laid out, so it is not reported.
        var separate = Plan(IpamFixtures.Grid("A").AddNet("10.1.0.0", 24), IpamFixtures.Grid("B").AddNet("10.2.0.0", 24));
        Assert.Empty(Of(separate, IpamFindingKind.PresenceGap));

        // With one grid there is nothing to compare.
        Assert.Empty(Of(Plan(IpamFixtures.Grid("A").AddNet("10.1.0.0", 24)), IpamFindingKind.PresenceGap));
    }

    [Fact]
    public void The_same_prefix_in_two_network_views_is_a_warning()
    {
        var plan = Plan(IpamFixtures.Grid("A", (5, "Corp")).AddNet("10.1.0.0", 24).AddNet("10.1.0.0", 24, view: 5).AddNet("10.2.0.0", 24));

        var finding = Assert.Single(Of(plan, IpamFindingKind.MultipleViews));

        Assert.Equal("10.1.0.0/24", finding.Subject);
        Assert.Equal(IpamSeverity.Warning, finding.Severity);
        Assert.Contains("2 network views (Corp, default)", finding.Summary);
    }

    [Fact]
    public void Grids_that_disagree_whether_a_prefix_is_a_container_are_reported()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.0.0.0", 8, container: true),
            IpamFixtures.Grid("B").AddNet("10.0.0.0", 8));

        var finding = Assert.Single(Of(plan, IpamFindingKind.KindDisagrees));

        Assert.Equal("10.0.0.0/8 is a container on A and a network on B.", finding.Summary);
        Assert.Equal(IpamSeverity.Warning, finding.Severity);
    }

    [Fact]
    public void A_network_disabled_on_one_grid_and_not_the_other_is_reported()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.1.0.0", 24, disabled: true),
            IpamFixtures.Grid("B").AddNet("10.1.0.0", 24));

        var finding = Assert.Single(Of(plan, IpamFindingKind.DisabledDisagrees));

        Assert.Equal("10.1.0.0/24 is disabled on A but enabled on B.", finding.Summary);
        Assert.Equal(IpamSeverity.Info, finding.Severity);

        // Disabled everywhere is a choice, not a disagreement.
        var agreed = Plan(IpamFixtures.Grid("A").AddNet("10.1.0.0", 24, disabled: true), IpamFixtures.Grid("B").AddNet("10.1.0.0", 24, disabled: true));
        Assert.Empty(Of(agreed, IpamFindingKind.DisabledDisagrees));
    }

    // ---- Networks inside networks ----

    [Fact]
    public void A_network_inside_another_network_is_reported_even_under_a_container()
    {
        var plan = Plan(IpamFixtures.Grid("A")
            .AddNet("10.0.0.0", 8, container: true).AddNet("10.1.0.0", 16).AddNet("10.1.2.0", 24).AddNet("10.2.0.0", 16));

        var finding = Assert.Single(Of(plan, IpamFindingKind.OverlappingNetworks));

        Assert.Equal("10.1.2.0/24", finding.Subject);
        Assert.Contains("lies inside the network 10.1.0.0/16", finding.Summary);
        Assert.Equal(["10.1.0.0/16 (network on A)", "10.1.2.0/24 (network on A)"], finding.Items);
        Assert.Equal(IpamSeverity.Warning, finding.Severity);
    }

    [Fact]
    public void A_container_inside_a_network_is_reported_but_containers_in_containers_are_not()
    {
        var wrong = Plan(IpamFixtures.Grid("A").AddNet("10.1.0.0", 16).AddNet("10.1.2.0", 24, container: true));
        Assert.Equal("10.1.2.0/24 (container on A)", Assert.Single(Of(wrong, IpamFindingKind.OverlappingNetworks)).Items[1]);

        var fine = Plan(IpamFixtures.Grid("A").AddNet("10.0.0.0", 8, container: true).AddNet("10.1.0.0", 16, container: true).AddNet("10.1.2.0", 24));
        Assert.Empty(Of(fine, IpamFindingKind.OverlappingNetworks));
    }

    [Fact]
    public void Networks_on_different_grids_that_overlap_are_reported_with_both_grids()
    {
        var plan = Plan(IpamFixtures.Grid("A").AddNet("10.1.0.0", 16), IpamFixtures.Grid("B").AddNet("10.1.2.0", 24));

        var finding = Assert.Single(Of(plan, IpamFindingKind.OverlappingNetworks));

        Assert.Equal(["A", "B"], finding.Sources);
    }

    [Fact]
    public void Overlap_is_judged_within_a_network_view_and_never_across_address_families()
    {
        var views = Plan(IpamFixtures.Grid("A", (5, "Corp")).AddNet("10.1.0.0", 16).AddNet("10.1.2.0", 24, view: 5));
        Assert.Empty(Of(views, IpamFindingKind.OverlappingNetworks));

        var families = Plan(IpamFixtures.Grid("A").AddNet("10.1.0.0", 16).AddNet("2001:db8::", 32).AddNet("2001:db8:1::", 48));
        var finding = Assert.Single(Of(families, IpamFindingKind.OverlappingNetworks));
        Assert.Equal("2001:db8:1::/48", finding.Subject);
    }

    // ---- Objects that are in no network, or in one their grid does not have ----

    [Fact]
    public void Ranges_hosts_and_records_in_no_network_are_listed_per_grid()
    {
        var plan = Plan(IpamFixtures.Grid("A").WithDns()
            .AddNet("10.1.0.0", 24)
            .AddRange("10.8.0.10", "10.8.0.20")
            .AddHost("stray.lab.test", ["10.9.9.9"], dns: false)
            .AddRecord(DnsRecordType.A, "x.lab.test", "10.7.0.1"));

        var finding = Assert.Single(Of(plan, IpamFindingKind.OutsideAnyNetwork));

        Assert.Equal("A", finding.Subject);
        Assert.Contains("3 objects on A are in no network", finding.Summary);
        Assert.Equal(["DHCP range 10.8.0.10 - 10.8.0.20", "host stray.lab.test 10.9.9.9", "A x.lab.test 10.7.0.1"], finding.Items);
        Assert.Equal(IpamSeverity.Warning, finding.Severity);
    }

    [Fact]
    public void An_object_on_a_grid_that_does_not_have_its_network_is_reported_against_that_network()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.1.0.0", 24),
            IpamFixtures.Grid("B").AddNet("10.9.0.0", 24).AddHost("db1.lab.test", ["10.1.0.5"], dns: false));

        var finding = Assert.Single(Of(plan, IpamFindingKind.NetworkNotOnGrid));

        Assert.Equal("10.1.0.0/24", finding.Subject);
        Assert.Equal("1 object on B sits in 10.1.0.0/24, which B does not have. The network is on A.", finding.Summary);
        Assert.Equal(["host db1.lab.test 10.1.0.5"], finding.Items);
        Assert.Equal(["A", "B"], finding.Sources);
    }

    [Fact]
    public void Objects_on_a_grid_that_has_the_network_are_not_reported()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.1.0.0", 24).AddHost("web1.lab.test", ["10.1.0.5"], dns: false),
            IpamFixtures.Grid("B").AddNet("10.1.0.0", 24));

        Assert.Empty(Of(plan, IpamFindingKind.NetworkNotOnGrid));
        Assert.Empty(Of(plan, IpamFindingKind.OutsideAnyNetwork));
    }

    // ---- The same address on hosts of different grids ----

    [Fact]
    public void The_same_address_on_hosts_of_two_grids_in_one_view_is_a_clash()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.1.0.0", 24).AddHost("web1.lab.test", ["10.1.0.5"], dns: false),
            IpamFixtures.Grid("B").AddNet("10.1.0.0", 24).AddHost("db1.lab.test", ["10.1.0.5"], dns: false));

        var finding = Assert.Single(Of(plan, IpamFindingKind.DuplicateHostAddress));

        Assert.Equal("10.1.0.5", finding.Subject);
        Assert.Equal("10.1.0.5 is held by hosts on 2 grids (A, B).", finding.Summary);
        Assert.Equal(["web1.lab.test on A", "db1.lab.test on B"], finding.Items);
        Assert.Equal(IpamSeverity.Warning, finding.Severity);
    }

    [Fact]
    public void The_same_address_in_different_network_views_is_only_information()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.1.0.0", 24).AddHost("web1.lab.test", ["10.1.0.5"], dns: false),
            IpamFixtures.Grid("B", (5, "Corp")).AddNet("10.1.0.0", 24, view: 5).AddHost("db1.lab.test", ["10.1.0.5"], dns: false, networkView: 5));

        var finding = Assert.Single(Of(plan, IpamFindingKind.DuplicateHostAddress));

        Assert.Equal(IpamSeverity.Info, finding.Severity);
        Assert.EndsWith("so this may be separate address space.", finding.Summary);
        Assert.Equal(["web1.lab.test on A (network view default)", "db1.lab.test on B (network view Corp)"], finding.Items);
    }

    [Fact]
    public void Different_addresses_or_one_grid_are_not_clashes()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").AddNet("10.1.0.0", 24).AddHost("web1.lab.test", ["10.1.0.5"], dns: false),
            IpamFixtures.Grid("B").AddNet("10.1.0.0", 24).AddHost("db1.lab.test", ["10.1.0.6"], dns: false));

        Assert.Empty(Of(plan, IpamFindingKind.DuplicateHostAddress));
    }

    // ---- A records and PTR records ----

    [Fact]
    public void An_a_record_with_no_ptr_is_reported_but_a_hosts_own_records_and_disabled_ones_are_not()
    {
        var plan = Plan(IpamFixtures.Grid("A").WithDns().AddNet("10.0.0.0", 24)
            .AddHost("web1.lab.test", ["10.0.0.20"])                      // generates an A and a PTR
            .AddRecord(DnsRecordType.A, "app.lab.test", "10.0.0.50")      // no PTR for 10.0.0.50
            .AddRecord(DnsRecordType.A, "old.lab.test", "10.0.0.77", disabled: true));

        var finding = Assert.Single(Of(plan, IpamFindingKind.ARecordWithoutPtr));

        Assert.Equal("A", finding.Subject);
        Assert.Equal("1 A / AAAA record on A has no PTR record for the address, on any grid.", finding.Summary);
        Assert.Equal(["app.lab.test 10.0.0.50"], finding.Items);
        Assert.Equal(IpamSeverity.Info, finding.Severity);
    }

    [Fact]
    public void A_ptr_record_on_another_grid_counts()
    {
        var plan = Plan(
            IpamFixtures.Grid("A").WithDns().AddNet("10.0.0.0", 24).AddRecord(DnsRecordType.A, "app.lab.test", "10.0.0.50"),
            IpamFixtures.Grid("B").WithDns().AddRecord(DnsRecordType.PTR, IpamFixtures.ReverseName("10.0.0.50"), "app.lab.test"));

        Assert.Empty(Of(plan, IpamFindingKind.ARecordWithoutPtr));
        Assert.Empty(Of(plan, IpamFindingKind.PtrNamesOtherHost));
    }

    [Fact]
    public void A_ptr_that_names_a_different_host_is_reported_and_one_that_differs_only_in_case_or_dot_is_not()
    {
        var plan = Plan(IpamFixtures.Grid("A").WithDns().AddNet("10.0.0.0", 24)
            .AddRecord(DnsRecordType.A, "www.lab.test", "10.0.0.60")
            .AddRecord(DnsRecordType.PTR, IpamFixtures.ReverseName("10.0.0.60"), "web1.lab.test")
            .AddRecord(DnsRecordType.A, "WWW2.lab.test", "10.0.0.61")
            .AddRecord(DnsRecordType.PTR, IpamFixtures.ReverseName("10.0.0.61"), "www2.lab.test."));

        var finding = Assert.Single(Of(plan, IpamFindingKind.PtrNamesOtherHost));

        Assert.Equal(["www.lab.test 10.0.0.60 (PTR says web1.lab.test)"], finding.Items);
        Assert.Empty(Of(plan, IpamFindingKind.ARecordWithoutPtr));
    }

    [Fact]
    public void Ptr_records_in_another_network_view_do_not_count()
    {
        var a = IpamFixtures.Grid("A", (5, "Corp")).WithDns().AddNet("10.0.0.0", 24).AddRecord(DnsRecordType.A, "app.lab.test", "10.0.0.50");
        a.DnsViews.Add(new DnsView { Key = "corp", Name = "Corp", NetworkViewId = 5 });
        a.Records.Add(new DnsRecord
        {
            Type = DnsRecordType.PTR, ZoneKey = "corp.arpa", ViewKey = "corp", Name = "50", Fqdn = IpamFixtures.ReverseName("10.0.0.50"),
            Value = "app.lab.test", Ttl = new Ttl(300, TtlSource.Record),
        });

        Assert.Single(Of(Plan(a), IpamFindingKind.ARecordWithoutPtr));
    }

    // ---- DHCP ----

    [Fact]
    public void Ipv4_networks_with_no_range_are_listed_but_not_containers_disabled_networks_or_ipv6()
    {
        var plan = Plan(IpamFixtures.Grid("A")
            .AddNet("10.0.0.0", 8, container: true)
            .AddNet("10.1.0.0", 24).AddRange("10.1.0.10", "10.1.0.20")
            .AddNet("10.2.0.0", 24)
            .AddNet("10.3.0.0", 24, disabled: true)
            .AddNet("2001:db8::", 64));

        var finding = Assert.Single(Of(plan, IpamFindingKind.NetworkWithoutDhcpRange));

        Assert.Equal("default", finding.Subject);
        Assert.Contains("1 IPv4 network has no DHCP range", finding.Summary);
        Assert.Equal(["10.2.0.0/24"], finding.Items);
        Assert.Equal(IpamSeverity.Info, finding.Severity);
    }

    // ---- The whole analysis ----

    [Fact]
    public void A_tidy_plan_has_no_findings()
    {
        var plan = Plan(IpamFixtures.Grid("A").WithDns().AddNet("10.0.0.0", 24)
            .AddRange("10.0.0.100", "10.0.0.200").AddHost("web1.lab.test", ["10.0.0.20"]));

        Assert.Empty(IpamFindings.Analyze(plan));
    }

    private static IpamPlan Messy() => Plan(
        IpamFixtures.Grid("A", (5, "Corp")).AddNet("10.0.0.0", 8, container: true).AddNet("10.1.0.0", 24).AddNet("10.1.0.0", 24, view: 5)
            .AddNet("10.2.0.0", 24, disabled: true).AddNet("10.9.0.0", 24).AddHost("web1.lab.test", ["10.2.0.5"], dns: false),
        IpamFixtures.Grid("B").AddNet("10.0.0.0", 8).AddNet("10.2.0.0", 24).AddHost("db1.lab.test", ["10.2.0.5"], dns: false),
        IpamFixtures.Grid("C").AddNet("10.9.0.0", 24));

    [Fact]
    public void Warnings_come_first_and_each_severity_follows_the_order_of_the_kinds()
    {
        var findings = IpamFindings.Analyze(Messy());

        Assert.NotEmpty(findings);
        Assert.Contains(findings, f => f.Severity == IpamSeverity.Warning);
        Assert.Contains(findings, f => f.Severity == IpamSeverity.Info);

        var order = IpamFindings.Kinds.Select((k, i) => (k.Kind, i)).ToDictionary(x => x.Kind, x => x.i);
        foreach (var (first, second) in findings.Zip(findings.Skip(1)))
        {
            Assert.True(first.Severity >= second.Severity, "a warning came after information");
            if (first.Severity == second.Severity) Assert.True(order[first.Kind] <= order[second.Kind], "kinds out of order");
        }
    }

    [Fact]
    public void Findings_can_be_narrowed_by_kind_severity_and_grid()
    {
        var all = IpamFindings.Analyze(Messy());

        var views = IpamFindings.Filter(all, new HashSet<IpamFindingKind> { IpamFindingKind.MultipleViews });
        Assert.All(views, f => Assert.Equal(IpamFindingKind.MultipleViews, f.Kind));
        Assert.NotEmpty(views);

        var warnings = IpamFindings.Filter(all, atLeast: IpamSeverity.Warning);
        Assert.All(warnings, f => Assert.Equal(IpamSeverity.Warning, f.Severity));
        Assert.True(warnings.Count < all.Count);

        var forC = IpamFindings.Filter(all, grid: "c");
        Assert.NotEmpty(forC);
        Assert.All(forC, f => Assert.Contains("C", f.Sources));
        Assert.True(forC.Count < all.Count);

        Assert.Equal(all.Count, IpamFindings.Filter(all).Count);
    }

    [Fact]
    public void Every_kind_has_a_unique_name_and_title_and_can_be_read_back()
    {
        foreach (var kind in Enum.GetValues<IpamFindingKind>())
        {
            var entry = Assert.Single(IpamFindings.Kinds, k => k.Kind == kind);
            Assert.False(string.IsNullOrWhiteSpace(entry.Title));
            Assert.True(IpamFindings.TryParseKind(entry.Id.ToUpperInvariant(), out var parsed));
            Assert.Equal(kind, parsed);
        }

        Assert.Equal(IpamFindings.Kinds.Count, IpamFindings.Kinds.Select(k => k.Id).Distinct().Count());
        Assert.False(IpamFindings.TryParseKind("no-such-thing", out _));
        Assert.False(IpamFindings.TryParseKind(null, out _));
    }

    [Fact]
    public void A_finding_counts_its_items_or_counts_as_one()
    {
        var one = new IpamFinding(IpamFindingKind.PresenceGap, IpamSeverity.Info, "10.1.0.0/24", "s", ["A"], []);
        var many = new IpamFinding(IpamFindingKind.OutsideAnyNetwork, IpamSeverity.Warning, "A", "s", ["A"], ["x", "y", "z"]);

        Assert.Equal(1, one.Count);
        Assert.Equal(3, many.Count);
        Assert.Equal("presence-gap", one.Id);
        Assert.Equal("Objects in no network", many.Title);
    }

    // ---- The reports ----

    private static (IpamPlan Plan, List<IpamFinding> Findings) Sample() => (
        Plan(IpamFixtures.Grid("A").AddNet("10.1.0.0", 24), IpamFixtures.Grid("B").AddNet("10.2.0.0", 24)),
        [
            new IpamFinding(IpamFindingKind.MultipleViews, IpamSeverity.Warning, "10.1.0.0/24", "It is in 2 views.", ["A"], []),
            new IpamFinding(IpamFindingKind.OutsideAnyNetwork, IpamSeverity.Warning, "A", "5 objects are in no network.", ["A"], ["i1", "i2", "i3", "i4", "i5"]),
            new IpamFinding(IpamFindingKind.NetworkWithoutDhcpRange, IpamSeverity.Info, "default", "1 network has no range.", ["A"], ["10.2.0.0/24"]),
        ]);

    [Fact]
    public void The_text_report_groups_findings_by_severity_and_shortens_long_lists()
    {
        var (plan, findings) = Sample();

        var text = IpamFindingsReport.Text(plan, findings, "Acme", ["A has no snapshot."], itemsPerFinding: 2).Replace("\r\n", "\n");

        Assert.StartsWith("Address plan findings: Acme\n2 grids (A, B), 2 networks and containers.\n", text);
        Assert.Contains("3 findings: 2 warnings, 1 for information.", text);
        Assert.True(text.IndexOf("WARNINGS", StringComparison.Ordinal) < text.IndexOf("FOR INFORMATION", StringComparison.Ordinal));
        Assert.Contains("[Same prefix in several network views] 10.1.0.0/24\n  It is in 2 views.", text);
        Assert.Contains("    - i1\n    - i2\n    ... and 3 more", text);
        Assert.DoesNotContain("i3", text);
        Assert.EndsWith("Notes\n  A has no snapshot.\n", text);
    }

    [Fact]
    public void Listing_no_items_leaves_out_the_item_lines()
    {
        var (plan, findings) = Sample();

        var text = IpamFindingsReport.Text(plan, findings, "Acme", [], itemsPerFinding: 0);

        Assert.DoesNotContain("    - ", text);
        Assert.DoesNotContain("... and", text);
    }

    [Fact]
    public void With_no_findings_the_report_says_so_and_explains_a_single_grid()
    {
        var one = Plan(IpamFixtures.Grid("A").AddNet("10.1.0.0", 24));
        var text = IpamFindingsReport.Text(one, [], "Acme", []);
        Assert.Contains("No findings.", text);
        Assert.Contains("needs at least two of the client's grids", text);

        var two = Plan(IpamFixtures.Grid("A").AddNet("10.1.0.0", 24), IpamFixtures.Grid("B").AddNet("10.2.0.0", 24));
        var textTwo = IpamFindingsReport.Text(two, [], "Acme", []);
        Assert.Contains("No findings.", textTwo);
        Assert.DoesNotContain("at least two", textTwo);
    }

    [Fact]
    public void The_json_carries_the_same_findings_and_the_counts()
    {
        var (plan, findings) = Sample();

        using var document = JsonDocument.Parse(IpamFindingsReport.Json(plan, findings, "Acme", ["a note"]));
        var root = document.RootElement;

        Assert.Equal("Acme", root.GetProperty("client").GetString());
        Assert.Equal(2, root.GetProperty("sources").GetArrayLength());
        Assert.Equal((3, 2, 1), (root.GetProperty("counts").GetProperty("findings").GetInt32(),
            root.GetProperty("counts").GetProperty("warnings").GetInt32(), root.GetProperty("counts").GetProperty("information").GetInt32()));

        var first = root.GetProperty("findings")[0];
        Assert.Equal("multiple-views", first.GetProperty("kind").GetString());
        Assert.Equal("warning", first.GetProperty("severity").GetString());
        Assert.Equal("10.1.0.0/24", first.GetProperty("subject").GetString());
        Assert.Equal(1, first.GetProperty("count").GetInt32());

        var second = root.GetProperty("findings")[1];
        Assert.Equal(5, second.GetProperty("count").GetInt32());
        Assert.Equal(5, second.GetProperty("items").GetArrayLength());
        Assert.Equal("info", root.GetProperty("findings")[2].GetProperty("severity").GetString());
        Assert.Equal("a note", root.GetProperty("notes")[0].GetString());
    }

    [Fact]
    public void A_large_plan_is_analysed_quickly()
    {
        var a = IpamFixtures.Grid("A").WithDns().AddNet("10.0.0.0", 8, container: true);
        var b = IpamFixtures.Grid("B").WithDns().AddNet("10.0.0.0", 8, container: true);
        for (var i = 0; i < 2000; i++)
        {
            a.AddNet($"10.{i / 250}.{i % 250}.0", 24);
            if (i % 2 == 0) b.AddNet($"10.{i / 250}.{i % 250}.0", 24);
            a.AddRecord(DnsRecordType.A, $"h{i}.lab.test", $"10.{i / 250}.{i % 250}.5");
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var findings = IpamFindings.Analyze(Plan(a, b));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.Contains(findings, f => f.Kind == IpamFindingKind.ARecordWithoutPtr && f.Count == 2000);
    }
}
