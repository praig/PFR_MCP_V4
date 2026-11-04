using System.Net;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pfr.Core.Clients;
using Pfr.Core.Network;
using Pfr.Core.Nios.Model;
using Pfr.Core.Storage;
using Pfr.Core.Tests.Ipam;
using Pfr.Core.Tests.Network;
using Pfr.Gui.Views;

namespace Pfr.Gui.Tests;

public class NetworkToolsViewTests
{
    private const string Password = "correct horse battery";

    /// <summary>A grid with a DNS and DHCP member, a second DNS member, and a member that serves neither.</summary>
    private static GridInventory LabGrid()
    {
        var inventory = IpamFixtures.Grid("Lab");
        var gm = new GridMember { Oid = 0, HostName = "gm.lab.test", Ipv4Address = "10.0.0.1" };
        gm.Services.AddRange(["DNS", "DHCP"]);
        var ns = new GridMember { Oid = 1, HostName = "ns1.lab.test", Ipv4Address = "10.0.0.2" };
        ns.Services.Add("DNS");
        var other = new GridMember { Oid = 2, HostName = "other.lab.test", Ipv4Address = "10.0.0.3" };
        inventory.Members.AddRange([gm, ns, other]);
        return inventory;
    }

    /// <summary>A window with Acme's Lab grid open, as if chosen in the grid picker.</summary>
    private static (MainWindow Window, Client Client, ClientGrid Grid) OpenLab(bool savedConnection = false)
    {
        var folder = TestWindows.NewDataFolder();
        var store = new SecureStore(folder);
        store.Initialize(Password, TestWindows.Iterations);
        var seed = new ClientWorkspace(store);
        var acme = seed.CreateClient("Acme Corp");
        seed.AddSnapshot(acme.Id, LabGrid(), "lab.bak", 1, "lab");
        var gridId = seed.GetClient(acme.Id).Grids.Single().Id;
        if (savedConnection)
        {
            seed.SaveConnection(acme.Id, gridId, new GridConnection
            {
                Address = "gm.lab.test",
                ReadOnly = new Credential { Username = "ro-user", Password = "ro-pass" },
                ReadWrite = new Credential { Username = "rw-user", Password = "rw-pass" },
            });
        }
        store.Lock();

        var window = TestWindows.New(folder);
        window.Show();
        Assert.True(window.Clients.TryUnlock(Password));
        var workspace = window.Clients.Workspace!;
        var client = workspace.GetClient(acme.Id);
        var grid = client.Grids.Single();
        var latest = workspace.LoadLatest(client.Id, grid.Id)!.Value;
        window.ShowSnapshot(client, grid, latest.Snapshot, latest.Inventory, switchToInventory: false);
        window.Tools.RefreshContext();
        return (window, client, grid);
    }

    private static MainWindow Plain()
    {
        var window = TestWindows.New();
        window.Show();
        return window;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(condition(), "Gave up waiting.");
    }

    // ---- The tab ----

    [AvaloniaFact]
    public void The_network_tools_tab_holds_ping_dns_and_ssh()
    {
        var window = Plain();

        var tab = window.FindControl<TabControl>("ModuleTabs")!.Items.OfType<TabItem>().Single(t => (string)t.Header! == "Network tools");

        Assert.Same(window.Tools, tab.Content);
        Assert.Equal("Ping,DNS lookup,SSH commands", string.Join(",", window.Tools.PageTitles));
    }

    // ---- Ping ----

    private static PingAttempt Reply(int sequence) => new(sequence, PingOutcome.Success, TimeSpan.FromMilliseconds(3), null, null);

    private static PingAttempt Silence(int sequence) => new(sequence, PingOutcome.TimedOut, null, null, null);

