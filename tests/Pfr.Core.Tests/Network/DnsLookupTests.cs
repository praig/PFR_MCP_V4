using System.Net;
using System.Net.Sockets;
using Pfr.Core.Network;
using Pfr.Core.Nios.Model;

namespace Pfr.Core.Tests.Network;

public class DnsLookupTests
{
    private static DnsQueryOptions Options() => new() { Server = IPAddress.Parse("10.0.0.1"), Timeout = TimeSpan.FromSeconds(1) };

    private static readonly DnsQuestion Www = new("www.lab.test", DnsType.A);

    private static DnsExchange Answer(byte[] query, bool tcp, ushort flags = WireBuilder.Answer) =>
        FakeDnsTransport.Reply(query, flags, tcp, b => b.AnswerRecord(WireBuilder.Pointer(12), DnsType.A, 300, WireBuilder.Address("10.0.0.10")));

    [Fact]
    public async Task A_question_is_sent_and_the_answer_is_read()
    {
        var transport = new FakeDnsTransport((query, tcp, _) => Answer(query, tcp, WireBuilder.AuthoritativeAnswer));

        var response = await DnsLookup.QueryAsync(Www, Options(), transport);

        Assert.Equal("NOERROR", response.Status);
        Assert.Equal("10.0.0.10", Assert.Single(response.Message.Answers).Data);
        Assert.False(response.UsedTcp);
        Assert.False(response.RetriedOverTcp);
        Assert.Equal(response.Message.Id, DnsMessage.Parse(transport.Calls.Single().Query).Id);

        var sent = DnsMessage.Parse(transport.Calls.Single().Query);
        Assert.False(sent.Flags.Response);
        Assert.True(sent.Flags.RecursionDesired);
        Assert.Equal("www.lab.test.", Assert.Single(sent.Questions).Name);
        Assert.Equal(1232, sent.Edns!.UdpPayload);
        Assert.False(sent.Edns.DnssecOk);
    }

    [Fact]
    public async Task The_options_set_the_flags_of_the_question()
    {
        var transport = new FakeDnsTransport((query, tcp, _) => Answer(query, tcp));
        var options = Options() with { Dnssec = true, CheckingDisabled = true, Recurse = false, AuthenticData = true };

        await DnsLookup.QueryAsync(Www, options, transport);

        var sent = DnsMessage.Parse(transport.Calls.Single().Query);
        Assert.False(sent.Flags.RecursionDesired);
        Assert.True(sent.Flags.CheckingDisabled);
        Assert.True(sent.Flags.AuthenticData);
        Assert.True(sent.Edns!.DnssecOk);
    }

    [Fact]
    public async Task Without_edns_no_edns_record_is_sent()
    {
        var transport = new FakeDnsTransport((query, tcp, _) => Answer(query, tcp));

        await DnsLookup.QueryAsync(Www, Options() with { Edns = false }, transport);

        Assert.Null(DnsMessage.Parse(transport.Calls.Single().Query).Edns);
    }

    [Fact]
    public async Task A_chaos_class_question_keeps_its_class()
    {
        var transport = new FakeDnsTransport((query, tcp, _) => Answer(query, tcp));

        await DnsLookup.QueryAsync(new DnsQuestion("version.bind", DnsType.TXT, DnsClass.CH), Options(), transport);

        Assert.Equal(DnsClass.CH, DnsMessage.Parse(transport.Calls.Single().Query).Questions[0].Class);
    }

    [Fact]
    public async Task A_cut_short_udp_answer_is_asked_again_over_tcp()
    {
        var transport = new FakeDnsTransport((query, tcp, _) =>
            tcp ? Answer(query, true) : FakeDnsTransport.Reply(query, 0x8380, false)); // qr rd ra tc, no records

        var response = await DnsLookup.QueryAsync(Www, Options(), transport);

        Assert.Equal([false, true], transport.Calls.Select(c => c.Tcp).ToArray());
        Assert.True(response.RetriedOverTcp);
        Assert.True(response.UsedTcp);
        Assert.Single(response.Message.Answers);
        Assert.Equal(TimeSpan.FromMilliseconds(6), response.Elapsed); // both exchanges count
    }

