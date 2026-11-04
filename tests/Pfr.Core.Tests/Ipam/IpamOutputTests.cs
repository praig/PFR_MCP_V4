using System.Text.Json;
using Pfr.Core.Clients;
using Pfr.Core.Ipam;
using Pfr.Core.Storage;
using Pfr.Core.Tests.Clients;

namespace Pfr.Core.Tests.Ipam;

public class IpamOutputTests
{
    /// <summary>Lab has a container, two networks and a failover range; DR has two of the same networks.</summary>
    private static IpamPlan SamplePlan()
    {
        var lab = IpamFixtures.Grid("Lab")
            .AddNet("10.0.0.0", 8, container: true, comment: "Corporate")
            .AddNet("10.1.0.0", 24, comment: "Servers")
            .AddNet("10.5.0.0", 24)
            .AddRange("10.1.0.100", "10.1.0.199", association: "Failover", failover: "gm-gmc", name: "Staff");
        var dr = IpamFixtures.Grid("DR")
            .AddNet("10.1.0.0", 24, comment: "Servers")
            .AddNet("10.2.0.0", 24);
        return IpamBuilder.Build([lab.Input("2026-02-10"), dr.Input("2026-01-30")]);
    }

    private static string[] Lines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    // ---- Text ----

    [Fact]
    public void The_text_report_shows_the_grids_the_totals_and_the_tree()
    {
        var text = IpamTextReport.Render(SamplePlan(), "Acme");

        Assert.Contains("Address plan for Acme", text);
        Assert.Contains("DR (snapshot of 2026-01-30), Lab (snapshot of 2026-02-10)", text);
        Assert.Contains("3 networks, 1 container, 1 DHCP range, 0 hosts, 0 DNS records in 1 network view", text);
        Assert.Contains("Network view: default", text);
    }

    [Fact]
    public void Networks_are_indented_by_containment_and_say_where_they_are_present()
    {
        var lines = Lines(IpamTextReport.Render(SamplePlan(), "Acme"));

        var container = lines.Single(l => l.Contains("10.0.0.0/8"));
        var shared = lines.Single(l => l.Contains("10.1.0.0/24"));
        var drOnly = lines.Single(l => l.Contains("10.2.0.0/24"));

        Assert.StartsWith("  10.0.0.0/8", container);
        Assert.Contains("container", container);
        Assert.Contains("\"Corporate\"", container);

        Assert.StartsWith("    10.1.0.0/24", shared); // one level deeper, below the container
        Assert.Contains("network", shared);
        Assert.Contains("DR, Lab", shared);             // present in both
        Assert.Contains("\"Servers\"", shared);         // both agree on the comment
        Assert.Contains("1 range", shared);

        Assert.StartsWith("    10.2.0.0/24", drOnly);
        Assert.DoesNotContain("Lab", drOnly);
    }

    [Fact]
    public void Ranges_are_listed_under_their_network_with_what_serves_them()
    {
        var text = IpamTextReport.Render(SamplePlan(), "Acme");

        Assert.Contains("range 10.1.0.100 - 10.1.0.199  Lab  Failover gm-gmc  \"Staff\"", text);
    }

    [Fact]
    public void Ranges_can_be_left_out()
    {
        var text = IpamTextReport.Render(SamplePlan(), "Acme", show: IpamLeafKinds.None);

        Assert.DoesNotContain("range 10.1.0.100", text);
        Assert.DoesNotContain("1 range", text); // no per-network range counts either
        Assert.Contains("10.1.0.0/24", text);
    }

    [Fact]
    public void A_value_the_sources_disagree_on_lists_each_different_value()
    {
        var plan = IpamBuilder.Build(
        [
            IpamFixtures.Grid("A").AddNet("10.0.0.0", 24, comment: "One").Input(),
            IpamFixtures.Grid("B").AddNet("10.0.0.0", 24, comment: "Two", disabled: true).Input(),
        ]);

        var text = IpamTextReport.Render(plan, "Acme");

        Assert.Contains("\"One\", \"Two\"", text);   // a comma separated list, not the word "varies"
        Assert.Contains("disabled in B", text);       // for a flag, the sources that have it set
        Assert.DoesNotContain("varies", text);
    }

    [Fact]
    public void A_comment_one_source_leaves_empty_is_not_listed_as_a_blank()
    {
        var plan = IpamBuilder.Build(
        [
            IpamFixtures.Grid("A").AddNet("10.0.0.0", 24, comment: "Servers").Input(),
            IpamFixtures.Grid("B").AddNet("10.0.0.0", 24).Input(),
        ]);

        var text = IpamTextReport.Render(plan, "Acme");

        Assert.Contains("\"Servers\"", text);
        Assert.DoesNotContain("\"\"", text);
    }

    [Fact]
    public void Ranges_in_no_network_are_shown_under_no_network()
    {
        var plan = IpamBuilder.Build([IpamFixtures.Grid("A").AddNet("10.0.0.0", 24).AddRange("192.168.1.1", "192.168.1.9").Input()]);

        var text = IpamTextReport.Render(plan, "Acme");

        Assert.Contains("(no network): 1 DHCP range outside every network of this view", text);
        Assert.Contains("range 192.168.1.1 - 192.168.1.9", text);
    }

