using System.Net;
using System.Net.Sockets;
using System.Text;
using Pfr.Core.Network;

namespace Pfr.Core.Tests.Network;

/// <summary>Builds DNS messages byte by byte, so the reader is tested against exactly what a server would send.</summary>
public sealed class WireBuilder
{
    private readonly List<byte> _body = [];
    private readonly ushort _id;
    private readonly ushort _flags;
    private int _questions, _answers, _authorities, _additionals;

    public WireBuilder(ushort id, ushort flags)
    {
        _id = id;
        _flags = flags;
    }

    /// <summary>The usual flags of an answer from a recursive server: qr, rd, ra.</summary>
    public const ushort Answer = 0x8180;

    /// <summary>qr, aa, rd, ra.</summary>
    public const ushort AuthoritativeAnswer = 0x8580;

    /// <summary>A compression pointer to an earlier part of the message (the first name starts at 12).</summary>
    public static byte[] Pointer(int offset) => [(byte)(0xC0 | (offset >> 8)), (byte)offset];

    public WireBuilder Question(string name, DnsType type, DnsClass dnsClass = DnsClass.IN)
    {
        _body.AddRange(DnsNames.Encode(name));
        U16((ushort)type);
        U16((ushort)dnsClass);
        _questions++;
        return this;
    }

    public WireBuilder AnswerRecord(byte[] owner, DnsType type, uint ttl, byte[] data, DnsClass dnsClass = DnsClass.IN)
    {
        Record(owner, type, (ushort)dnsClass, ttl, data);
        _answers++;
        return this;
    }

    public WireBuilder AuthorityRecord(byte[] owner, DnsType type, uint ttl, byte[] data)
    {
        Record(owner, type, (ushort)DnsClass.IN, ttl, data);
        _authorities++;
        return this;
    }

    public WireBuilder AdditionalRecord(byte[] owner, DnsType type, uint ttl, byte[] data)
    {
        Record(owner, type, (ushort)DnsClass.IN, ttl, data);
        _additionals++;
        return this;
    }

    /// <summary>The EDNS record, as a server adds it: the UDP size it can take and the DO flag.</summary>
    public WireBuilder Opt(int udpPayload = 4096, bool dnssecOk = false)
    {
        _body.Add(0);
        U16((ushort)DnsType.OPT);
        U16((ushort)udpPayload);
        U16(0);
        U16(dnssecOk ? 0x8000 : 0);
        U16(0);
        _additionals++;
        return this;
    }

    public byte[] Build()
    {
        var message = new List<byte>();
        void U16(int value)
        {
            message.Add((byte)(value >> 8));
            message.Add((byte)value);
        }

        U16(_id);
        U16(_flags);
        U16(_questions);
        U16(_answers);
        U16(_authorities);
        U16(_additionals);
        message.AddRange(_body);
        return message.ToArray();
    }

    private void Record(byte[] owner, DnsType type, ushort dnsClass, uint ttl, byte[] data)
    {
        _body.AddRange(owner);
        U16((ushort)type);
        U16(dnsClass);
        U16((int)(ttl >> 16));
        U16((int)(ttl & 0xFFFF));
        U16(data.Length);
        _body.AddRange(data);
    }

    private void U16(int value)
    {
        _body.Add((byte)(value >> 8));
        _body.Add((byte)value);
    }

    /// <summary>Record data helpers.</summary>
    public static byte[] Name(string name) => DnsNames.Encode(name);
    public static byte[] Address(string address) => IPAddress.Parse(address).GetAddressBytes();
    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    public static byte[] Number16(int value) => [(byte)(value >> 8), (byte)value];

    public static byte[] Number32(uint value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    public static byte[] CharacterString(string text) => [(byte)text.Length, .. System.Text.Encoding.ASCII.GetBytes(text)];
}

/// <summary>A transport that answers from a function, and records what it was asked.</summary>
public sealed class FakeDnsTransport(Func<byte[], bool, int, DnsExchange> respond) : IDnsTransport
{
    public List<(bool Tcp, byte[] Query)> Calls { get; } = [];

    public Task<DnsExchange> ExchangeAsync(byte[] query, IPEndPoint server, bool tcp, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add((tcp, query));
        return Task.FromResult(respond(query, tcp, Calls.Count));
    }

    /// <summary>An answer to whatever was asked: same id and question, with the records the caller adds.</summary>
    public static DnsExchange Reply(byte[] query, ushort flags, bool tcp, Action<WireBuilder>? records = null)
    {
        var asked = DnsMessage.Parse(query);
        var builder = new WireBuilder(asked.Id, flags);
        foreach (var q in asked.Questions) builder.Question(q.Name, q.Type, q.Class);
        records?.Invoke(builder);
        var bytes = builder.Build();
        return new DnsExchange(bytes, tcp, TimeSpan.FromMilliseconds(3));
    }
}

/// <summary>A real DNS server on the loopback address, to exercise the UDP and TCP code with actual sockets.</summary>
internal sealed class LoopbackDnsServer : IDisposable
{
    private readonly UdpClient _udp;
    private readonly TcpListener _tcp;
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<DnsMessage, bool, byte[]> _respond;