    [Fact]
    public async Task With_udp_only_a_cut_short_answer_is_left_as_it_is()
    {
        var transport = new FakeDnsTransport((query, tcp, _) => FakeDnsTransport.Reply(query, 0x8380, tcp));

        var response = await DnsLookup.QueryAsync(Www, Options() with { UdpOnly = true }, transport);

        Assert.Single(transport.Calls);
        Assert.True(response.Message.Flags.Truncated);
        Assert.False(response.RetriedOverTcp);
    }

    [Fact]
    public async Task A_tcp_question_goes_over_tcp_once()
    {
        var transport = new FakeDnsTransport((query, tcp, _) => Answer(query, tcp));

        var response = await DnsLookup.QueryAsync(Www, Options() with { Tcp = true }, transport);

        Assert.Equal([true], transport.Calls.Select(c => c.Tcp).ToArray());
        Assert.True(response.UsedTcp);
        Assert.False(response.RetriedOverTcp);
    }

    [Fact]
    public async Task A_timeout_is_tried_again_and_then_reported()
    {
        var attempts = 0;
        var flaky = new FakeDnsTransport((query, tcp, _) =>
            ++attempts == 1 ? throw new DnsTimeoutException("no answer") : Answer(query, tcp));

        var response = await DnsLookup.QueryAsync(Www, Options(), flaky);

        Assert.Equal(2, flaky.Calls.Count);
        Assert.Single(response.Message.Answers);

        var silent = new FakeDnsTransport((_, _, _) => throw new DnsTimeoutException("no answer"));
        await Assert.ThrowsAsync<DnsTimeoutException>(() => DnsLookup.QueryAsync(Www, Options() with { Retries = 2 }, silent));
        Assert.Equal(3, silent.Calls.Count); // the first try and two more

        var once = new FakeDnsTransport((_, _, _) => throw new DnsTimeoutException("no answer"));
        await Assert.ThrowsAsync<DnsTimeoutException>(() => DnsLookup.QueryAsync(Www, Options() with { Retries = 0 }, once));
        Assert.Single(once.Calls);
    }

    [Fact]
    public async Task An_answer_to_a_different_question_is_refused()
    {
        var wrongId = new FakeDnsTransport((query, tcp, _) =>
        {
            var asked = DnsMessage.Parse(query);
            var bytes = new WireBuilder((ushort)(asked.Id ^ 0xFFFF), WireBuilder.Answer).Question("www.lab.test", DnsType.A).Build();
            return new DnsExchange(bytes, tcp, TimeSpan.Zero);
        });
        await Assert.ThrowsAsync<DnsQueryException>(() => DnsLookup.QueryAsync(Www, Options(), wrongId));

        var notAnAnswer = new FakeDnsTransport((query, tcp, _) => FakeDnsTransport.Reply(query, 0x0100, tcp)); // qr not set
        await Assert.ThrowsAsync<DnsQueryException>(() => DnsLookup.QueryAsync(Www, Options(), notAnAnswer));
    }

    [Fact]
    public async Task An_error_status_is_an_answer_not_a_failure()
    {
        var transport = new FakeDnsTransport((query, tcp, _) => FakeDnsTransport.Reply(query, 0x8183, tcp));

        var response = await DnsLookup.QueryAsync(Www, Options(), transport);

        Assert.Equal("NXDOMAIN", response.Status);
        Assert.Empty(response.Message.Answers);
    }

