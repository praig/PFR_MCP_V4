using System.Globalization;
using System.Net;
using System.Text;

namespace Pfr.Core.Network;

public enum DnsType : ushort
{
    A = 1, NS = 2, CNAME = 5, SOA = 6, PTR = 12, MX = 15, TXT = 16, AAAA = 28, SRV = 33, NAPTR = 35,
    DNAME = 39, OPT = 41, DS = 43, SSHFP = 44, RRSIG = 46, NSEC = 47, DNSKEY = 48, NSEC3 = 50, NSEC3PARAM = 51,
    TLSA = 52, SVCB = 64, HTTPS = 65, SPF = 99, IXFR = 251, AXFR = 252, ANY = 255, CAA = 257,
}

public enum DnsClass : ushort
{
    IN = 1,

    /// <summary>Chaos: used for questions about the server itself, such as version.bind.</summary>
    CH = 3,
    HS = 4,
    ANY = 255,
}

/// <summary>Raised for anything that stops a DNS question being asked or answered, with a message fit to show.</summary>
public class DnsQueryException(string message) : Exception(message);

/// <summary>No answer arrived in time.</summary>
public sealed class DnsTimeoutException(string message) : DnsQueryException(message);

public static class DnsTypes
{
    /// <summary>"A", "MX"..., or "TYPE65280" for a type with no name here.</summary>
    public static string Name(DnsType type) => Enum.IsDefined(type) ? type.ToString() : $"TYPE{(ushort)type}";

    /// <summary>Reads a type by name ("aaaa") or in the generic form ("TYPE65280").</summary>
    public static bool TryParse(string? text, out DnsType type)
    {
        type = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        if (text.All(char.IsLetter) && Enum.TryParse<DnsType>(text, ignoreCase: true, out var named) && Enum.IsDefined(named))
        {
            type = named;
            return true;
        }
        if (text.StartsWith("TYPE", StringComparison.OrdinalIgnoreCase) && ushort.TryParse(text.AsSpan(4), out var number))
        {
            type = (DnsType)number;
            return true;
        }
        return false;
    }

    public static string Name(DnsClass dnsClass) => Enum.IsDefined(dnsClass) ? dnsClass.ToString() : $"CLASS{(ushort)dnsClass}";

    public static bool TryParse(string? text, out DnsClass dnsClass)
    {
        dnsClass = default;
        if (string.IsNullOrWhiteSpace(text) || !text.Trim().All(char.IsLetter)) return false;
        return Enum.TryParse(text.Trim(), ignoreCase: true, out dnsClass) && Enum.IsDefined(dnsClass);
    }

    /// <summary>NOERROR, NXDOMAIN, SERVFAIL...; "RCODE23" for one with no name.</summary>
    public static string RcodeName(int rcode) => rcode switch
    {
        0 => "NOERROR", 1 => "FORMERR", 2 => "SERVFAIL", 3 => "NXDOMAIN", 4 => "NOTIMP", 5 => "REFUSED",
        6 => "YXDOMAIN", 7 => "YXRRSET", 8 => "NXRRSET", 9 => "NOTAUTH", 10 => "NOTZONE", 16 => "BADVERS",
        _ => $"RCODE{rcode}",
    };

    public static string OpcodeName(int opcode) => opcode switch
    {
        0 => "QUERY", 1 => "IQUERY", 2 => "STATUS", 4 => "NOTIFY", 5 => "UPDATE", _ => $"OPCODE{opcode}",
    };
}

/// <summary>Domain names as they travel in a DNS message and as people write them.</summary>
public static class DnsNames
{
    /// <summary>The name with a trailing dot ("www.lab.test." and "." for the root), as DNS tools show it.</summary>
    public static string Absolute(string name)
    {
        name = name.Trim();
        return name.Length == 0 || name == "." ? "." : name.EndsWith('.') && !name.EndsWith("\\.") ? name : name + ".";
    }

    /// <summary>"10.0.0.20" gives "20.0.0.10.in-addr.arpa."; an IPv6 address gives its nibble form under ip6.arpa.</summary>
    public static string ReverseName(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4) return string.Join('.', bytes.Reverse()) + ".in-addr.arpa.";

