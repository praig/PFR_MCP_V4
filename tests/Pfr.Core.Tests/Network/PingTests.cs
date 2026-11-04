using System.Net;
using System.Net.Sockets;
using Pfr.Core.Network;
using Pfr.Core.Nios.Model;

namespace Pfr.Core.Tests.Network;

public class PingTests
{
    private static readonly PingSettings Fast = new() { Count = 4, Interval = TimeSpan.Zero, Timeout = TimeSpan.FromMilliseconds(100) };

    private static PingAttempt Reply(int sequence, double milliseconds) =>
        new(sequence, PingOutcome.Success, TimeSpan.FromMilliseconds(milliseconds), null, null);

    private static PingAttempt Timeout(int sequence) => new(sequence, PingOutcome.TimedOut, null, null, null);

    private static Task<IPAddress[]> Known(string host, CancellationToken ct) => host switch
    {
        "gm.lab.test" => Task.FromResult(new[] { IPAddress.Parse("2001:db8::1"), IPAddress.Parse("10.0.0.1") }),
        "v6only.lab.test" => Task.FromResult(new[] { IPAddress.Parse("2001:db8::6") }),
        _ => throw new SocketException((int)SocketError.HostNotFound),
    };

    [Fact]
    public async Task Every_reply_gives_the_loss_and_the_times()
    {
        var pinger = new FakePinger((_, n) => Reply(n, n)); // 1, 2, 3 and 4 ms

        var results = await PingRunner.RunAsync([new PingTarget("gm", "10.0.0.1")], Fast, pinger);

        var result = Assert.Single(results);
        Assert.Equal(4, result.Sent);
        Assert.Equal(4, result.Received);
        Assert.Equal(0, result.LossPercent);
        Assert.Equal(TimeSpan.FromMilliseconds(1), result.Min);
        Assert.Equal(TimeSpan.FromMilliseconds(4), result.Max);
        Assert.Equal(TimeSpan.FromMilliseconds(2.5), result.Average);
        Assert.Equal("up", result.Status);
        Assert.True(result.Finished);
        Assert.Equal("10.0.0.1", result.Address);
    }

    [Fact]
    public async Task Some_lost_pings_are_a_percentage()
    {
        var pinger = new FakePinger((_, n) => n % 2 == 1 ? Reply(n, 5) : Timeout(n));

        var result = Assert.Single(await PingRunner.RunAsync([new PingTarget("gm", "10.0.0.1")], Fast, pinger));

        Assert.Equal(2, result.Received);
        Assert.Equal(50, result.LossPercent);
        Assert.Equal("50% loss", result.Status);
    }

    [Fact]
    public async Task No_reply_at_all_is_down_unreachable_or_an_error()
    {
        var down = Assert.Single(await PingRunner.RunAsync([new PingTarget("a", "10.0.0.1")], Fast, new FakePinger((_, n) => Timeout(n))));
        Assert.Equal("down", down.Status);
        Assert.Null(down.Min);
        Assert.Equal("-", PingReport.Rtt(down.Min));

        var unreachable = Assert.Single(await PingRunner.RunAsync([new PingTarget("a", "10.0.0.1")], Fast,
            new FakePinger((_, n) => new PingAttempt(n, PingOutcome.Unreachable, null, "DestinationHostUnreachable", null))));
        Assert.Equal("unreachable", unreachable.Status);

        var error = Assert.Single(await PingRunner.RunAsync([new PingTarget("a", "10.0.0.1")], Fast,
            new FakePinger((_, n) => new PingAttempt(n, PingOutcome.Error, null, "no permission", null))));
        Assert.Equal("error: no permission", error.Status);
    }

