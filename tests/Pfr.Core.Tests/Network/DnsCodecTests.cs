using System.Net;
using Pfr.Core.Ipam;
using Pfr.Core.Network;

namespace Pfr.Core.Tests.Network;

public class DnsNamesTests
{
    [Theory]
    [InlineData("www.lab.test", "www.lab.test.")]
    [InlineData("www.lab.test.", "www.lab.test.")]
    [InlineData(".", ".")]
    [InlineData("", ".")]
    public void A_name_survives_being_written_and_read(string written, string expected)
    {
        var wire = DnsNames.Encode(written);

        var position = 0;
        Assert.Equal(expected, DnsNames.Decode(wire, ref position));
        Assert.Equal(wire.Length, position);
    }

    [Fact]
    public void Labels_are_length_prefixed_and_the_name_ends_with_a_zero()
    {
        var wire = DnsNames.Encode("example.com");

        Assert.Equal([7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0], wire);
    }

    [Fact]
    public void A_dot_inside_a_label_is_escaped_and_a_decimal_escape_is_a_byte()
    {
        var withDot = DnsNames.Encode("a\\.b.example");
        var position = 0;
        Assert.Equal(3, withDot[0]);                                     // "a.b" is one label of three bytes
        Assert.Equal("a\\.b.example.", DnsNames.Decode(withDot, ref position));

        var byDecimal = DnsNames.Encode("\\065bc.test");                 // \065 is "A"
        position = 0;
        Assert.Equal("Abc.test.", DnsNames.Decode(byDecimal, ref position));
    }

    [Fact]
    public void Bytes_that_are_not_printable_are_shown_as_decimal_escapes()
    {
        var wire = new byte[] { 3, 0x01, (byte)'a', 0x7F, 0 };

        var position = 0;
        Assert.Equal("\\001a\\127.", DnsNames.Decode(wire, ref position));
    }

    [Fact]
    public void An_accented_name_is_sent_in_its_ascii_form()
    {
        var position = 0;
        Assert.Equal("xn--bcher-kva.example.", DnsNames.Decode(DnsNames.Encode("bücher.example"), ref position));
    }

    [Theory]
    [InlineData("a..b")]
    [InlineData(".leading")]
    [InlineData("ends-with-backslash\\")]
    public void A_name_with_an_empty_label_or_a_dangling_backslash_is_refused(string name)
    {
        Assert.Throws<ArgumentException>(() => DnsNames.Encode(name));
    }

    [Fact]
    public void Labels_and_names_have_a_maximum_length()
    {
        Assert.Throws<ArgumentException>(() => DnsNames.Encode(new string('a', 64) + ".test"));
        Assert.Throws<ArgumentException>(() => DnsNames.Encode(string.Join('.', Enumerable.Repeat(new string('a', 63), 5))));
        DnsNames.Encode(new string('a', 63) + ".test"); // 63 is fine
    }

    [Fact]
    public void A_compression_pointer_is_followed_and_the_position_moves_past_the_pointer_only()
    {
        // "lab.test" at 0..9, then "www" followed by a pointer back to offset 0.
        var message = WireBuilder.Concat(DnsNames.Encode("lab.test"), [3, (byte)'w', (byte)'w', (byte)'w'], WireBuilder.Pointer(0));

        var position = 10;
        Assert.Equal("www.lab.test.", DnsNames.Decode(message, ref position));
        Assert.Equal(message.Length, position);
    }

    [Fact]
    public void A_pointer_that_loops_or_points_outside_the_message_is_refused()
    {
        var loop = WireBuilder.Pointer(0);
        var position = 0;
        Assert.Throws<DnsQueryException>(() =>
        {
            var p = 0;
            return DnsNames.Decode(loop, ref p);
        });

        var outside = WireBuilder.Pointer(500);
        Assert.Throws<DnsQueryException>(() =>
        {
            var p = 0;
            return DnsNames.Decode(outside, ref p);
        });
        Assert.Equal(0, position);
    }

    [Fact]
    public void A_name_that_runs_past_the_end_is_refused()
    {
        Assert.Throws<DnsQueryException>(() =>
        {
            var p = 0;
            return DnsNames.Decode([5, (byte)'a', (byte)'b'], ref p);
        });
    }

    [Fact]
    public void The_reverse_name_of_an_ipv4_address()
    {
        Assert.Equal("20.0.0.10.in-addr.arpa.", DnsNames.ReverseName(IPAddress.Parse("10.0.0.20")));
    }

