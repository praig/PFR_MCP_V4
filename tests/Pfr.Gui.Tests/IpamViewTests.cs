using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pfr.Core;
using Pfr.Core.Clients;
using Pfr.Core.Nios.Model;
using Pfr.Core.Storage;
using Pfr.Core.Tests.Ipam;
using Pfr.Gui.Tables;
using Pfr.Gui.Views;

namespace Pfr.Gui.Tests;

public class IpamViewTests
{
    private const string Password = "correct horse battery";
    private static readonly string? ScreenshotDir = Environment.GetEnvironmentVariable("PFR_SCREENSHOT_DIR");

    private static void Snapshot(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        if (ScreenshotDir is null) return;
        Directory.CreateDirectory(ScreenshotDir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(ScreenshotDir, name + ".png"));
    }

    /// <summary>
    /// Acme Corp has two grids: Lab (a container, a network and a failover range) and DR (two networks, one the same
    /// as Lab's). Beta Ltd has one grid with a single container.
    /// </summary>
    private static (string Folder, Client Acme, Client Beta, string BetaGridId) Seed()
    {
        var folder = TestWindows.NewDataFolder();
        var store = new SecureStore(folder);
        store.Initialize(Password, TestWindows.Iterations);
        var ws = new ClientWorkspace(store);

        var acme = ws.CreateClient("Acme Corp");
        ws.AddSnapshot(acme.Id, IpamFixtures.Grid("Lab")
            .AddNet("10.0.0.0", 8, container: true, comment: "Corporate")
            .AddNet("10.1.0.0", 24, comment: "Servers")
            .AddRange("10.1.0.100", "10.1.0.199", association: "Failover", failover: "gm-gmc"), "lab.bak", 1, "lab");
        ws.AddSnapshot(acme.Id, IpamFixtures.Grid("DR")
            .AddNet("10.1.0.0", 24, comment: "Servers")
            .AddNet("10.2.0.0", 24), "dr.bak", 1, "dr");

        var beta = ws.CreateClient("Beta Ltd");
        ws.AddSnapshot(beta.Id, IpamFixtures.Grid("Beta Grid").AddNet("172.16.0.0", 16, container: true), "beta.bak", 1, "beta");
        var betaGridId = ws.GetClient(beta.Id).Grids.Single().Id;

        store.Lock();
        return (folder, acme, beta, betaGridId);
    }

    private static MainWindow Unlocked(string folder)
    {
        var window = TestWindows.New(folder);
        window.Show();
        Assert.True(window.Clients.TryUnlock(Password));
        return window;
    }

    private static async Task<MainWindow> OpenIpamAsync(string folder)
    {
        var window = Unlocked(folder);
        await window.ShowIpamAsync();
        return window;
    }

    private static string Rows(IpamView ipam) => string.Join("|", ipam.Table!.VisibleRows.Select(r => r.Cells[0]));

    private static TreeRow Row(IpamView ipam, string first) => ipam.Table!.VisibleRows.Single(r => r.Cells[0] == first);

    // ---- The tab ----

    [AvaloniaFact]
    public void The_ipam_tab_comes_after_inventory()
    {
        var window = TestWindows.New();
        window.Show();

        var headers = window.FindControl<TabControl>("ModuleTabs")!.Items.OfType<TabItem>().Select(t => (string)t.Header!);

        // The working modules are in this order; placeholders for later integrations follow them (see PlaceholderTabsTests).
        Assert.Equal("Clients,Inventory,IPAM,Network tools,Reports", string.Join(",", headers.Take(5)));
    }