    [AvaloniaFact]
    public async Task Listed_hosts_are_pinged_and_shown_in_a_table_with_their_results()
    {
        var ping = Plain().Tools.Ping;
        ping.TargetsText = "10.0.0.1  # gm.lab.test\n10.0.0.2";
        ping.PingCount = 2;
        ping.IntervalMs = 0;

        await ping.RunAsync(new FakePinger((address, n) => address.ToString() == "10.0.0.1" ? Reply(n) : Silence(n)));

        Assert.Equal(["gm.lab.test", "10.0.0.2"], ping.Results.Select(r => r.Target.Label).ToArray());
        Assert.Equal(["up", "down"], ping.Results.Select(r => r.Status).ToArray());
        Assert.Equal("1 of 2 answered.", ping.MessageText);
        Assert.Equal(2, ping.Table!.VisibleRowCount);
        Assert.False(ping.IsRunning);
    }

    [AvaloniaFact]
    public async Task Pinging_with_nothing_listed_says_what_to_do()
    {
        var ping = Plain().Tools.Ping;

        await ping.RunAsync(new FakePinger((_, n) => Reply(n)));

        Assert.Contains("at least one host", ping.MessageText);
        Assert.Null(ping.Table);
    }

    [AvaloniaFact]
    public void The_open_grids_members_can_be_added_once_and_keep_their_names()
    {
        var (window, _, _) = OpenLab();
        var ping = window.Tools.Ping;

        Assert.Equal(3, ping.AddMembers());
        Assert.Contains("10.0.0.1  # gm.lab.test", ping.TargetsText);
        Assert.Contains("10.0.0.3  # other.lab.test", ping.TargetsText);

        Assert.Equal(0, ping.AddMembers()); // nothing new the second time
        Assert.Equal(3, PingTargets.Parse(ping.TargetsText).Count);
    }

    [AvaloniaFact]
    public async Task A_grids_members_are_listed_under_their_names_in_the_results()
    {
        var (window, _, _) = OpenLab();
        var ping = window.Tools.Ping;
        ping.AddMembers();
        ping.PingCount = 1;
        ping.IntervalMs = 0;

        await ping.RunAsync(new FakePinger((_, n) => Reply(n)));

        Assert.Equal(["gm.lab.test", "ns1.lab.test", "other.lab.test"], ping.Results.Select(r => r.Target.Label).ToArray());
        Assert.Equal("3 of 3 answered.", ping.MessageText);
    }

    [AvaloniaFact]
    public void With_no_grid_open_there_are_no_members_to_add()
    {
        var ping = Plain().Tools.Ping;

        Assert.Equal(0, ping.AddMembers());
        Assert.Contains("No grid is open", ping.MessageText);
    }

    /// <summary>Waits for a reply that never comes, until it is told to stop.</summary>
    private sealed class StallingPinger : IPinger
    {
        public async Task<PingAttempt> PingAsync(IPAddress address, int sequence, PingSettings settings, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Reply(sequence);
        }
    }

    [AvaloniaFact]
    public async Task A_ping_that_is_running_can_be_stopped()
    {
        var ping = Plain().Tools.Ping;
        ping.TargetsText = "10.0.0.1";

        var run = ping.RunAsync(new StallingPinger());
        Assert.True(ping.IsRunning);

        ping.Stop();
        await run;

        Assert.False(ping.IsRunning);
        Assert.Contains("before it was stopped", ping.MessageText);
    }

    // ---- DNS ----

    private static DnsExchange Www(byte[] query, bool tcp) =>
        FakeDnsTransport.Reply(query, WireBuilder.Answer, tcp, b => b.AnswerRecord(WireBuilder.Pointer(12), DnsType.A, 300, WireBuilder.Address("10.0.0.10")));

    [AvaloniaFact]
    public async Task A_name_is_looked_up_on_the_server_typed_and_shown_the_way_dig_shows_it()
    {
        var dns = Plain().Tools.Dns;
        dns.ServerText = "10.0.0.1";
        dns.NameText = "www.lab.test";
        var transport = new PerServerDnsTransport((_, query, tcp) => Www(query, tcp));

        await dns.LookUpAsync(transport);

        Assert.Equal("10.0.0.1", Assert.Single(transport.Asked).ToString());
        Assert.Contains(";; ANSWER SECTION:", dns.Output);
        Assert.Contains("10.0.0.10", dns.Output);
        Assert.Equal("", dns.MessageText);
    }