    [Fact]
    public void The_reverse_name_of_an_ipv6_address_has_a_label_per_nibble()
    {
        var name = DnsNames.ReverseName(IPAddress.Parse("2001:db8::20"));

        Assert.StartsWith("0.2.0.0.", name);
        Assert.EndsWith("8.b.d.0.1.0.0.2.ip6.arpa.", name);
        Assert.Equal(32 + 2, name.TrimEnd('.').Split('.').Length);
    }

    [Theory]
    [InlineData("10.0.0.20")]
    [InlineData("192.168.1.1")]
    [InlineData("2001:db8::20")]
    [InlineData("fe80::1")]
    public void A_reverse_name_reads_back_as_the_address_it_was_made_from(string address)
    {
        var reverse = DnsNames.ReverseName(IPAddress.Parse(address));

        Assert.Equal(address, IpamKeys.AddressFromReverseName(reverse));
    }

    [Theory]
    [InlineData("www.lab.test", "www.lab.test.")]
    [InlineData("www.lab.test.", "www.lab.test.")]
    [InlineData(" . ", ".")]
    public void Absolute_puts_a_dot_on_the_end(string name, string expected)
    {
        Assert.Equal(expected, DnsNames.Absolute(name));
    }
}

public class DnsMessageTests
{
    [Fact]
    public void A_flag_pattern_reads_and_writes_back_the_same()
    {
        var flags = DnsFlags.FromWire(0x8583); // qr aa rd ra, NXDOMAIN

        Assert.True(flags.Response);
        Assert.True(flags.Authoritative);
        Assert.True(flags.RecursionDesired);
        Assert.True(flags.RecursionAvailable);
        Assert.False(flags.Truncated);
        Assert.Equal(3, flags.Rcode);
        Assert.Equal("qr aa rd ra", flags.Names);
        Assert.Equal((ushort)0x8583, flags.ToWire());
    }

    [Fact]
    public void Every_flag_has_its_own_bit()
    {
        var all = new DnsFlags(true, 5, true, true, true, true, true, true, 9);

        Assert.Equal("qr aa tc rd ra ad cd", all.Names);
        var back = DnsFlags.FromWire(all.ToWire());
        Assert.Equal(all, back);
    }

    [Fact]
    public void A_question_is_written_in_the_standard_layout()
    {
        var message = new DnsMessage { Id = 0x1234, Flags = new DnsFlags(false, 0, false, false, true, false, false, false, 0) };
        message.Questions.Add(new DnsQuestion("example.com", DnsType.A));

        var expected = new byte[]
        {
            0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0,                       // id, rd, one question
            7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0,
            0, 1, 0, 1,                                                           // A, IN
        };
        Assert.Equal(expected, message.Encode());
    }

    [Fact]
    public void The_edns_record_carries_the_udp_size_and_the_dnssec_ok_flag()
    {
        var message = new DnsMessage { Id = 1 };
        message.Questions.Add(new DnsQuestion("a.test", DnsType.A));
        message.Edns = new DnsEdns(1232, 0, 0, true, []);

        var wire = message.Encode();

        Assert.Equal([0, 1], wire[10..12]);                                       // one additional record
        Assert.Equal([0, 0x00, 0x29, 0x04, 0xD0, 0, 0, 0x80, 0, 0, 0], wire[^11..]); // root, OPT, 1232, DO, no options
    }

    [Fact]
    public void An_answer_is_read_with_names_through_compression_pointers()
    {
        var wire = new WireBuilder(0xBEEF, WireBuilder.AuthoritativeAnswer)
            .Question("www.lab.test", DnsType.A)
            .AnswerRecord(WireBuilder.Pointer(12), DnsType.A, 300, WireBuilder.Address("10.0.0.10"))
            .Opt(4096, dnssecOk: true)
            .Build();

        var message = DnsMessage.Parse(wire);

        Assert.Equal(0xBEEF, message.Id);
        Assert.True(message.Flags.Authoritative);
        Assert.Equal("www.lab.test.", Assert.Single(message.Questions).Name);
        var answer = Assert.Single(message.Answers);
        Assert.Equal("www.lab.test.", answer.Name);
        Assert.Equal(DnsType.A, answer.Type);
        Assert.Equal(300u, answer.Ttl);
        Assert.Equal("10.0.0.10", answer.Data);
        Assert.Empty(message.Additionals);               // the EDNS record is not an ordinary record
        Assert.Equal(1, message.AdditionalCount);        // but the header counted it
        Assert.NotNull(message.Edns);
        Assert.Equal(4096, message.Edns!.UdpPayload);
        Assert.True(message.Edns.DnssecOk);
    }