    [Fact]
    public async Task A_question_that_is_already_cancelled_is_not_sent()
    {
        var transport = new FakeDnsTransport((query, tcp, _) => Answer(query, tcp));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DnsLookup.QueryAsync(Www, Options(), transport, cancelled.Token));
        Assert.Empty(transport.Calls);
    }

    // ---- Real sockets, on the loopback address ----

    [Fact]
    public async Task A_real_server_answers_over_udp_and_a_cut_short_answer_is_repeated_over_tcp()
    {
        using var server = new LoopbackDnsServer((query, tcp) =>
        {
            var question = query.Questions[0];
            var big = question.Name.StartsWith("big", StringComparison.Ordinal);
            var flags = !tcp && big ? (ushort)0x8380 : WireBuilder.Answer; // the UDP answer for "big" is cut short
            var builder = new WireBuilder(query.Id, flags).Question(question.Name, question.Type, question.Class);
            if (!big) builder.AnswerRecord(WireBuilder.Pointer(12), DnsType.A, 300, WireBuilder.Address("10.0.0.10"));
            else if (tcp) builder.AnswerRecord(WireBuilder.Pointer(12), DnsType.TXT, 60, WireBuilder.CharacterString("the whole answer"));
            return builder.Build();
        });
        var options = new DnsQueryOptions { Server = IPAddress.Loopback, Port = server.Port, Timeout = TimeSpan.FromSeconds(5) };

        var small = await DnsLookup.QueryAsync(Www, options);
        Assert.Equal("10.0.0.10", Assert.Single(small.Message.Answers).Data);
        Assert.False(small.UsedTcp);

        var large = await DnsLookup.QueryAsync(new DnsQuestion("big.lab.test", DnsType.TXT), options);
        Assert.True(large.RetriedOverTcp);
        Assert.True(large.UsedTcp);
        Assert.Equal("\"the whole answer\"", Assert.Single(large.Message.Answers).Data);

        var forced = await DnsLookup.QueryAsync(Www, options with { Tcp = true });
        Assert.True(forced.UsedTcp);
        Assert.Equal("10.0.0.10", Assert.Single(forced.Message.Answers).Data);
    }

    [Fact]
    public async Task A_server_that_is_not_there_is_reported_in_plain_words()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop(); // now nothing listens on it

        var ex = await Assert.ThrowsAsync<DnsQueryException>(() =>
            DnsLookup.QueryAsync(Www, new DnsQueryOptions { Server = IPAddress.Loopback, Port = port, Tcp = true, Timeout = TimeSpan.FromSeconds(5) }));

        Assert.Contains("refused", ex.Message);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_times_out_with_a_clear_message()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); // takes the question and never answers
        var port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;

        var ex = await Assert.ThrowsAsync<DnsTimeoutException>(() => DnsLookup.QueryAsync(Www,
            new DnsQueryOptions { Server = IPAddress.Loopback, Port = port, Timeout = TimeSpan.FromMilliseconds(200), Retries = 0 }));

        Assert.Contains("No answer from 127.0.0.1", ex.Message);
    }
}