    [AvaloniaFact]
    public async Task The_record_type_and_the_options_are_used()
    {
        var dns = Plain().Tools.Dns;
        dns.ServerText = "10.0.0.1";
        dns.NameText = "lab.test";
        dns.TypeText = "mx";
        dns.Tcp = true;
        dns.Dnssec = true;
        dns.NoRecursion = true;
        var transport = new FakeDnsTransport((query, tcp, _) => Www(query, tcp));

        await dns.LookUpAsync(transport);

        var (usedTcp, wire) = Assert.Single(transport.Calls);
        var sent = DnsMessage.Parse(wire);
        Assert.True(usedTcp);
        Assert.Equal(DnsType.MX, sent.Questions[0].Type);
        Assert.True(sent.Edns!.DnssecOk);
        Assert.False(sent.Flags.RecursionDesired);
    }

    [AvaloniaFact]
    public async Task A_type_written_after_the_name_and_the_chaos_class_are_honoured()
    {
        var dns = Plain().Tools.Dns;
        dns.ServerText = "10.0.0.1";
        dns.NameText = "version.bind TXT";
        dns.ClassText = "CH";
        var transport = new FakeDnsTransport((query, tcp, _) => Www(query, tcp));

        await dns.LookUpAsync(transport);

        var question = DnsMessage.Parse(Assert.Single(transport.Calls).Query).Questions[0];
        Assert.Equal(("version.bind.", DnsType.TXT, DnsClass.CH), (question.Name, question.Type, question.Class));
    }

    [AvaloniaFact]
    public async Task An_address_is_looked_up_in_reverse()
    {
        var dns = Plain().Tools.Dns;
        dns.ServerText = "10.0.0.1";
        dns.NameText = "10.0.0.20";
        var transport = new FakeDnsTransport((query, tcp, _) => Www(query, tcp));

        await dns.LookUpAsync(transport);

        var question = DnsMessage.Parse(Assert.Single(transport.Calls).Query).Questions[0];
        Assert.Equal(("20.0.0.10.in-addr.arpa.", DnsType.PTR), (question.Name, question.Type));
    }

    [AvaloniaFact]
    public async Task Several_lines_are_a_batch_and_the_answers_can_be_shown_on_their_own()
    {
        var dns = Plain().Tools.Dns;
        dns.ServerText = "10.0.0.1";
        dns.NameText = "www.lab.test\nmail.lab.test MX";
        var transport = new FakeDnsTransport((query, tcp, _) => Www(query, tcp));

        await dns.LookUpAsync(transport);

        Assert.Equal(2, transport.Calls.Count);
        Assert.Contains("Query", dns.Output);
        Assert.Contains("www.lab.test", dns.Output);
        Assert.Contains("mail.lab.test", dns.Output);
        Assert.Contains("NOERROR", dns.Output);

        dns.NameText = "www.lab.test";
        dns.AnswersOnly = true;
        await dns.LookUpAsync(transport);
        Assert.Equal("10.0.0.10", dns.Output.Trim());
    }

    [AvaloniaFact]
    public async Task A_member_picked_from_the_list_is_understood()
    {
        var (window, _, _) = OpenLab();
        var dns = window.Tools.Dns;
        Assert.Equal(["gm.lab.test (10.0.0.1)", "ns1.lab.test (10.0.0.2)"], dns.ServerChoices.ToArray()); // only the DNS members
        dns.ServerText = dns.ServerChoices[1];
        dns.NameText = "www.lab.test";
        var transport = new PerServerDnsTransport((_, query, tcp) => Www(query, tcp));

        await dns.LookUpAsync(transport);

        Assert.Equal("10.0.0.2", Assert.Single(transport.Asked).ToString());
    }

