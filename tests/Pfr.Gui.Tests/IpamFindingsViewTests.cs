using Pfr.Core.Clients;
using Pfr.Core.Ipam;
using Pfr.Core.Storage;
using Pfr.Core.Tests.Ipam;
using Pfr.Gui.Tables;
using Pfr.Gui.Views;
using Avalonia.Headless.XUnit;

namespace Pfr.Gui.Tests;

public class IpamFindingsViewTests
{
    private const string Password = "correct horse battery";

    /// <summary>
    /// Acme has two grids: Lab (10.5.0.0/16, and 10.1.0.0/24 with a DHCP range) and DR (10.5.1.0/24, which lies inside
    /// Lab's /16, and 10.1.0.0/24). That gives one warning (a network inside a network) and one piece of information
    /// (two networks with no DHCP range). Beta has a single container and nothing to report.
    /// </summary>
    private static string Seed()
    {
        var folder = TestWindows.NewDataFolder();
        var store = new SecureStore(folder);
        store.Initialize(Password, TestWindows.Iterations);
        var ws = new ClientWorkspace(store);

        var acme = ws.CreateClient("Acme Corp");
        ws.AddSnapshot(acme.Id, IpamFixtures.Grid("Lab").AddNet("10.5.0.0", 16).AddNet("10.1.0.0", 24).AddRange("10.1.0.100", "10.1.0.199"), "lab.bak", 1, "lab");
        ws.AddSnapshot(acme.Id, IpamFixtures.Grid("DR").AddNet("10.5.1.0", 24).AddNet("10.1.0.0", 24), "dr.bak", 1, "dr");

        var beta = ws.CreateClient("Beta Ltd");
        ws.AddSnapshot(beta.Id, IpamFixtures.Grid("Beta Grid").AddNet("172.16.0.0", 16, container: true), "beta.bak", 1, "beta");

        store.Lock();
        return folder;
    }

    private static async Task<Pfr.Gui.Views.IpamView> OpenAsync(string client)
    {
        var window = TestWindows.New(Seed());
        window.Show();
        Assert.True(window.Clients.TryUnlock(Password));
        await window.ShowIpamAsync();
        await window.Ipam.SelectClientAsync(client);
        return window.Ipam;
    }

    [AvaloniaFact]
    public async Task The_findings_switch_says_how_many_findings_there_are()
    {
        var acme = await OpenAsync("Acme Corp");
        Assert.Equal("Findings (2)", acme.FindingsLabel);
        Assert.Equal(2, acme.Findings.Count);

        await acme.SelectClientAsync("Beta Ltd");
        Assert.Equal("Findings", acme.FindingsLabel);
        Assert.Empty(acme.Findings);
    }

    [AvaloniaFact]
    public async Task Turning_findings_on_replaces_the_tree_with_a_table_and_off_brings_it_back()
    {
        var ipam = await OpenAsync("Acme Corp");
        Assert.NotNull(ipam.Table);
        Assert.Null(ipam.FindingsTable);

        await ipam.SetShowFindingsAsync(true);

        Assert.Null(ipam.Table);
        var table = Assert.IsType<Pfr.Gui.Controls.FilterableTable>(ipam.FindingsTable);
        Assert.Equal(2, table.VisibleRowCount);
        Assert.Equal(["Warning", "Network contains another network", "10.5.1.0/24"], table.VisibleRows[0].Cells.Take(3).ToArray());
        Assert.Equal("Information", table.VisibleRows[1].Cells[0]);
        Assert.Contains("2 findings: 1 warning, 1 for information.", ipam.SummaryText);
        Assert.Contains("3 networks", ipam.SummaryText); // the plan's own summary is still there

        await ipam.SetShowFindingsAsync(false);

        Assert.NotNull(ipam.Table);
        Assert.Null(ipam.FindingsTable);
        Assert.DoesNotContain("findings", ipam.SummaryText);
    }