public class DigFormatterTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 28, 9, 5, 7, TimeSpan.Zero);

    private static async Task<DnsResponse> Ask(Func<byte[], bool, DnsExchange> respond, DnsQuestion? question = null, bool tcp = false)
    {
        var transport = new FakeDnsTransport((q, t, _) => respond(q, t));
        return await DnsLookup.QueryAsync(question ?? new DnsQuestion("www.lab.test", DnsType.A),
            new DnsQueryOptions { Server = IPAddress.Parse("10.0.0.1"), Tcp = tcp }, transport);
    }

    private static DnsExchange Full(byte[] query, bool tcp) => FakeDnsTransport.Reply(query, WireBuilder.AuthoritativeAnswer, tcp, b => b
        .AnswerRecord(WireBuilder.Pointer(12), DnsType.A, 300, WireBuilder.Address("10.0.0.10"))
        .AuthorityRecord(WireBuilder.Name("lab.test"), DnsType.NS, 3600, WireBuilder.Name("ns1.lab.test"))
        .AdditionalRecord(WireBuilder.Name("ns1.lab.test"), DnsType.A, 3600, WireBuilder.Address("10.0.0.1"))
        .Opt(4096, dnssecOk: true));

    [Fact]
    public async Task The_output_has_the_sections_dig_users_expect()
    {
        var response = await Ask(Full);

        var text = DigFormatter.Format(response, when: When);

        Assert.Contains("; <<>> PFR dig <<>> @10.0.0.1 www.lab.test A", text);
        Assert.Contains($";; ->>HEADER<<- opcode: QUERY, status: NOERROR, id: {response.Message.Id}", text);
        Assert.Contains(";; flags: qr aa rd ra; QUERY: 1, ANSWER: 1, AUTHORITY: 1, ADDITIONAL: 2", text);
        Assert.Contains(";; OPT PSEUDOSECTION:\n; EDNS: version: 0, flags: do; udp: 4096", text.Replace("\r\n", "\n"));
        Assert.Contains(";; QUESTION SECTION:", text);
        Assert.Contains(";www.lab.test.\t\tIN\tA", text);
        Assert.Contains(";; ANSWER SECTION:", text);
        Assert.Contains("www.lab.test.\t300\tIN\tA\t10.0.0.10", text);
        Assert.Contains(";; AUTHORITY SECTION:", text);
        Assert.Contains("lab.test.\t3600\tIN\tNS\tns1.lab.test.", text);
        Assert.Contains(";; ADDITIONAL SECTION:", text);
        Assert.Contains("ns1.lab.test.\t3600\tIN\tA\t10.0.0.1", text);
        Assert.Contains(";; Query time: 3 msec", text);
        Assert.Contains(";; SERVER: 10.0.0.1#53(10.0.0.1) (UDP)", text);
        Assert.Contains(";; WHEN: Wed Oct 28 09:05:07 2026", text);
        Assert.Contains($";; MSG SIZE  rcvd: {response.Size}", text);
    }

    [Fact]
    public async Task Sections_with_nothing_in_them_are_left_out()
    {
        var response = await Ask((q, tcp) => FakeDnsTransport.Reply(q, 0x8183, tcp)); // NXDOMAIN, no records

        var text = DigFormatter.Format(response, when: When);

        Assert.Contains("status: NXDOMAIN", text);
        Assert.DoesNotContain("ANSWER SECTION", text);
        Assert.DoesNotContain("AUTHORITY SECTION", text);
        Assert.DoesNotContain("OPT PSEUDOSECTION", text); // the server sent no EDNS record
    }

    [Fact]
    public async Task The_short_form_is_just_the_data_of_each_answer()
    {
        var response = await Ask((q, tcp) => FakeDnsTransport.Reply(q, WireBuilder.Answer, tcp, b => b
            .AnswerRecord(WireBuilder.Pointer(12), DnsType.A, 60, WireBuilder.Address("10.0.0.10"))
            .AnswerRecord(WireBuilder.Pointer(12), DnsType.A, 60, WireBuilder.Address("10.0.0.11"))));

        Assert.Equal("10.0.0.10\n10.0.0.11\n", DigFormatter.Format(response, shortForm: true));
    }

    [Fact]
    public async Task A_question_that_went_over_tcp_says_so_and_a_retried_one_says_why()
    {
        var tcp = await Ask(Full, tcp: true);
        Assert.Contains("(TCP)", DigFormatter.Format(tcp, when: When));

        var retried = await Ask((q, isTcp) => isTcp ? Full(q, true) : FakeDnsTransport.Reply(q, 0x8380, false));
        var text = DigFormatter.Format(retried, when: When);
        Assert.Contains(";; Truncated, retrying in TCP mode.", text);
        Assert.Contains("(TCP)", text);
    }

    [Fact]
    public async Task A_question_in_another_class_shows_the_class_in_the_command_line()
    {
        var response = await Ask((q, tcp) => FakeDnsTransport.Reply(q, WireBuilder.Answer, tcp),
            new DnsQuestion("version.bind", DnsType.TXT, DnsClass.CH));

        Assert.Contains("; <<>> PFR dig <<>> @10.0.0.1 version.bind TXT CH", DigFormatter.Format(response, when: When));
    }
}

public class DnsCompareTests
{
    private static readonly DnsQuestion Question = new("www.lab.test", DnsType.A);

    private static DnsServerChoice Server(string label, string address) => new(label, IPAddress.Parse(address));

    private static DnsQueryOptions Template() => new() { Server = IPAddress.Loopback, Timeout = TimeSpan.FromSeconds(1), Retries = 0 };

    private static DnsExchange Records(byte[] query, ushort flags, params string[] addresses) =>
        FakeDnsTransport.Reply(query, flags, false, b =>
        {
            foreach (var address in addresses) b.AnswerRecord(WireBuilder.Pointer(12), DnsType.A, 300, WireBuilder.Address(address));
        });