    [AvaloniaFact]
    public async Task A_name_to_ask_about_is_needed_and_a_dead_server_is_explained()
    {
        var dns = Plain().Tools.Dns;
        dns.ServerText = "10.0.0.1";

        await dns.LookUpAsync(new FakeDnsTransport((query, tcp, _) => Www(query, tcp)));
        Assert.Contains("Type a name to look up", dns.MessageText);

        dns.NameText = "www.lab.test";
        await dns.LookUpAsync(new FakeDnsTransport((_, _, _) => throw new DnsTimeoutException("No answer from 10.0.0.1 within 5 seconds.")));
        Assert.Equal("No answer from 10.0.0.1 within 5 seconds.", dns.MessageText);
        Assert.Equal("", dns.Output);
        Assert.False(dns.IsRunning);
    }

    [AvaloniaFact]
    public async Task Every_dns_member_of_the_open_grid_can_be_asked_and_compared()
    {
        var (window, _, _) = OpenLab();
        var dns = window.Tools.Dns;
        dns.NameText = "www.lab.test";
        var transport = new PerServerDnsTransport((server, query, tcp) =>
            FakeDnsTransport.Reply(query, WireBuilder.Answer, tcp, b => b.AnswerRecord(
                WireBuilder.Pointer(12), DnsType.A, 300, WireBuilder.Address(server.ToString() == "10.0.0.2" ? "10.0.0.99" : "10.0.0.10"))));

        await dns.CompareAsync(transport);

        Assert.Equal(["10.0.0.1", "10.0.0.2"], transport.Asked.Select(a => a.ToString()).Order().ToArray());
        Assert.Contains("The servers do not agree: 2 different answers.", dns.Output);
        Assert.Contains("gm.lab.test", dns.Output);
        Assert.Contains("ns1.lab.test", dns.Output);
    }

    [AvaloniaFact]
    public async Task Comparing_needs_an_open_grid_with_dns_members()
    {
        var dns = Plain().Tools.Dns;
        dns.NameText = "www.lab.test";

        await dns.CompareAsync(new FakeDnsTransport((query, tcp, _) => Www(query, tcp)));

        Assert.Contains("No grid is open", dns.MessageText);
    }

    // ---- SSH ----

    private static readonly SshTarget Gm = new("gm.lab.test", "gm.lab.test");

    [AvaloniaFact]
    public async Task A_new_hosts_key_is_put_to_the_engineer_and_when_trusted_the_commands_run()
    {
        var ssh = Plain().Tools.Ssh;
        ssh.HostsText = "gm.lab.test\nns1.lab.test";
        ssh.User = "admin";
        ssh.Password = "s3cret!";
        var client = new FakeSshClient(FakeSshClient.Honest);

        var run = ssh.RunAsync(client);

        await WaitForAsync(() => ssh.PendingKey is { Target.Host: "gm.lab.test" });
        Assert.True(ssh.KeyQuestionVisible);
        Assert.Contains(FakeSshClient.KeyOf(Gm).Fingerprint, ssh.KeyQuestionText);
        Assert.Contains("ssh-ed25519", ssh.KeyQuestionText);
        ssh.TrustPendingKey();

        await WaitForAsync(() => ssh.PendingKey is { Target.Host: "ns1.lab.test" });
        ssh.TrustPendingKey();
        await run;

        Assert.False(ssh.KeyQuestionVisible);
        Assert.Equal(2, Regex.Matches(ssh.Output, "NIOS 9.0.8").Count);
        Assert.Contains("$ show version", ssh.Output);
        Assert.Equal("2 of 2 hosts done without a problem.", ssh.MessageText);
        Assert.False(ssh.IsRunning);
    }

    [AvaloniaFact]
    public async Task A_key_that_is_not_trusted_is_not_pinned_and_that_host_is_not_run()
    {
        var ssh = Plain().Tools.Ssh;
        ssh.HostsText = "gm.lab.test";
        ssh.User = "admin";
        ssh.Password = "s3cret!";
        var client = new FakeSshClient(FakeSshClient.Honest);

        var run = ssh.RunAsync(client);
        await WaitForAsync(() => ssh.PendingKey is not null);
        ssh.RejectPendingKey();
        await run;

        Assert.Contains("has not been pinned yet", ssh.Output);
        Assert.DoesNotContain("NIOS 9.0.8", ssh.Output);
        Assert.Equal("0 of 1 host done without a problem.", ssh.MessageText);

        // Nothing was pinned, so it is asked about again.
        var again = ssh.RunAsync(client);
        await WaitForAsync(() => ssh.PendingKey is not null);
        ssh.RejectPendingKey();
        await again;
    }