    [AvaloniaFact]
    public async Task Selecting_a_finding_lists_what_it_covers()
    {
        var ipam = await OpenAsync("Acme Corp");
        await ipam.SetShowFindingsAsync(true);
        var table = ipam.FindingsTable!;
        Assert.Null(ipam.Details);

        table.Select(table.VisibleRows[0]);

        var detail = ipam.Details!;
        Assert.Equal("Network contains another network: 10.5.1.0/24", detail.Title);
        Assert.Equal(["Objects"], detail.Headers);
        Assert.Equal(["10.5.0.0/16 (network on Lab)", "10.5.1.0/24 (network on DR)"], detail.Rows.Select(r => r[0]).ToArray());
        Assert.Contains("lies inside the network 10.5.0.0/16", detail.Note);

        table.Select(table.VisibleRows[1]);
        Assert.Equal(["10.5.0.0/16", "10.5.1.0/24"], ipam.Details!.Rows.Select(r => r[0]).ToArray());

        table.Select(null);
        Assert.Null(ipam.Details);
    }

    [AvaloniaFact]
    public async Task The_findings_can_be_searched()
    {
        var ipam = await OpenAsync("Acme Corp");
        await ipam.SetShowFindingsAsync(true);

        ipam.FindingsTable!.ApplyFilter("inside");

        Assert.Equal(1, ipam.FindingsTable.VisibleRowCount);
        Assert.Equal("Warning", ipam.FindingsTable.VisibleRows[0].Cells[0]);
    }

    [AvaloniaFact]
    public async Task A_plan_with_nothing_to_report_says_so()
    {
        var ipam = await OpenAsync("Beta Ltd");

        await ipam.SetShowFindingsAsync(true);

        Assert.Equal(0, ipam.FindingsTable!.VisibleRowCount);
        Assert.Contains("No findings in this plan.", ipam.SummaryText);
        Assert.Null(ipam.Details);
    }

    [AvaloniaFact]
    public async Task Findings_stay_on_screen_when_the_plan_is_rebuilt_or_another_client_is_chosen()
    {
        var ipam = await OpenAsync("Acme Corp");
        await ipam.SetShowFindingsAsync(true);

        await ipam.RebuildAsync();
        Assert.NotNull(ipam.FindingsTable);
        Assert.Equal(2, ipam.FindingsTable!.VisibleRowCount);

        await ipam.SelectClientAsync("Beta Ltd");
        Assert.NotNull(ipam.FindingsTable);
        Assert.Equal(0, ipam.FindingsTable!.VisibleRowCount);
    }
}

public class IpamFindingDetailTests
{
    [Fact]
    public void A_finding_about_one_network_shows_what_each_grid_says_about_it()
    {
        var plan = IpamBuilder.Build(new[]
        {
            IpamFixtures.Grid("A").AddNet("10.1.0.0", 24, comment: "Servers").Input(),
            IpamFixtures.Grid("B").AddNet("10.1.0.0", 24).Input(),
            IpamFixtures.Grid("C").AddNet("10.9.0.0", 24).Input(),
        });
        var gap = IpamFindings.Analyze(plan).Single(f => f.Kind == IpamFindingKind.PresenceGap);

        var detail = IpamDetails.ForFinding(plan, gap);

        Assert.Equal(detail.Headers.Count, detail.Rows[0].Count);
        Assert.Equal(["A", "B"], detail.Rows.Select(r => r[0]).ToArray());
        Assert.StartsWith(gap.Summary, detail.Note);
    }

    [Fact]
    public void A_long_list_of_objects_is_cut_and_says_how_many_are_left()
    {
        var items = Enumerable.Range(1, 600).Select(i => $"host {i}").ToList();
        var finding = new IpamFinding(IpamFindingKind.OutsideAnyNetwork, IpamSeverity.Warning, "A", "600 objects are in no network.", ["A"], items);

        var detail = IpamDetails.ForFinding(new IpamPlan(), finding);

        Assert.Equal(IpamDetails.MaxFindingRows, detail.Rows.Count);
        Assert.Equal("host 1", detail.Rows[0][0]);
        Assert.Contains("100 more are not listed here", detail.Note);
    }
}