    [Fact]
    public void Notes_and_problems_are_printed()
    {
        var plan = IpamBuilder.Build([IpamFixtures.Grid("A").AddNet("bad", 24).Input()]);

        var text = IpamTextReport.Render(plan, "Acme", ["Empty has no snapshot yet."]);

        Assert.Contains("Note: Empty has no snapshot yet.", text);
        Assert.Contains("Note: A: \"bad/24\" is not a valid prefix", text);
    }

    // ---- Filtering by grid ----

    [Fact]
    public void Filtering_by_grid_keeps_its_networks_and_the_containers_above_them()
    {
        var text = IpamTextReport.Render(SamplePlan(), "Acme", grid: "DR");

        Assert.Contains("Showing only what is present in DR.", text);
        Assert.Contains("10.0.0.0/8", text);   // not in DR, but it holds DR's networks
        Assert.Contains("10.1.0.0/24", text);
        Assert.Contains("10.2.0.0/24", text);
        Assert.DoesNotContain("10.5.0.0/24", text); // Lab only
        Assert.DoesNotContain("range 10.1.0.100", text); // Lab's range
    }

    [Fact]
    public void Filtering_by_grid_keeps_a_network_that_has_one_of_its_ranges()
    {
        // 10.1.0.0/24 is on both grids; the range is Lab's. Filtering to Lab shows the range, DR does not.
        Assert.Contains("range 10.1.0.100", IpamTextReport.Render(SamplePlan(), "Acme", grid: "Lab"));
        Assert.DoesNotContain("10.5.0.0/24", IpamTextReport.Render(SamplePlan(), "Acme", grid: "DR"));
        Assert.Contains("10.5.0.0/24", IpamTextReport.Render(SamplePlan(), "Acme", grid: "Lab"));
        Assert.DoesNotContain("10.2.0.0/24", IpamTextReport.Render(SamplePlan(), "Acme", grid: "Lab"));
    }

    [Fact]
    public void A_view_with_nothing_for_the_grid_is_left_out()
    {
        var plan = IpamBuilder.Build(
        [
            IpamFixtures.Grid("A", (1, "Guest")).AddNet("10.0.0.0", 24).AddNet("10.9.0.0", 24, view: 1).Input(),
            IpamFixtures.Grid("B").AddNet("10.0.0.0", 24).Input(),
        ]);

        var text = IpamTextReport.Render(plan, "Acme", grid: "B");

        Assert.Contains("Network view: default", text);
        Assert.DoesNotContain("Network view: Guest", text);
    }

    // ---- JSON ----

    [Fact]
    public void The_json_has_views_nested_networks_presences_and_ranges()
    {
        using var document = JsonDocument.Parse(IpamJson.ToJson(SamplePlan(), "Acme"));
        var root = document.RootElement;

        Assert.Equal("Acme", root.GetProperty("client").GetString());
        Assert.Equal(3, root.GetProperty("totals").GetProperty("networks").GetInt32());
        Assert.Equal(1, root.GetProperty("totals").GetProperty("containers").GetInt32());
        Assert.Equal(1, root.GetProperty("totals").GetProperty("dhcpRanges").GetInt32());
        Assert.Equal(2, root.GetProperty("grids").GetArrayLength());

        var view = root.GetProperty("views")[0];
        Assert.Equal("default", view.GetProperty("name").GetString());

        var container = view.GetProperty("networks")[0];
        Assert.Equal("10.0.0.0/8", container.GetProperty("prefix").GetString());
        Assert.Equal("container", container.GetProperty("kind").GetString());
        Assert.Equal(4, container.GetProperty("ipVersion").GetInt32());
        Assert.Equal(3, container.GetProperty("children").GetArrayLength());

        var shared = container.GetProperty("children")[0];
        Assert.Equal("10.1.0.0/24", shared.GetProperty("prefix").GetString());
        Assert.Equal(["DR", "Lab"], shared.GetProperty("presentIn").EnumerateArray().Select(e => e.GetString()).ToList());
        Assert.Equal(2, shared.GetProperty("presences").GetArrayLength());
        Assert.Equal("Servers", shared.GetProperty("comment").GetString());
        Assert.False(shared.GetProperty("commentVaries").GetBoolean());
        Assert.Equal(1, shared.GetProperty("rangeCount").GetInt32());

        var range = shared.GetProperty("ranges")[0];
        Assert.Equal("10.1.0.100", range.GetProperty("start").GetString());
        Assert.Equal("Lab", range.GetProperty("grid").GetString());
        Assert.Equal("gm-gmc", range.GetProperty("servedBy").GetString());
    }