    public LoopbackDnsServer(Func<DnsMessage, bool, byte[]> respond)
    {
        _respond = respond;

        // UDP and TCP must share a port number; try until one is free for both.
        for (var attempt = 0; ; attempt++)
        {
            var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            try
            {
                var tcp = new TcpListener(IPAddress.Loopback, port);
                tcp.Start();
                _udp = udp;
                _tcp = tcp;
                Port = port;
                break;
            }
            catch (SocketException) when (attempt < 30)
            {
                udp.Dispose();
            }
        }

        _ = Task.Run(UdpLoopAsync);
        _ = Task.Run(TcpLoopAsync);
    }

    public int Port { get; }

    private async Task UdpLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var received = await _udp.ReceiveAsync(_stop.Token);
                var response = _respond(DnsMessage.Parse(received.Buffer), false);
                await _udp.SendAsync(response, received.RemoteEndPoint, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopped.
        }
    }

    private async Task TcpLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _tcp.AcceptTcpClientAsync(_stop.Token);
                var stream = client.GetStream();
                var header = new byte[2];
                await stream.ReadExactlyAsync(header, _stop.Token);
                var query = new byte[(header[0] << 8) | header[1]];
                await stream.ReadExactlyAsync(query, _stop.Token);

                var response = _respond(DnsMessage.Parse(query), true);
                await stream.WriteAsync(new[] { (byte)(response.Length >> 8), (byte)response.Length }, _stop.Token);
                await stream.WriteAsync(response, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
        {
            // Stopped.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
        _tcp.Stop();
        _stop.Dispose();
    }
}

/// <summary>Answers differently depending on which server is asked.</summary>
public sealed class PerServerDnsTransport(Func<IPAddress, byte[], bool, DnsExchange> respond) : IDnsTransport
{
    public List<IPAddress> Asked { get; } = [];

    public Task<DnsExchange> ExchangeAsync(byte[] query, IPEndPoint server, bool tcp, TimeSpan timeout, CancellationToken ct)
    {
        lock (Asked) Asked.Add(server.Address);
        return Task.FromResult(respond(server.Address, query, tcp));
    }
}

/// <summary>A pinger that answers from a function, so no real ICMP is needed.</summary>
public sealed class FakePinger(Func<IPAddress, int, PingAttempt> respond) : IPinger
{
    public List<(IPAddress Address, int Sequence)> Calls { get; } = [];

    public Task<PingAttempt> PingAsync(IPAddress address, int sequence, PingSettings settings, CancellationToken ct)
    {
        lock (Calls) Calls.Add((address, sequence));
        return Task.FromResult(respond(address, sequence));
    }
}

/// <summary>An SSH client that behaves like a real one about host keys: it stops, before any password, unless the key matches.</summary>
public sealed class FakeSshClient(Func<SshTarget, string?, SshSessionResult> respond) : ISshClient
{
    public List<(SshTarget Target, string? Pinned, IReadOnlyList<string> Commands)> Calls { get; } = [];

    /// <summary>The account each call signed in with, in the same order as <see cref="Calls"/>.</summary>
    public List<SshAccount> Accounts { get; } = [];

    public Task<SshSessionResult> RunAsync(SshTarget target, SshAccount account, string? pinnedFingerprint,
        IReadOnlyList<string> commands, TimeSpan connectTimeout, TimeSpan commandTimeout, CancellationToken ct)
    {
        lock (Calls)
        {
            Calls.Add((target, pinnedFingerprint, commands));
            Accounts.Add(account);
        }
        return Task.FromResult(respond(target, pinnedFingerprint));
    }

    /// <summary>The key a host presents: one of its own, different for every host.</summary>
    public static SshHostKeyInfo KeyOf(SshTarget target) =>
        new("ssh-ed25519", SshFingerprint.Of(Encoding.UTF8.GetBytes("key of " + target.Host)));

    public static SshSessionResult Output(SshTarget target, string output = "NIOS 9.0.8") =>
        new(target, KeyOf(target), [new SshCommandResult("show version", output, "", 0, TimeSpan.FromMilliseconds(5), null)], null);

    /// <summary>The usual behaviour: refuse a host whose key is not pinned, or is not the one pinned; otherwise answer.</summary>
    public static SshSessionResult Honest(SshTarget target, string? pinned)
    {
        var key = KeyOf(target);
        if (pinned is null) throw new SshHostKeyException(SshHostKeyProblem.NotPinned, target, key, null);
        if (pinned != key.Fingerprint) throw new SshHostKeyException(SshHostKeyProblem.Mismatch, target, key, pinned);
        return Output(target);
    }
}