    [Fact]
    public async Task Servers_that_give_the_same_records_agree_whatever_the_order_or_ttl()
    {
        var transport = new PerServerDnsTransport((server, query, _) =>
            server.ToString() == "10.0.0.2"
                ? Records(query, WireBuilder.Answer, "10.0.0.11", "10.0.0.10")   // the same two, the other way round
                : Records(query, WireBuilder.AuthoritativeAnswer, "10.0.0.10", "10.0.0.11"));

        var comparison = await DnsCompare.RunAsync(Question,
            [Server("gm", "10.0.0.1"), Server("ns1", "10.0.0.2"), Server("ns2", "10.0.0.3")], Template(), transport);

        Assert.True(comparison.AllAgree);
        Assert.Single(comparison.Groups);
        Assert.Equal("gm,ns1,ns2", string.Join(",", comparison.Rows.Select(r => r.Server.Label)));
        Assert.Contains("All 3 servers give the same answer.", DnsCompare.Text(comparison));
    }

    [Fact]
    public async Task A_server_that_gives_a_different_answer_is_picked_out()
    {
        var transport = new PerServerDnsTransport((server, query, _) =>
            Records(query, WireBuilder.Answer, server.ToString() == "10.0.0.3" ? "10.0.0.99" : "10.0.0.10"));

        var comparison = await DnsCompare.RunAsync(Question,
            [Server("gm", "10.0.0.1"), Server("ns1", "10.0.0.2"), Server("ns2", "10.0.0.3")], Template(), transport);

        Assert.False(comparison.AllAgree);
        Assert.Equal(2, comparison.Groups.Count);
        var text = DnsCompare.Text(comparison);
        Assert.Contains("The servers do not agree: 2 different answers.", text);
        Assert.Contains("1. gm, ns1 - NOERROR: www.lab.test. A 10.0.0.10", text);
        Assert.Contains("2. ns2 - NOERROR: www.lab.test. A 10.0.0.99", text);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_counts_as_a_difference_and_says_why()
    {
        var transport = new PerServerDnsTransport((server, query, _) =>
            server.ToString() == "10.0.0.2" ? throw new DnsTimeoutException("No answer from 10.0.0.2 within 1 seconds.")
                                            : Records(query, WireBuilder.Answer, "10.0.0.10"));

        var comparison = await DnsCompare.RunAsync(Question, [Server("gm", "10.0.0.1"), Server("ns1", "10.0.0.2")], Template(), transport);

        Assert.False(comparison.AllAgree);
        var failed = comparison.Rows.Single(r => r.Server.Label == "ns1");
        Assert.Equal("No answer from 10.0.0.2 within 1 seconds.", failed.Error);
        Assert.Contains("ns1 (10.0.0.2)  No answer from 10.0.0.2 within 1 seconds.", DnsCompare.Text(comparison));
    }

    [Fact]
    public async Task A_different_status_is_a_difference()
    {
        var transport = new PerServerDnsTransport((server, query, _) =>
            server.ToString() == "10.0.0.2" ? FakeDnsTransport.Reply(query, 0x8183, false) : Records(query, WireBuilder.Answer, "10.0.0.10"));

        var comparison = await DnsCompare.RunAsync(Question, [Server("gm", "10.0.0.1"), Server("ns1", "10.0.0.2")], Template(), transport);

        Assert.False(comparison.AllAgree);
        Assert.Contains("ns1 - NXDOMAIN, no records", DnsCompare.Text(comparison));
    }

    [Fact]
    public async Task The_authoritative_flag_and_lowest_ttl_are_recorded_for_each_server()
    {
        var transport = new PerServerDnsTransport((_, query, _) => Records(query, WireBuilder.AuthoritativeAnswer, "10.0.0.10"));

        var comparison = await DnsCompare.RunAsync(Question, [Server("gm", "10.0.0.1")], Template(), transport);

        var row = Assert.Single(comparison.Rows);
        Assert.True(row.Authoritative);
        Assert.Equal(300u, row.LowestTtl);
        Assert.Contains("Only one server was asked.", DnsCompare.Text(comparison));
    }

    [Fact]
    public async Task Every_server_is_asked_the_same_question()
    {
        var transport = new PerServerDnsTransport((_, query, _) => Records(query, WireBuilder.Answer, "10.0.0.10"));

        await DnsCompare.RunAsync(Question, [Server("a", "10.0.0.1"), Server("b", "10.0.0.2")], Template() with { Dnssec = true }, transport);

        Assert.Equal(["10.0.0.1", "10.0.0.2"], transport.Asked.Select(a => a.ToString()).Order().ToArray());
    }

    [Fact]
    public void The_dns_servers_of_a_grid_are_the_members_that_run_dns()
    {
        var inventory = new GridInventory();
        var gm = new GridMember { Oid = 0, HostName = "gm.lab.test", Ipv4Address = "10.0.0.1" };
        gm.Services.AddRange(["DNS", "DHCP"]);
        var ntp = new GridMember { Oid = 1, HostName = "ntp.lab.test", Ipv4Address = "10.0.0.2" };
        ntp.Services.Add("NTP");
        var v6 = new GridMember { Oid = 2, HostName = "v6.lab.test", Ipv6Address = "2001:db8::53" };
        v6.Services.Add("DNS");
        var noAddress = new GridMember { Oid = 3, HostName = "none.lab.test" };
        noAddress.Services.Add("DNS");
        inventory.Members.AddRange([gm, ntp, v6, noAddress]);

        var servers = DnsServers.FromMembers(inventory);

        Assert.Equal("gm.lab.test=10.0.0.1,v6.lab.test=2001:db8::53", string.Join(",", servers.Select(s => $"{s.Label}={s.Address}")));
    }
}

public class DnsBatchTests
{
    [Fact]
    public void Names_types_and_addresses_are_read_one_per_line()
    {
        var (items, problems) = DnsBatch.Parse("""
            # a comment
            www.lab.test
            mail.lab.test MX
            10.0.0.20

            lab.test  txt   # trailing comment
            """);

        Assert.Empty(problems);
        Assert.Equal(
            ["www.lab.test A", "mail.lab.test MX", "20.0.0.10.in-addr.arpa. PTR", "lab.test TXT"],
            items.Select(i => $"{i.Question.Name} {DnsTypes.Name(i.Question.Type)}").ToArray());
        Assert.Equal("10.0.0.20", items[2].Written); // an address is shown as it was written, though asked in reverse
    }