    [Fact]
    public void The_json_can_be_filtered_by_grid_and_leave_the_ranges_out()
    {
        using var document = JsonDocument.Parse(IpamJson.ToJson(SamplePlan(), "Acme", grid: "DR", show: IpamLeafKinds.None));
        var container = document.RootElement.GetProperty("views")[0].GetProperty("networks")[0];

        Assert.Equal("DR", document.RootElement.GetProperty("filter").GetString());
        Assert.Equal(2, container.GetProperty("children").GetArrayLength()); // 10.1 and 10.2, not Lab's 10.5
        Assert.Equal(JsonValueKind.Null, container.GetProperty("children")[0].GetProperty("ranges").ValueKind);
    }

    [Fact]
    public void A_value_that_differs_is_given_as_a_list_with_a_flag()
    {
        var plan = IpamBuilder.Build(
        [
            IpamFixtures.Grid("A").AddNet("10.0.0.0", 24, comment: "One").Input(),
            IpamFixtures.Grid("B").AddNet("10.0.0.0", 24, comment: "Two", disabled: true).Input(),
        ]);

        using var document = JsonDocument.Parse(IpamJson.ToJson(plan, "Acme"));
        var node = document.RootElement.GetProperty("views")[0].GetProperty("networks")[0];

        Assert.True(node.GetProperty("commentVaries").GetBoolean());
        Assert.Equal("One, Two", node.GetProperty("comment").GetString());
        Assert.Equal("One,Two", string.Join(",", node.GetProperty("comments").EnumerateArray().Select(e => e.GetString())));

        Assert.True(node.GetProperty("disabledVaries").GetBoolean());
        Assert.Equal(JsonValueKind.Null, node.GetProperty("disabled").ValueKind);
        Assert.Equal("B", string.Join(",", node.GetProperty("disabledIn").EnumerateArray().Select(e => e.GetString())));
    }
}

/// <summary>The plan built from a real (encrypted) client workspace.</summary>
public sealed class IpamSourcesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pfr-ipam-{Guid.NewGuid():N}");
    private readonly ClientWorkspace _ws;

    public IpamSourcesTests()
    {
        var store = new SecureStore(_dir);
        store.Initialize("correct horse battery", SecureStoreTests.TestIterations);
        _ws = new ClientWorkspace(store);
    }

    public void Dispose()
    {
        _ws.Store.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static string Join(IEnumerable<string> values) => string.Join(",", values);

    [Fact]
    public void Each_grid_contributes_its_latest_snapshot_and_a_grid_with_none_is_noted()
    {
        var client = _ws.CreateClient("Acme");
        _ws.AddSnapshot(client.Id, IpamFixtures.Grid("Lab").AddNet("10.0.0.0", 24), "old.bak", 1, "aaa");
        _ws.AddSnapshot(client.Id, IpamFixtures.Grid("Lab").AddNet("10.0.0.0", 24).AddNet("10.9.0.0", 24), "new.bak", 1, "bbb");
        _ws.AddSnapshot(client.Id, IpamFixtures.Grid("DR").AddNet("10.0.0.0", 24), "dr.bak", 1, "ccc");
        _ws.CreateGrid(client.Id, "Empty");

        var notes = new List<string>();
        var inputs = IpamSources.LoadGrids(_ws, _ws.GetClient(client.Id), notes);

        Assert.Equal("DR,Lab", Join(inputs.Select(i => i.GridName)));
        Assert.Contains(notes, n => n.StartsWith("Empty has no snapshot"));

        var plan = IpamBuilder.Build(inputs);
        Assert.Equal(2, plan.Networks.Count); // Lab's newer snapshot has the second network; the older one is ignored
        Assert.Equal("DR,Lab", Join(plan.Networks.Single(n => n.Key == "10.0.0.0/24").PresentIn));
        Assert.Equal("Lab", Join(plan.Networks.Single(n => n.Key == "10.9.0.0/24").PresentIn));

        // Each source knows which snapshot it came from.
        var latestLab = _ws.GetClient(client.Id).Grids.Single(g => g.Name == "Lab").LatestSnapshot!;
        Assert.Equal(latestLab.Id, plan.Sources.Single(s => s.Name == "Lab").SnapshotId);
    }

    [Fact]
    public void Deleting_a_grid_takes_it_out_of_every_networks_presence()
    {
        var client = _ws.CreateClient("Acme");
        _ws.AddSnapshot(client.Id, IpamFixtures.Grid("Lab").AddNet("10.0.0.0", 24).AddNet("10.1.0.0", 24), "lab.bak", 1, "aaa");
        var (dr, _) = _ws.AddSnapshot(client.Id, IpamFixtures.Grid("DR").AddNet("10.0.0.0", 24), "dr.bak", 1, "bbb");

        _ws.DeleteGrid(client.Id, dr.Id);

        var plan = IpamBuilder.Build(IpamSources.LoadGrids(_ws, _ws.GetClient(client.Id)));
        Assert.Equal("Lab", Join(plan.Networks.Single(n => n.Key == "10.0.0.0/24").PresentIn));
        Assert.Equal(["Lab"], plan.Sources.Select(s => s.Name).ToList());
    }

    [Fact]
    public void A_client_with_no_snapshots_has_no_inputs()
    {
        var client = _ws.CreateClient("Acme");

        Assert.Empty(IpamSources.LoadGrids(_ws, _ws.GetClient(client.Id)));
    }
}