    [Fact]
    public async Task A_name_is_looked_up_and_ipv4_is_preferred_unless_asked_otherwise()
    {
        var pinger = new FakePinger((_, n) => Reply(n, 1));

        var v4 = Assert.Single(await PingRunner.RunAsync([new PingTarget("gm", "gm.lab.test")], Fast, pinger, Known));
        Assert.Equal("10.0.0.1", v4.Address);

        var v6 = Assert.Single(await PingRunner.RunAsync([new PingTarget("gm", "gm.lab.test")], Fast with { PreferIpv6 = true }, pinger, Known));
        Assert.Equal("2001:db8::1", v6.Address);

        var only = Assert.Single(await PingRunner.RunAsync([new PingTarget("v6", "v6only.lab.test")], Fast, pinger, Known));
        Assert.Equal("2001:db8::6", only.Address); // the only address there is is used
    }

    [Fact]
    public async Task An_address_is_pinged_without_any_lookup()
    {
        var pinger = new FakePinger((_, n) => Reply(n, 1));

        var results = await PingRunner.RunAsync([new PingTarget("x", "10.0.0.7")], Fast, pinger,
            (_, _) => throw new InvalidOperationException("an address must not be looked up"));

        Assert.Equal("10.0.0.7", Assert.Single(results).Address);
    }

    [Fact]
    public async Task A_name_that_cannot_be_found_is_reported_and_the_others_still_run()
    {
        var pinger = new FakePinger((_, n) => Reply(n, 1));

        var results = await PingRunner.RunAsync(
            [new PingTarget("bad", "nowhere.lab.test"), new PingTarget("gm", "gm.lab.test")], Fast, pinger, Known);

        Assert.Equal("cannot resolve: name not found", results[0].Status);
        Assert.Empty(results[0].Attempts);
        Assert.True(results[0].Finished);
        Assert.Equal("up", results[1].Status);
    }

    [Fact]
    public async Task Results_come_back_in_the_order_of_the_targets_whatever_order_they_finished_in()
    {
        var pinger = new FakePinger((a, n) => Reply(n, 1));
        var targets = Enumerable.Range(1, 30).Select(i => new PingTarget($"h{i}", $"10.0.0.{i}")).ToList();

        var results = await PingRunner.RunAsync(targets, Fast with { Count = 2, MaxParallel = 5 }, pinger);

        Assert.Equal(targets.Select(t => t.Label), results.Select(r => r.Target.Label));
        Assert.All(results, r => Assert.Equal(2, r.Sent));
        Assert.Equal(60, pinger.Calls.Count);
    }

    [Fact]
    public async Task Progress_is_reported_as_each_ping_completes()
    {
        var reports = new List<PingResult>();
        var progress = new SyncProgress<PingResult>(reports.Add);

        await PingRunner.RunAsync([new PingTarget("gm", "10.0.0.1")], Fast, new FakePinger((_, n) => Reply(n, 1)), progress: progress);

        // The address once it is known, then after each of the four pings.
        Assert.Equal([0, 1, 2, 3, 4], reports.Select(r => r.Sent).ToArray());
        Assert.False(reports[3].Finished);
        Assert.True(reports[4].Finished);
    }

    [Fact]
    public async Task Cancelling_stops_the_run_and_gives_back_what_was_measured()
    {
        using var cancel = new CancellationTokenSource();
        var pinger = new FakePinger((_, n) =>
        {
            if (n == 2) cancel.Cancel();
            return Reply(n, 1);
        });

        var results = await PingRunner.RunAsync([new PingTarget("gm", "10.0.0.1")],
            Fast with { Count = 10, Interval = TimeSpan.FromMilliseconds(20) }, pinger, ct: cancel.Token);

        var result = Assert.Single(results);
        Assert.InRange(result.Sent, 2, 3);
        Assert.False(result.Finished);
    }

    [Fact]
    public void Targets_can_be_typed_in_any_separator_with_comments_and_repeats_dropped()
    {
        var targets = PingTargets.Parse("gm.lab.test, 10.0.0.2  10.0.0.3;10.0.0.4\n# a comment line\nGM.lab.test # again\n\n  10.0.0.5");

        Assert.Equal(["gm.lab.test", "10.0.0.2", "10.0.0.3", "10.0.0.4", "10.0.0.5"], targets.Select(t => t.Host).ToArray());
        Assert.Empty(PingTargets.Parse(null));
    }