        var nibbles = bytes.SelectMany(b => new[] { b >> 4, b & 0xF }).Reverse().Select(n => n.ToString("x", CultureInfo.InvariantCulture));
        return string.Join('.', nibbles) + ".ip6.arpa.";
    }

    /// <summary>
    /// The name as the labels that go on the wire. Backslash escapes work as in zone files (\. for a dot inside a label,
    /// \DDD for a byte); names with accented letters are converted to their ASCII form.
    /// </summary>
    /// <exception cref="ArgumentException">A label is empty or too long, or the name is too long.</exception>
    public static byte[] Encode(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == ".") return [0];
        if (name.Any(c => c > 0x7F)) name = new IdnMapping().GetAscii(name);

        var output = new List<byte>();
        var label = new List<byte>();
        void EndLabel()
        {
            if (label.Count == 0) throw new ArgumentException($"\"{name}\" has an empty label (two dots in a row, or a leading dot).");
            if (label.Count > 63) throw new ArgumentException($"A label of \"{name}\" is longer than 63 characters.");
            output.Add((byte)label.Count);
            output.AddRange(label);
            label.Clear();
        }

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '.')
            {
                EndLabel();
            }
            else if (c == '\\')
            {
                if (i + 1 >= name.Length) throw new ArgumentException($"\"{name}\" ends with a backslash.");
                if (i + 3 < name.Length + 0 && char.IsAsciiDigit(name[i + 1]) && char.IsAsciiDigit(name[i + 2]) && char.IsAsciiDigit(name[i + 3])
                    && int.Parse(name.AsSpan(i + 1, 3), CultureInfo.InvariantCulture) is var value and <= 255)
                {
                    label.Add((byte)value);
                    i += 3;
                }
                else
                {
                    label.Add((byte)name[++i]);
                }
            }
            else
            {
                label.Add((byte)c);
            }
        }
        if (label.Count > 0) EndLabel();

        output.Add(0);
        if (output.Count > 255) throw new ArgumentException($"\"{name}\" is longer than 255 bytes.");
        return output.ToArray();
    }

    /// <summary>
    /// Reads a name at <paramref name="position"/>, following compression pointers, and moves the position past it.
    /// Bytes that are not plain characters, and dots and backslashes inside a label, are written as zone files do.
    /// </summary>
    public static string Decode(byte[] message, ref int position)
    {
        var text = new StringBuilder();
        var cursor = position;
        var jumped = false;
        var hops = 0;
        var length = 0;

        while (true)
        {
            if (cursor >= message.Length) throw Damaged();
            int size = message[cursor];

            if (size == 0)
            {
                if (!jumped) position = cursor + 1;
                break;
            }

            if ((size & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= message.Length) throw Damaged();
                var target = ((size & 0x3F) << 8) | message[cursor + 1];
                if (!jumped) position = cursor + 2;
                jumped = true;
                if (++hops > 64 || target >= message.Length) throw Damaged();
                cursor = target;
                continue;
            }

            if ((size & 0xC0) != 0) throw Damaged();

            cursor++;
            if (cursor + size > message.Length) throw Damaged();
            length += size + 1;
            if (length > 255) throw Damaged();

            for (var i = 0; i < size; i++)
            {
                var b = message[cursor + i];
                if (b is (byte)'.' or (byte)'\\') text.Append('\\').Append((char)b);
                else if (b < 0x21 || b > 0x7E) text.Append('\\').Append(b.ToString("000", CultureInfo.InvariantCulture));
                else text.Append((char)b);
            }
            text.Append('.');
            cursor += size;
        }

        return text.Length == 0 ? "." : text.ToString();
    }

    internal static DnsQueryException Damaged() => new("The server's answer is cut short or damaged.");
}

public readonly record struct DnsFlags(
    bool Response, int Opcode, bool Authoritative, bool Truncated, bool RecursionDesired,
    bool RecursionAvailable, bool AuthenticData, bool CheckingDisabled, int Rcode)
{
    public ushort ToWire() => (ushort)(
        (Response ? 0x8000 : 0) | ((Opcode & 0xF) << 11) | (Authoritative ? 0x0400 : 0) | (Truncated ? 0x0200 : 0)
        | (RecursionDesired ? 0x0100 : 0) | (RecursionAvailable ? 0x0080 : 0) | (AuthenticData ? 0x0020 : 0)
        | (CheckingDisabled ? 0x0010 : 0) | (Rcode & 0xF));

    public static DnsFlags FromWire(ushort wire) => new(
        (wire & 0x8000) != 0, (wire >> 11) & 0xF, (wire & 0x0400) != 0, (wire & 0x0200) != 0, (wire & 0x0100) != 0,
        (wire & 0x0080) != 0, (wire & 0x0020) != 0, (wire & 0x0010) != 0, wire & 0xF);

    /// <summary>The flags that are set, as dig lists them: "qr aa rd ra".</summary>
    public string Names => string.Join(' ', new[]
    {
        Response ? "qr" : null, Authoritative ? "aa" : null, Truncated ? "tc" : null, RecursionDesired ? "rd" : null,
        RecursionAvailable ? "ra" : null, AuthenticData ? "ad" : null, CheckingDisabled ? "cd" : null,
    }.Where(f => f is not null));
}