    [AvaloniaFact]
    public async Task Opening_the_tab_builds_the_plan_of_the_client_being_worked_on()
    {
        var (folder, _, _, _) = Seed();
        var window = Unlocked(folder);

        window.ShowModule(AppModule.Ipam); // selecting the tab starts building

        for (var i = 0; i < 200 && window.Ipam.Table is null; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        Assert.NotNull(window.Ipam.Table);
        Assert.Equal("Acme Corp", window.Ipam.SelectedClientName);
    }

    // ---- The plan ----

    [AvaloniaFact]
    public async Task The_plan_shows_each_network_once_with_the_grids_it_is_present_in()
    {
        var (folder, _, _, _) = Seed();
        var window = await OpenIpamAsync(folder);
        var ipam = window.Ipam;

        Assert.Equal("Acme Corp,Beta Ltd", string.Join(",", ipam.ClientNames));
        Assert.Equal("Acme Corp", ipam.SelectedClientName);
        Assert.Equal("", ipam.MessageText);
        Assert.Contains("2 networks, 1 container, 1 DHCP range, 0 hosts, 0 DNS records in 1 network view", ipam.SummaryText);
        Assert.Contains("DR (snapshot", ipam.SummaryText);
        Assert.Contains("Lab (snapshot", ipam.SummaryText);
        Assert.Equal("All grids,DR,Lab", string.Join(",", ipam.GridChoices));

        // The view and the top container show; everything else is inside the collapsed container.
        Assert.Equal("default|10.0.0.0/8", Rows(ipam));
        ipam.Table!.SetAllExpanded(true);
        Snapshot(window, "52-ipam");
        Assert.Equal("default|10.0.0.0/8|10.1.0.0/24|10.1.0.100 – 10.1.0.199|10.2.0.0/24", Rows(ipam));

        var container = Row(ipam, "10.0.0.0/8");
        Assert.Equal("Container", container.Cells[1]);
        Assert.Equal("Lab", container.Cells[3]);          // only Lab has it
        Assert.Equal("1", container.Cells[5]);            // ranges, rolled up
        Assert.Equal("Corporate", container.Cells[8]);

        var shared = Row(ipam, "10.1.0.0/24");
        Assert.Equal("Network", shared.Cells[1]);
        Assert.Equal("DR, Lab", shared.Cells[3]);         // one network, present in both
        Assert.Equal("IPv4", shared.Cells[4]);
        Assert.Equal("1", shared.Cells[5]);
        Assert.Equal("Servers", shared.Cells[8]);

        var range = Row(ipam, "10.1.0.100 – 10.1.0.199");
        Assert.Equal("DHCP range · Failover gm-gmc", range.Cells[1]);
        Assert.Equal("Lab", range.Cells[3]);
    }

    [AvaloniaFact]
    public async Task Choosing_a_grid_keeps_its_networks_and_the_containers_above_them()
    {
        var (folder, _, _, _) = Seed();
        var ipam = (await OpenIpamAsync(folder)).Ipam;

        await ipam.SetFilterAsync("DR", sharedOnly: false, showRanges: true);
        ipam.Table!.SetAllExpanded(true);
        // Lab's container stays because it holds DR's networks; Lab's range is not DR's.
        Assert.Equal("default|10.0.0.0/8|10.1.0.0/24|10.2.0.0/24", Rows(ipam));

        await ipam.SetFilterAsync("Lab", sharedOnly: false, showRanges: true);
        ipam.Table!.SetAllExpanded(true);
        Assert.Equal("default|10.0.0.0/8|10.1.0.0/24|10.1.0.100 – 10.1.0.199", Rows(ipam)); // 10.2.0.0/24 is DR only
    }

    [AvaloniaFact]
    public async Task The_plan_can_be_narrowed_to_networks_in_more_than_one_grid()
    {
        var (folder, _, _, _) = Seed();
        var ipam = (await OpenIpamAsync(folder)).Ipam;

        await ipam.SetFilterAsync(null, sharedOnly: true, showRanges: true);
        ipam.Table!.SetAllExpanded(true);

        Assert.Equal("default|10.0.0.0/8|10.1.0.0/24|10.1.0.100 – 10.1.0.199", Rows(ipam));
    }

    [AvaloniaFact]
    public async Task Ranges_can_be_hidden()
    {
        var (folder, _, _, _) = Seed();
        var ipam = (await OpenIpamAsync(folder)).Ipam;

        await ipam.SetFilterAsync(null, sharedOnly: false, showRanges: false);
        ipam.Table!.SetAllExpanded(true);

        Assert.Equal("default|10.0.0.0/8|10.1.0.0/24|10.2.0.0/24", Rows(ipam));
        Assert.Equal("", Row(ipam, "10.1.0.0/24").Cells[5]); // no range count either
    }

    [AvaloniaFact]
    public async Task Ranges_in_no_network_are_listed_under_no_network()
    {
        var folder = TestWindows.NewDataFolder();
        var store = new SecureStore(folder);
        store.Initialize(Password, TestWindows.Iterations);
        var ws = new ClientWorkspace(store);
        var client = ws.CreateClient("Gamma");
        ws.AddSnapshot(client.Id, IpamFixtures.Grid("G").AddNet("10.0.0.0", 24).AddRange("192.168.1.1", "192.168.1.9"), "g.bak", 1, "g");
        store.Lock();

        var ipam = (await OpenIpamAsync(folder)).Ipam;
        ipam.Table!.SetAllExpanded(true);

        Assert.Equal("default|10.0.0.0/24|(no network)|192.168.1.1 – 192.168.1.9", Rows(ipam));
        Assert.Contains("outside every network", Row(ipam, "(no network)").Cells[1]);
    }

    [AvaloniaFact]
    public async Task Where_sources_disagree_each_value_is_listed()
    {
        var folder = TestWindows.NewDataFolder();
        var store = new SecureStore(folder);
        store.Initialize(Password, TestWindows.Iterations);
        var ws = new ClientWorkspace(store);
        var client = ws.CreateClient("Delta");
        ws.AddSnapshot(client.Id, IpamFixtures.Grid("A").AddNet("10.0.0.0", 24, comment: "Voice"), "a.bak", 1, "a");
        ws.AddSnapshot(client.Id, IpamFixtures.Grid("B").AddNet("10.0.0.0", 24, comment: "VoIP", disabled: true), "b.bak", 1, "b");
        store.Lock();

        var ipam = (await OpenIpamAsync(folder)).Ipam;

        var row = Row(ipam, "10.0.0.0/24");
        Assert.Equal("A, B", row.Cells[3]);
        Assert.Equal("Voice, VoIP", row.Cells[8]);
        Assert.Equal("In B", row.Cells[9]);
    }

    // ---- Clients and states ----

    [AvaloniaFact]
    public async Task Another_client_can_be_chosen()
    {
        var (folder, _, _, _) = Seed();
        var ipam = (await OpenIpamAsync(folder)).Ipam;

        await ipam.SelectClientAsync("Beta Ltd");

        Assert.Equal("Beta Ltd", ipam.SelectedClientName);
        Assert.Contains("0 networks, 1 container, 0 DHCP ranges, 0 hosts, 0 DNS records in 1 network view", ipam.SummaryText);
        Assert.Equal("default|172.16.0.0/16", Rows(ipam));
        Assert.Equal("All grids,Beta Grid", string.Join(",", ipam.GridChoices));
    }

    [AvaloniaFact]
    public async Task Locked_client_data_shows_a_message_instead_of_a_plan()
    {
        var (folder, _, _, _) = Seed();
        var window = await OpenIpamAsync(folder);

        await window.Ipam.ShowAsync(null);

        Assert.Null(window.Ipam.Table);
        Assert.Contains("locked", window.Ipam.MessageText);
    }

    [AvaloniaFact]
    public async Task A_client_with_no_snapshots_says_so_and_says_what_to_do()
    {
        var folder = TestWindows.NewDataFolder();
        var store = new SecureStore(folder);
        store.Initialize(Password, TestWindows.Iterations);
        new ClientWorkspace(store).CreateClient("Empty Co");
        store.Lock();

        var ipam = (await OpenIpamAsync(folder)).Ipam;

        Assert.Null(ipam.Table);
        Assert.Contains("Empty Co has no grid snapshots yet", ipam.MessageText);
        Assert.Contains("Import a backup", ipam.MessageText);
    }

    [AvaloniaFact]
    public async Task A_grid_with_no_snapshot_is_noted_but_the_others_are_shown()
    {
        var folder = TestWindows.NewDataFolder();
        var store = new SecureStore(folder);
        store.Initialize(Password, TestWindows.Iterations);
        var ws = new ClientWorkspace(store);
        var client = ws.CreateClient("Epsilon");
        ws.AddSnapshot(client.Id, IpamFixtures.Grid("Lab").AddNet("10.0.0.0", 24), "lab.bak", 1, "lab");
        ws.CreateGrid(client.Id, "Not yet imported");
        store.Lock();

        var ipam = (await OpenIpamAsync(folder)).Ipam;

        Assert.Equal("default|10.0.0.0/24", Rows(ipam));
        Assert.Contains("Not yet imported has no snapshot yet", ipam.NoticeText);
    }

    // ---- Staying up to date ----

    [AvaloniaFact]
    public async Task The_plan_follows_the_client_whose_grid_was_opened()
    {
        var (folder, _, beta, betaGridId) = Seed();
        var window = Unlocked(folder);

        await window.Clients.OpenGridAsync(beta.Id, betaGridId, switchToInventory: false);
        await window.ShowIpamAsync();

        Assert.Equal("Beta Ltd", window.Ipam.SelectedClientName);
    }

    [AvaloniaFact]
    public async Task The_plan_is_rebuilt_only_when_something_changed()
    {
        var (folder, acme, _, _) = Seed();
        var window = await OpenIpamAsync(folder);
        var before = window.Ipam.Plan;

        await window.ShowIpamAsync(); // nothing changed: the same plan, not a rebuild
        Assert.Same(before, window.Ipam.Plan);

        // A new snapshot of Lab with another network, saved to the client.
        window.Clients.Workspace!.AddSnapshot(acme.Id, IpamFixtures.Grid("Lab")
            .AddNet("10.0.0.0", 8, container: true).AddNet("10.1.0.0", 24).AddNet("10.9.0.0", 24), "lab2.bak", 1, "lab2");
        window.Clients.RefreshClients(acme.Id);
        await window.ShowIpamAsync();

        Assert.NotSame(before, window.Ipam.Plan);
        Assert.Equal(3, window.Ipam.Plan!.NetworkCount); // 10.1, 10.2 and the new 10.9
        window.Ipam.Table!.SetAllExpanded(true);
        Assert.Contains("10.9.0.0/24", Rows(window.Ipam));
    }

    // ---- Hosts and records ----

    /// <summary>One grid: a network with a DNS host (with its generated A and PTR records), an IPAM-only host and a lone A record.</summary>
    private static string SeedHosts(int extraRecords = 0)
    {
        var folder = TestWindows.NewDataFolder();
        var store = new SecureStore(folder);
        store.Initialize(Password, TestWindows.Iterations);
        var ws = new ClientWorkspace(store);
        var client = ws.CreateClient("Hosty");

        var lab = IpamFixtures.Grid("Lab").WithDns().AddNet("10.0.0.0", 24, comment: "Servers")
            .AddHost("server1.lab.test", ["10.0.0.20"], ttl: 600, aliases: ["files.lab.test"], dhcp: true)
            .AddHost("printer", ["10.0.0.30"], dns: false)
            .AddRecord(DnsRecordType.A, "www.lab.test", "10.0.0.10");
        for (var i = 0; i < extraRecords; i++) lab.AddRecord(DnsRecordType.A, $"h{i}.lab.test", "10.0.0.50");
        ws.AddSnapshot(client.Id, lab, "lab.bak", 1, "lab");
        store.Lock();
        return folder;
    }

    [AvaloniaFact]
    public async Task Hosts_are_listed_with_the_records_they_generate_under_them_and_other_records_on_their_own()
    {
        var window = await OpenIpamAsync(SeedHosts());
        var ipam = window.Ipam;
        window.ShowModule(AppModule.Ipam);
        ipam.Table!.SetAllExpanded(true);
        Snapshot(window, "55-ipam-hosts");

        Assert.Contains("0 DHCP ranges, 2 hosts, 3 DNS records in 1 network view", ipam.SummaryText);
        Assert.Equal("default|10.0.0.0/24|10.0.0.20|10.0.0.20|10.0.0.20|10.0.0.30|10.0.0.10", Rows(ipam));

        var network = Row(ipam, "10.0.0.0/24");
        Assert.Equal("", network.Cells[5]);   // no ranges
        Assert.Equal("2", network.Cells[6]);  // hosts
        Assert.Equal("3", network.Cells[7]);  // the host's A and PTR, and the A record on its own

        // The three rows at 10.0.0.20: the host, then the two records it generates.
        var at20 = ipam.Table.VisibleRows.Where(r => r.Cells[0] == "10.0.0.20").ToList();
        Assert.Equal("Host · DNS · DHCP", at20[0].Cells[1]);
        Assert.Equal("server1.lab.test", at20[0].Cells[2]);
        Assert.Equal("A record", at20[1].Cells[1]);
        Assert.Equal("server1.lab.test", at20[1].Cells[2]);
        Assert.Equal("PTR record · server1.lab.test", at20[2].Cells[1]);
        Assert.Equal("20.0.0.10.in-addr.arpa", at20[2].Cells[2]);
        Assert.All(at20, r => Assert.Equal("Lab", r.Cells[3]));
        Assert.IsType<IpamHostTag>(at20[0].Tag);
        Assert.Same(at20[0].Tag is IpamHostTag h ? h.Host : null, ((IpamRecordTag)at20[1].Tag!).Host); // the record knows its host

        var printer = Row(ipam, "10.0.0.30");
        Assert.Equal("Host · IPAM only", printer.Cells[1]);
        Assert.Equal("printer", printer.Cells[2]);

        var lone = Row(ipam, "10.0.0.10");
        Assert.Equal("A record", lone.Cells[1]);
        Assert.Equal("www.lab.test", lone.Cells[2]);
        Assert.Null(((IpamRecordTag)lone.Tag!).Host);
    }

    [AvaloniaFact]
    public async Task Hosts_and_records_can_be_switched_off_separately()
    {
        var ipam = (await OpenIpamAsync(SeedHosts())).Ipam;

        await ipam.SetFilterAsync(null, sharedOnly: false, showRanges: true, showHosts: true, showRecords: false);
        ipam.Table!.SetAllExpanded(true);
        Assert.Equal("default|10.0.0.0/24|10.0.0.20|10.0.0.30", Rows(ipam)); // no records, not even under the host
        Assert.Equal("", Row(ipam, "10.0.0.0/24").Cells[7]);
        Assert.Equal("2", Row(ipam, "10.0.0.0/24").Cells[6]);

        await ipam.SetFilterAsync(null, sharedOnly: false, showRanges: true, showHosts: false, showRecords: true);
        ipam.Table!.SetAllExpanded(true);
        Assert.Equal("default|10.0.0.0/24|10.0.0.10", Rows(ipam)); // the host's own records go with the host

        await ipam.SetFilterAsync(null, sharedOnly: false, showRanges: true, showHosts: false, showRecords: false);
        ipam.Table!.SetAllExpanded(true);
        Assert.Equal("default|10.0.0.0/24", Rows(ipam));
    }

    [AvaloniaFact]
    public async Task Selecting_a_host_or_a_record_shows_its_details()
    {
        var window = await OpenIpamAsync(SeedHosts());
        var ipam = window.Ipam;
        window.ShowModule(AppModule.Ipam);
        ipam.Table!.SetAllExpanded(true);

        var at20 = ipam.Table.VisibleRows.Where(r => r.Cells[0] == "10.0.0.20").ToList();
        ipam.Table.Select(at20[0]);
        Assert.Equal("Host server1.lab.test · 10.0.0.20", ipam.Details!.Title);
        Assert.Contains("Aliases: files.lab.test.", ipam.Details.Note);

        ipam.Table.Select(at20[2]);
        Assert.Equal("PTR record 20.0.0.10.in-addr.arpa", ipam.Details!.Title);
        Assert.Contains("Generated by the host server1.lab.test", ipam.Details.Note);
    }

    [AvaloniaFact]
    public async Task A_very_large_number_of_records_is_not_listed_until_asked_for_and_the_page_says_so()
    {
        var ipam = (await OpenIpamAsync(SeedHosts(extraRecords: IpamView.RecordRowLimit))).Ipam;

        Assert.Contains("DNS records are not listed", ipam.NoticeText);
        Assert.Equal("default|10.0.0.0/24", Rows(ipam)); // collapsed network, no record rows built
        ipam.Table!.SetAllExpanded(true);
        Assert.Equal("default|10.0.0.0/24|10.0.0.20|10.0.0.30", Rows(ipam)); // hosts are still listed, records are not
    }

    // ---- The details panel ----

    [AvaloniaFact]
    public async Task Selecting_a_row_shows_what_each_grid_says_about_it()
    {
        var (folder, _, _, _) = Seed();
        var window = await OpenIpamAsync(folder);
        var ipam = window.Ipam;
        window.ShowModule(AppModule.Ipam); // the tree is on screen, so its selection works as it does for a user
        ipam.Table!.SetAllExpanded(true);
        Snapshot(window, "53-ipam-details-hint");

        Assert.Null(ipam.Details); // nothing selected yet: the panel only has its hint

        // A network present in both grids: one row for each, with that grid's own comment.
        ipam.Table.Select(Row(ipam, "10.1.0.0/24"));
        Snapshot(window, "54-ipam-details-network");
        var network = ipam.Details!;
        Assert.Equal("10.1.0.0/24 · Network · IPv4", network.Title);
        Assert.Equal("DR,Lab", string.Join(",", network.Rows.Select(r => r[0])));
        Assert.Equal("Servers,Servers", string.Join(",", network.Rows.Select(r => r[3])));

        // A DHCP range: where it sits, and that Lab (its own grid) has that network.
        ipam.Table.Select(Row(ipam, "10.1.0.100 – 10.1.0.199"));
        var range = ipam.Details!;
        Assert.StartsWith("DHCP range 10.1.0.100", range.Title);
        Assert.Contains("Sits in 10.1.0.0/24", range.Note);
        Assert.DoesNotContain("does not have", range.Note);

        // A network view: what each grid has in it.
        ipam.Table.Select(Row(ipam, "default"));
        var view = ipam.Details!;
        Assert.Equal("Network view default", view.Title);
        Assert.Equal("DR|2|0|0|0|0,Lab|1|1|1|0|0", string.Join(",", view.Rows.Select(r => string.Join("|", r))));
    }

    [AvaloniaFact]
    public async Task The_panel_is_hidden_when_there_is_no_plan_and_clear_after_a_filter_change()
    {
        var (folder, _, _, _) = Seed();
        var window = await OpenIpamAsync(folder);
        var ipam = window.Ipam;
        window.ShowModule(AppModule.Ipam);
        ipam.Table!.SetAllExpanded(true);
        ipam.Table.Select(Row(ipam, "10.1.0.0/24"));
        Assert.NotNull(ipam.Details);

        await ipam.SetFilterAsync("DR", sharedOnly: false, showRanges: true); // a new tree: nothing is selected in it
        Assert.Null(ipam.Details);

        await ipam.ShowAsync(null); // locked: no plan, so no details either
        Assert.Null(ipam.Details);
    }
}