    [AvaloniaFact]
    public async Task Without_a_client_a_trusted_key_is_remembered_for_the_session_only()
    {
        var ssh = Plain().Tools.Ssh;
        Assert.Contains("only remembered until the application closes", ssh.AccountNote);
        ssh.HostsText = "gm.lab.test";
        ssh.User = "admin";
        ssh.Password = "s3cret!";
        var client = new FakeSshClient(FakeSshClient.Honest);

        var first = ssh.RunAsync(client);
        await WaitForAsync(() => ssh.PendingKey is not null);
        ssh.TrustPendingKey();
        await first;

        await ssh.RunAsync(client); // no question this time
        Assert.Equal("1 of 1 host done without a problem.", ssh.MessageText);
        Assert.Equal(3, client.Calls.Count); // refused, retried once the key was trusted, then run again
    }

    [AvaloniaFact]
    public async Task A_trusted_key_is_pinned_for_the_client_and_can_be_forgotten()
    {
        var (window, client, _) = OpenLab();
        var workspace = window.Clients.Workspace!;
        var ssh = window.Tools.Ssh;
        Assert.Contains("Host keys are pinned for Acme Corp.", ssh.AccountNote);
        ssh.HostsText = "gm.lab.test";
        ssh.User = "admin";
        ssh.Password = "s3cret!";
        var fake = new FakeSshClient(FakeSshClient.Honest);

        var run = ssh.RunAsync(fake);
        await WaitForAsync(() => ssh.PendingKey is not null);
        ssh.TrustPendingKey();
        await run;

        Assert.Equal(FakeSshClient.KeyOf(Gm).Fingerprint, workspace.FindSshHostKey(client.Id, "gm.lab.test", 22)!.Fingerprint);

        await ssh.RunAsync(fake); // pinned: no question
        Assert.Equal("1 of 1 host done without a problem.", ssh.MessageText);

        Assert.Equal(1, ssh.ForgetPinnedKeys());
        Assert.Null(workspace.FindSshHostKey(client.Id, "gm.lab.test", 22));
        Assert.Equal(0, ssh.ForgetPinnedKeys());
    }

    [AvaloniaFact]
    public async Task A_host_that_presents_a_different_key_is_refused_without_asking()
    {
        var (window, client, _) = OpenLab();
        window.Clients.Workspace!.PinSshHostKey(client.Id, "gm.lab.test", 22, "ssh-ed25519", "SHA256:" + new string('B', 43));
        var ssh = window.Tools.Ssh;
        ssh.HostsText = "gm.lab.test";
        ssh.User = "admin";
        ssh.Password = "s3cret!";

        await ssh.RunAsync(new FakeSshClient(FakeSshClient.Honest));

        Assert.Null(ssh.PendingKey);
        Assert.False(ssh.KeyQuestionVisible);
        Assert.Contains("different host key", ssh.Output);
        Assert.DoesNotContain("s3cret!", ssh.Output);
        Assert.DoesNotContain("s3cret!", ssh.MessageText);
    }