public sealed record DnsQuestion(string Name, DnsType Type, DnsClass Class = DnsClass.IN);

public sealed record DnsEdnsOption(int Code, byte[] Data);

/// <summary>The EDNS0 pseudo-record: the size of UDP answer the sender can take, and the DNSSEC OK flag.</summary>
public sealed record DnsEdns(
    int UdpPayload, int ExtendedRcode, int Version, bool DnssecOk, IReadOnlyList<DnsEdnsOption> Options);

public sealed record DnsResourceRecord(string Name, DnsType Type, DnsClass Class, uint Ttl, string Data)
{
    /// <summary>The record as a line of a zone file.</summary>
    public override string ToString() =>
        $"{Name}\t{Ttl}\t{DnsTypes.Name(Class)}\t{DnsTypes.Name(Type)}\t{Data}";
}

/// <summary>A DNS message: what is asked, what is answered, and the flags that go with it.</summary>
public sealed class DnsMessage
{
    public ushort Id { get; init; }
    public DnsFlags Flags { get; init; }
    public List<DnsQuestion> Questions { get; } = [];
    public List<DnsResourceRecord> Answers { get; } = [];
    public List<DnsResourceRecord> Authorities { get; } = [];

    /// <summary>The additional section, without the EDNS pseudo-record (which is <see cref="Edns"/>).</summary>
    public List<DnsResourceRecord> Additionals { get; } = [];

    public DnsEdns? Edns { get; set; }

    /// <summary>The counts the header declared (a message that is cut short may hold fewer records).</summary>
    public int QuestionCount { get; init; }
    public int AnswerCount { get; init; }
    public int AuthorityCount { get; init; }
    public int AdditionalCount { get; init; }

    /// <summary>The response code including the bits EDNS adds to it.</summary>
    public int FullRcode => Flags.Rcode | ((Edns?.ExtendedRcode ?? 0) << 4);

    /// <summary>A question, as sent: the questions and the EDNS record (answers are never encoded).</summary>
    public byte[] Encode()
    {
        var wire = new List<byte>();
        void U16(int value)
        {
            wire.Add((byte)(value >> 8));
            wire.Add((byte)value);
        }

        U16(Id);
        U16(Flags.ToWire());
        U16(Questions.Count);
        U16(0);
        U16(0);
        U16(Edns is null ? 0 : 1);

        foreach (var q in Questions)
        {
            wire.AddRange(DnsNames.Encode(q.Name));
            U16((ushort)q.Type);
            U16((ushort)q.Class);
        }

        if (Edns is { } edns)
        {
            wire.Add(0); // the root name
            U16((ushort)DnsType.OPT);
            U16(edns.UdpPayload);
            var ttl = ((uint)edns.ExtendedRcode << 24) | ((uint)edns.Version << 16) | (edns.DnssecOk ? 0x8000u : 0);
            U16((int)(ttl >> 16));
            U16((int)(ttl & 0xFFFF));
            U16(edns.Options.Sum(o => 4 + o.Data.Length));
            foreach (var option in edns.Options)
            {
                U16(option.Code);
                U16(option.Data.Length);
                wire.AddRange(option.Data);
            }
        }

        return wire.ToArray();
    }