    [Fact]
    public void A_line_with_an_unknown_type_is_reported_and_skipped()
    {
        var (items, problems) = DnsBatch.Parse("www.lab.test\nbad.lab.test NOPE\nok.lab.test AAAA");

        Assert.Equal(2, items.Count);
        Assert.Equal("Line 2: \"NOPE\" is not a record type.", Assert.Single(problems));
    }

    [Fact]
    public void The_default_type_can_be_changed()
    {
        var (items, _) = DnsBatch.Parse("a.lab.test\nb.lab.test MX", DnsType.AAAA);

        Assert.Equal([DnsType.AAAA, DnsType.MX], items.Select(i => i.Question.Type).ToArray());
    }

    [Fact]
    public async Task Every_line_is_asked_and_failures_are_kept_with_their_line()
    {
        var transport = new FakeDnsTransport((query, tcp, _) =>
        {
            var name = DnsMessage.Parse(query).Questions[0].Name;
            return name.StartsWith("slow", StringComparison.Ordinal)
                ? throw new DnsTimeoutException("No answer from 10.0.0.1 within 1 seconds.")
                : FakeDnsTransport.Reply(query, WireBuilder.Answer, tcp, b => b.AnswerRecord(WireBuilder.Pointer(12), DnsType.A, 60, WireBuilder.Address("10.0.0.10")));
        });
        var (items, _) = DnsBatch.Parse("www.lab.test\nslow.lab.test\nmail.lab.test");

        var results = await DnsBatch.RunAsync(items, new DnsQueryOptions { Server = IPAddress.Parse("10.0.0.1"), Retries = 0 }, transport);

        Assert.Equal(3, results.Count);
        Assert.Equal("www.lab.test,slow.lab.test,mail.lab.test", string.Join(",", results.Select(r => r.Item.Written)));
        Assert.Null(results[0].Error);
        Assert.Equal("No answer from 10.0.0.1 within 1 seconds.", results[1].Error);
        Assert.Null(results[2].Error);

        var text = DnsBatch.Text(results);
        Assert.Contains("www.lab.test", text);
        Assert.Contains("NOERROR", text);
        Assert.Contains("error", text);
        Assert.Contains("10.0.0.10", text);
    }

    [Fact]
    public void Nothing_to_look_up_says_so()
    {
        Assert.Equal("Nothing to look up.\n", DnsBatch.Text([]));
    }
}