    [Fact]
    public void An_extended_response_code_is_joined_to_the_one_in_the_header()
    {
        // BADVERS is 16: the high bits (1) are in the EDNS record's ttl, the low bits (0) in the header.
        var wire = new WireBuilder(1, WireBuilder.Answer).Question("a.test", DnsType.A).Build();
        var withExtension = wire.Concat(new byte[] { 0, 0, 0x29, 0x04, 0xD0, 1, 0, 0, 0, 0, 0 }).ToArray();
        withExtension[11] = 1; // one additional

        var message = DnsMessage.Parse(withExtension);

        Assert.Equal(16, message.FullRcode);
        Assert.Equal("BADVERS", DnsTypes.RcodeName(message.FullRcode));
    }

    [Fact]
    public void A_message_that_is_cut_short_is_refused()
    {
        Assert.Throws<DnsQueryException>(() => DnsMessage.Parse(new byte[5]));

        var whole = new WireBuilder(1, WireBuilder.Answer).Question("a.test", DnsType.A)
            .AnswerRecord(WireBuilder.Pointer(12), DnsType.A, 60, WireBuilder.Address("10.0.0.1")).Build();
        Assert.Throws<DnsQueryException>(() => DnsMessage.Parse(whole[..^2]));
    }

    [Fact]
    public void A_header_that_promises_more_than_the_message_holds_is_refused()
    {
        var wire = new WireBuilder(1, WireBuilder.Answer).Question("a.test", DnsType.A).Build();
        wire[7] = 3; // three answers declared, none present

        Assert.Throws<DnsQueryException>(() => DnsMessage.Parse(wire));
    }

    [Fact]
    public void The_truncated_flag_is_read()
    {
        var wire = new WireBuilder(1, 0x8200).Question("a.test", DnsType.A).Build();

        Assert.True(DnsMessage.Parse(wire).Flags.Truncated);
    }
}

public class DnsRdataTests
{
    /// <summary>The text of one answer record with the given type and data.</summary>
    private static string Data(DnsType type, byte[] data)
    {
        var wire = new WireBuilder(1, WireBuilder.Answer)
            .Question("x.test", type)
            .AnswerRecord(WireBuilder.Pointer(12), type, 60, data)
            .Build();
        return DnsMessage.Parse(wire).Answers.Single().Data;
    }

    [Fact]
    public void Addresses()
    {
        Assert.Equal("10.0.0.10", Data(DnsType.A, WireBuilder.Address("10.0.0.10")));
        Assert.Equal("2001:db8::20", Data(DnsType.AAAA, WireBuilder.Address("2001:db8::20")));
    }

    [Fact]
    public void Names()
    {
        Assert.Equal("ns1.lab.test.", Data(DnsType.NS, WireBuilder.Name("ns1.lab.test")));
        Assert.Equal("www.lab.test.", Data(DnsType.CNAME, WireBuilder.Name("www.lab.test")));
        Assert.Equal("host.lab.test.", Data(DnsType.PTR, WireBuilder.Name("host.lab.test")));
        Assert.Equal("x.test.", Data(DnsType.CNAME, WireBuilder.Pointer(12))); // a target that points back into the message
    }

    [Fact]
    public void Mail_exchangers_and_services()
    {
        Assert.Equal("10 mail.lab.test.", Data(DnsType.MX, WireBuilder.Concat(WireBuilder.Number16(10), WireBuilder.Name("mail.lab.test"))));
        Assert.Equal("10 20 5060 sip.lab.test.", Data(DnsType.SRV,
            WireBuilder.Concat(WireBuilder.Number16(10), WireBuilder.Number16(20), WireBuilder.Number16(5060), WireBuilder.Name("sip.lab.test"))));
    }

    [Fact]
    public void The_start_of_authority()
    {
        var data = WireBuilder.Concat(
            WireBuilder.Name("ns1.lab.test"), WireBuilder.Name("admin.lab.test"),
            WireBuilder.Number32(2026102801), WireBuilder.Number32(3600), WireBuilder.Number32(600),
            WireBuilder.Number32(86400), WireBuilder.Number32(300));

        Assert.Equal("ns1.lab.test. admin.lab.test. 2026102801 3600 600 86400 300", Data(DnsType.SOA, data));
    }