    /// <exception cref="DnsQueryException">The message is cut short or damaged.</exception>
    public static DnsMessage Parse(byte[] data)
    {
        try
        {
            if (data.Length < 12) throw DnsNames.Damaged();
            var position = 0;
            var id = U16(data, ref position);
            var flags = DnsFlags.FromWire(U16(data, ref position));
            int questions = U16(data, ref position), answers = U16(data, ref position);
            int authorities = U16(data, ref position), additionals = U16(data, ref position);

            var message = new DnsMessage
            {
                Id = id, Flags = flags, QuestionCount = questions, AnswerCount = answers,
                AuthorityCount = authorities, AdditionalCount = additionals,
            };

            for (var i = 0; i < questions; i++)
            {
                var name = DnsNames.Decode(data, ref position);
                var type = (DnsType)U16(data, ref position);
                var dnsClass = (DnsClass)U16(data, ref position);
                message.Questions.Add(new DnsQuestion(name, type, dnsClass));
            }

            ReadRecords(data, ref position, answers, message.Answers, null);
            ReadRecords(data, ref position, authorities, message.Authorities, null);
            ReadRecords(data, ref position, additionals, message.Additionals, message);
            return message;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            throw DnsNames.Damaged();
        }
    }

    private static void ReadRecords(byte[] data, ref int position, int count, List<DnsResourceRecord> into, DnsMessage? edns)
    {
        for (var i = 0; i < count; i++)
        {
            var name = DnsNames.Decode(data, ref position);
            var type = (DnsType)U16(data, ref position);
            var dnsClass = U16(data, ref position);
            var ttl = U32(data, ref position);
            int length = U16(data, ref position);
            if (position + length > data.Length) throw DnsNames.Damaged();

            if (type == DnsType.OPT && edns is not null)
            {
                var options = new List<DnsEdnsOption>();
                var p = position;
                var end = position + length;
                while (p + 4 <= end)
                {
                    var code = U16(data, ref p);
                    int size = U16(data, ref p);
                    if (p + size > end) throw DnsNames.Damaged();
                    options.Add(new DnsEdnsOption(code, data[p..(p + size)]));
                    p += size;
                }
                edns.Edns = new DnsEdns(dnsClass, (int)(ttl >> 24), (int)((ttl >> 16) & 0xFF), (ttl & 0x8000) != 0, options);
            }
            else
            {
                into.Add(new DnsResourceRecord(name, type, (DnsClass)dnsClass, ttl, DnsRdata.Format(type, data, position, length)));
            }
            position += length;
        }
    }

    private static ushort U16(byte[] data, ref int position)
    {
        if (position + 2 > data.Length) throw DnsNames.Damaged();
        var value = (ushort)((data[position] << 8) | data[position + 1]);
        position += 2;
        return value;
    }

    private static uint U32(byte[] data, ref int position)
    {
        if (position + 4 > data.Length) throw DnsNames.Damaged();
        var value = ((uint)data[position] << 24) | ((uint)data[position + 1] << 16) | ((uint)data[position + 2] << 8) | data[position + 3];
        position += 4;
        return value;
    }
}