    [AvaloniaFact]
    public async Task The_open_grids_saved_accounts_can_be_used_instead_of_typing_one()
    {
        var (window, client, _) = OpenLab(savedConnection: true);
        var workspace = window.Clients.Workspace!;
        foreach (var host in new[] { "gm.lab.test" })
            workspace.PinSshHostKey(client.Id, host, 22, "ssh-ed25519", FakeSshClient.KeyOf(new SshTarget(host, host)).Fingerprint);
        var ssh = window.Tools.Ssh;
        ssh.HostsText = "gm.lab.test";
        var fake = new FakeSshClient(FakeSshClient.Honest);

        Assert.Equal(SshAccountSource.GridReadOnly, ssh.Source); // offered because it exists
        Assert.Contains("read-only account ro-user", ssh.AccountNote);
        Assert.Contains("read-write account rw-user", ssh.AccountNote);

        await ssh.RunAsync(fake);
        Assert.Equal(("ro-user", "ro-pass"), (fake.Accounts[^1].User, fake.Accounts[^1].Password));

        ssh.Source = SshAccountSource.GridReadWrite;
        await ssh.RunAsync(fake);
        Assert.Equal(("rw-user", "rw-pass"), (fake.Accounts[^1].User, fake.Accounts[^1].Password));
    }

    [AvaloniaFact]
    public async Task A_missing_account_is_explained_and_nothing_connects()
    {
        var (window, _, _) = OpenLab(); // no saved connection
        var ssh = window.Tools.Ssh;
        ssh.HostsText = "gm.lab.test";
        var fake = new FakeSshClient(FakeSshClient.Honest);

        await ssh.RunAsync(fake); // typed account with nothing typed
        Assert.Contains("Type the user id and password", ssh.MessageText);

        ssh.Source = SshAccountSource.GridReadWrite;
        await ssh.RunAsync(fake);
        Assert.Contains("no saved read-write account", ssh.MessageText);

        Assert.Empty(fake.Calls);
    }

    [AvaloniaFact]
    public async Task Only_read_only_commands_run_unless_changes_are_allowed()
    {
        var ssh = Plain().Tools.Ssh;
        ssh.HostsText = "gm.lab.test";
        ssh.User = "admin";
        ssh.Password = "s3cret!";
        ssh.CommandsText = "show version\nset debug on";
        var fake = new FakeSshClient(FakeSshClient.Honest);

        await ssh.RunAsync(fake);
        Assert.Contains("\"set\" is not a read-only command", ssh.MessageText);
        Assert.Empty(fake.Calls);

        ssh.AllowChanges = true;
        var run = ssh.RunAsync(fake);
        await WaitForAsync(() => ssh.PendingKey is not null || fake.Calls.Count > 0);
        ssh.RejectPendingKey();
        await run;
        Assert.NotEmpty(fake.Calls);
        Assert.Equal(["show version", "set debug on"], fake.Calls[0].Commands.ToArray());
    }

    [AvaloniaFact]
    public async Task Nothing_to_run_or_nowhere_to_run_it_is_explained()
    {
        var ssh = Plain().Tools.Ssh;
        ssh.User = "admin";
        ssh.Password = "s3cret!";

        await ssh.RunAsync(new FakeSshClient(FakeSshClient.Honest));
        Assert.Contains("at least one host", ssh.MessageText);

        ssh.HostsText = "gm.lab.test";
        ssh.CommandsText = "  \n ";
        await ssh.RunAsync(new FakeSshClient(FakeSshClient.Honest));
        Assert.Contains("at least one command", ssh.MessageText);
    }

    [AvaloniaFact]
    public void The_open_grids_members_can_be_added_as_hosts()
    {
        var (window, _, _) = OpenLab();
        var ssh = window.Tools.Ssh;

        Assert.Equal(3, ssh.AddMembers());
        Assert.Contains("10.0.0.2  # ns1.lab.test", ssh.HostsText);
        Assert.Equal(0, ssh.AddMembers());
    }

    [AvaloniaFact]
    public async Task Stopping_while_a_key_question_is_waiting_ends_the_run()
    {
        var ssh = Plain().Tools.Ssh;
        ssh.HostsText = "gm.lab.test";
        ssh.User = "admin";
        ssh.Password = "s3cret!";

        var run = ssh.RunAsync(new FakeSshClient(FakeSshClient.Honest));
        await WaitForAsync(() => ssh.PendingKey is not null);

        ssh.Stop();
        await run;

        Assert.False(ssh.IsRunning);
        Assert.False(ssh.KeyQuestionVisible);
    }
}