    [Fact]
    public void Text_is_quoted_with_the_special_characters_escaped()
    {
        Assert.Equal("\"v=spf1 -all\"", Data(DnsType.TXT, WireBuilder.CharacterString("v=spf1 -all")));
        Assert.Equal("\"one\" \"two\"", Data(DnsType.TXT, WireBuilder.Concat(WireBuilder.CharacterString("one"), WireBuilder.CharacterString("two"))));
        Assert.Equal("\"a\\\"b\\\\c\\001\"", Data(DnsType.TXT, [6, (byte)'a', (byte)'"', (byte)'b', (byte)'\\', (byte)'c', 0x01]));
    }

    [Fact]
    public void Certification_authority_authorisation()
    {
        var data = WireBuilder.Concat([0], WireBuilder.CharacterString("issue"), System.Text.Encoding.ASCII.GetBytes("letsencrypt.org"));

        Assert.Equal("0 issue \"letsencrypt.org\"", Data(DnsType.CAA, data));
    }

    [Fact]
    public void Dnssec_records()
    {
        var digest = new byte[] { 0xE0, 0x6D, 0x44 };
        Assert.Equal("20326 8 2 E06D44", Data(DnsType.DS, WireBuilder.Concat(WireBuilder.Number16(20326), [8, 2], digest)));

        Assert.Equal("257 3 13 AQID", Data(DnsType.DNSKEY, WireBuilder.Concat(WireBuilder.Number16(257), [3, 13], [1, 2, 3])));

        var signature = WireBuilder.Concat(
            WireBuilder.Number16((ushort)DnsType.A), [13, 3], WireBuilder.Number32(300),
            WireBuilder.Number32(1767225600), WireBuilder.Number32(1764547200), WireBuilder.Number16(12345),
            WireBuilder.Name("lab.test"), [1, 2, 3, 4]);
        Assert.Equal("A 13 3 300 20260101000000 20251201000000 12345 lab.test. AQIDBA==", Data(DnsType.RRSIG, signature));
    }

    [Fact]
    public void The_next_secure_record_lists_the_types_that_exist()
    {
        // Window 0, six bytes of bitmap: A, NS, SOA, MX, RRSIG and NSEC.
        var data = WireBuilder.Concat(WireBuilder.Name("next.lab.test"), [0, 6, 0x62, 0x01, 0x00, 0x00, 0x00, 0x03]);

        Assert.Equal("next.lab.test. A NS SOA MX RRSIG NSEC", Data(DnsType.NSEC, data));
    }

    [Fact]
    public void A_type_with_no_reader_here_is_shown_in_the_generic_form()
    {
        Assert.Equal("\\# 3 010203", Data((DnsType)65280, [1, 2, 3]));
        Assert.Equal("\\# 0", Data((DnsType)65280, []));
    }

    [Fact]
    public void Data_of_the_wrong_length_is_shown_as_bytes_rather_than_failing()
    {
        Assert.Equal("\\# 3 0A0000", Data(DnsType.A, [10, 0, 0]));
        Assert.Equal("\\# 2 0001", Data(DnsType.MX, [0, 1])); // a preference with no name
    }

    [Theory]
    [InlineData("A", DnsType.A)]
    [InlineData("aaaa", DnsType.AAAA)]
    [InlineData("Mx", DnsType.MX)]
    [InlineData("TYPE65280", (DnsType)65280)]
    [InlineData("type1", DnsType.A)]
    public void Record_types_can_be_read_by_name_or_in_the_generic_form(string text, DnsType expected)
    {
        Assert.True(DnsTypes.TryParse(text, out DnsType type));
        Assert.Equal(expected, type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NOPE")]
    [InlineData("TYPEx")]
    [InlineData("12")]
    public void Anything_else_is_not_a_record_type(string text)
    {
        Assert.False(DnsTypes.TryParse(text, out DnsType _));
    }

    [Fact]
    public void Names_are_given_back_for_types_and_codes()
    {
        Assert.Equal("AAAA", DnsTypes.Name(DnsType.AAAA));
        Assert.Equal("TYPE65280", DnsTypes.Name((DnsType)65280));
        Assert.Equal("NXDOMAIN", DnsTypes.RcodeName(3));
        Assert.Equal("RCODE23", DnsTypes.RcodeName(23));
        Assert.Equal("QUERY", DnsTypes.OpcodeName(0));
    }
}