    [Fact]
    public void A_comment_after_a_single_host_is_its_label()
    {
        var targets = PingTargets.Parse("10.0.0.1  # gm.lab.test\n10.0.0.2\n10.0.0.3, 10.0.0.4  # two hosts, so no label");

        Assert.Equal(
            ["gm.lab.test=10.0.0.1", "10.0.0.2=10.0.0.2", "10.0.0.3=10.0.0.3", "10.0.0.4=10.0.0.4"],
            targets.Select(t => $"{t.Label}={t.Host}").ToArray());
    }

    [Fact]
    public void Every_member_of_a_grid_is_a_target_and_each_node_of_an_ha_pair_too()
    {
        var inventory = new GridInventory();
        var gm = new GridMember { Oid = 0, HostName = "gm.lab.test", Ipv4Address = "10.0.0.1" };
        gm.Nodes.Add(new GridNode { Position = 0, Ipv4Address = "10.0.0.1" });
        var ha = new GridMember { Oid = 1, HostName = "ha.lab.test", Ipv4Address = "10.0.0.10", HaEnabled = true };
        ha.Nodes.Add(new GridNode { Position = 0, Ipv4Address = "10.0.0.11" });
        ha.Nodes.Add(new GridNode { Position = 1, Ipv4Address = "10.0.0.12" });
        var noAddress = new GridMember { Oid = 2, HostName = "bare.lab.test" };
        inventory.Members.AddRange([gm, ha, noAddress]);

        var targets = PingTargets.FromMembers(inventory);

        Assert.Equal(
            ["gm.lab.test=10.0.0.1", "ha.lab.test=10.0.0.10", "ha.lab.test (node 1)=10.0.0.11", "ha.lab.test (node 2)=10.0.0.12", "bare.lab.test=bare.lab.test"],
            targets.Select(t => $"{t.Label}={t.Host}").ToArray());
        Assert.Equal(3, PingTargets.FromMembers(inventory, includeNodes: false).Count);
    }

    [Fact]
    public async Task The_report_is_a_table_with_one_line_per_target()
    {
        var results = await PingRunner.RunAsync(
            [new PingTarget("gm.lab.test", "10.0.0.1"), new PingTarget("down.lab.test", "10.0.0.9")], Fast,
            new FakePinger((address, n) => address.ToString() == "10.0.0.1" ? Reply(n, 0.4) : Timeout(n)));

        var lines = PingReport.Text(results).TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Target", lines[0]);
        Assert.Contains("gm.lab.test", lines[1]);
        Assert.Contains("10.0.0.1", lines[1]);
        Assert.Contains("<1 ms", lines[1]);
        Assert.EndsWith("up", lines[1]);
        Assert.Contains("100%", lines[2]);
        Assert.EndsWith("down", lines[2]);
        Assert.Equal("Nothing to ping.\n", PingReport.Text([]));
    }

    [Theory]
    [InlineData(0.2, "<1 ms")]
    [InlineData(12, "12 ms")]
    [InlineData(250.4, "250 ms")]
    public void Round_trip_times_are_shown_in_whole_milliseconds(double milliseconds, string expected)
    {
        Assert.Equal(expected, PingReport.Rtt(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public async Task The_system_pinger_can_ping_this_computer()
    {
        // Real ICMP: allowed on most machines, refused on some locked-down ones. Either way it must answer, not hang or throw.
        var attempt = await new SystemPinger().PingAsync(IPAddress.Loopback, 1, new PingSettings { Timeout = TimeSpan.FromSeconds(2) }, default);

        Assert.True(attempt.Outcome is PingOutcome.Success or PingOutcome.Error, $"unexpected outcome {attempt.Outcome}: {attempt.Detail}");
    }
}

/// <summary>Progress that reports at once on the calling thread (Progress&lt;T&gt; posts later, which is no use in a test).</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value)
    {
        lock (this) report(value);
    }
}