/// <summary>Record data as zone-file text. A type with no reader here is shown in the generic form (RFC 3597).</summary>
public static class DnsRdata
{
    public static string Format(DnsType type, byte[] message, int start, int length)
    {
        var end = start + length;
        try
        {
            var p = start;
            switch (type)
            {
                case DnsType.A when length == 4:
                case DnsType.AAAA when length == 16:
                    return new IPAddress(message.AsSpan(start, length)).ToString();

                case DnsType.NS or DnsType.CNAME or DnsType.PTR or DnsType.DNAME:
                    return DnsNames.Decode(message, ref p);

                case DnsType.MX:
                    {
                        var preference = U16(message, ref p);
                        return $"{preference} {DnsNames.Decode(message, ref p)}";
                    }

                case DnsType.SOA:
                    {
                        var primary = DnsNames.Decode(message, ref p);
                        var mailbox = DnsNames.Decode(message, ref p);
                        var numbers = Enumerable.Range(0, 5).Select(_ => U32(message, ref p)).ToArray();
                        return $"{primary} {mailbox} {string.Join(' ', numbers)}";
                    }

                case DnsType.TXT or DnsType.SPF:
                    {
                        var strings = new List<string>();
                        while (p < end)
                        {
                            int size = message[p++];
                            if (p + size > end) throw DnsNames.Damaged();
                            strings.Add(Quoted(message.AsSpan(p, size)));
                            p += size;
                        }
                        return string.Join(' ', strings);
                    }

                case DnsType.SRV:
                    {
                        var priority = U16(message, ref p);
                        var weight = U16(message, ref p);
                        var port = U16(message, ref p);
                        return $"{priority} {weight} {port} {DnsNames.Decode(message, ref p)}";
                    }

                case DnsType.CAA:
                    {
                        int flags = message[p++];
                        int tagLength = message[p++];
                        var tag = Encoding.ASCII.GetString(message, p, tagLength);
                        p += tagLength;
                        return $"{flags} {tag} {Quoted(message.AsSpan(p, end - p))}";
                    }

                case DnsType.NAPTR:
                    {
                        var order = U16(message, ref p);
                        var preference = U16(message, ref p);
                        string CharacterString()
                        {
                            int size = message[p++];
                            var text = Quoted(message.AsSpan(p, size));
                            p += size;
                            return text;
                        }
                        var flags = CharacterString();
                        var services = CharacterString();
                        var regexp = CharacterString();
                        return $"{order} {preference} {flags} {services} {regexp} {DnsNames.Decode(message, ref p)}";
                    }

                case DnsType.DS when length >= 4:
                    return $"{U16(message, ref p)} {message[p++]} {message[p++]} {Convert.ToHexString(message, p, end - p)}";

                case DnsType.DNSKEY when length >= 4:
                    return $"{U16(message, ref p)} {message[p++]} {message[p++]} {Convert.ToBase64String(message, p, end - p)}";

                case DnsType.RRSIG when length >= 18:
                    {
                        var covered = (DnsType)U16(message, ref p);
                        int algorithm = message[p++];
                        int labels = message[p++];
                        var originalTtl = U32(message, ref p);
                        var expires = U32(message, ref p);
                        var inception = U32(message, ref p);
                        var keyTag = U16(message, ref p);
                        var signer = DnsNames.Decode(message, ref p);
                        return $"{DnsTypes.Name(covered)} {algorithm} {labels} {originalTtl} {Time(expires)} {Time(inception)} " +
                               $"{keyTag} {signer} {Convert.ToBase64String(message, p, end - p)}";
                    }

                case DnsType.NSEC:
                    {
                        var next = DnsNames.Decode(message, ref p);
                        return $"{next} {TypeBitmap(message, p, end)}".TrimEnd();
                    }

                case DnsType.TLSA when length >= 3:
                    return $"{message[p++]} {message[p++]} {message[p++]} {Convert.ToHexString(message, p, end - p)}";

                case DnsType.SSHFP when length >= 2:
                    return $"{message[p++]} {message[p++]} {Convert.ToHexString(message, p, end - p)}";
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or DnsQueryException)
        {
            // Fall through to the generic form: better to show the bytes than nothing.
        }

        return length == 0 ? "\\# 0" : $"\\# {length} {Convert.ToHexString(message, start, length)}";
    }

    private static ushort U16(byte[] data, ref int position)
    {
        var value = (ushort)((data[position] << 8) | data[position + 1]);
        position += 2;
        return value;
    }

    private static uint U32(byte[] data, ref int position)
    {
        var value = ((uint)data[position] << 24) | ((uint)data[position + 1] << 16) | ((uint)data[position + 2] << 8) | data[position + 3];
        position += 4;
        return value;
    }

    /// <summary>A character string in quotes, with quotes, backslashes and unprintable bytes escaped as in zone files.</summary>
    private static string Quoted(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder("\"");
        foreach (var b in bytes)
        {
            if (b is (byte)'"' or (byte)'\\') text.Append('\\').Append((char)b);
            else if (b < 0x20 || b > 0x7E) text.Append('\\').Append(b.ToString("000", CultureInfo.InvariantCulture));
            else text.Append((char)b);
        }
        return text.Append('"').ToString();
    }

    /// <summary>RRSIG times: seconds since 1970 written as yyyyMMddHHmmss.</summary>
    private static string Time(uint seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    /// <summary>The record types listed in an NSEC type bitmap.</summary>
    private static string TypeBitmap(byte[] message, int start, int end)
    {
        var names = new List<string>();
        var p = start;
        while (p + 2 <= end)
        {
            int window = message[p];
            int size = message[p + 1];
            p += 2;
            if (size is 0 or > 32 || p + size > end) break;
            for (var i = 0; i < size; i++)
            {
                for (var bit = 0; bit < 8; bit++)
                {
                    if ((message[p + i] & (0x80 >> bit)) != 0) names.Add(DnsTypes.Name((DnsType)(window * 256 + i * 8 + bit)));
                }
            }
            p += size;
        }
        return string.Join(' ', names);
    }
}
