using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Pfr.Core.Nios.Model;

namespace Pfr.Core.Network;

/// <summary>How to ask: which server, over what, and which flags to set (the options of dig).</summary>
public sealed record DnsQueryOptions
{
    /// <summary>The server to ask. DNS queries only ever go to a server the user chose.</summary>
    public required IPAddress Server { get; init; }

    public int Port { get; init; } = 53;

    /// <summary>Use TCP instead of UDP.</summary>
    public bool Tcp { get; init; }

    /// <summary>Never fall back to TCP when a UDP answer is cut short.</summary>
    public bool UdpOnly { get; init; }

    /// <summary>Ask the server to look the answer up if it does not have it (the RD flag).</summary>
    public bool Recurse { get; init; } = true;

    /// <summary>Ask for DNSSEC records (sets the DO flag; needs EDNS).</summary>
    public bool Dnssec { get; init; }

    /// <summary>Ask a validating server not to validate (the CD flag).</summary>
    public bool CheckingDisabled { get; init; }

    /// <summary>Set the AD flag in the question.</summary>
    public bool AuthenticData { get; init; }

    public bool Edns { get; init; } = true;

    /// <summary>The largest UDP answer we say we can take (1232 avoids fragmentation).</summary>
    public int UdpPayload { get; init; } = 1232;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Further tries over UDP after a timeout.</summary>
    public int Retries { get; init; } = 1;
}

public sealed record DnsExchange(byte[] Response, bool UsedTcp, TimeSpan Elapsed);

/// <summary>Sends one DNS message and returns the reply. The sockets one is used; tests replace it.</summary>
public interface IDnsTransport
{
    /// <exception cref="DnsTimeoutException">Nothing came back in time.</exception>
    /// <exception cref="DnsQueryException">The server could not be reached.</exception>
    Task<DnsExchange> ExchangeAsync(byte[] query, IPEndPoint server, bool tcp, TimeSpan timeout, CancellationToken ct);
}

public sealed class SystemDnsTransport : IDnsTransport
{
    public static readonly SystemDnsTransport Instance = new();

    public async Task<DnsExchange> ExchangeAsync(byte[] query, IPEndPoint server, bool tcp, TimeSpan timeout, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);

        try
        {
            var response = tcp ? await TcpAsync(query, server, limit.Token) : await UdpAsync(query, server, limit.Token);
            return new DnsExchange(response, tcp, watch.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DnsTimeoutException($"No answer from {server.Address} within {timeout.TotalSeconds:0.#} seconds.");
        }
        catch (SocketException ex)
        {
            throw new DnsQueryException(ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.ConnectionReset
                ? $"{server.Address} refused the query: nothing is listening on {(tcp ? "TCP" : "UDP")} port {server.Port}."
                : $"Could not reach {server.Address}: {ex.Message}");
        }
        catch (IOException ex)
        {
            throw new DnsQueryException($"The connection to {server.Address} failed: {ex.Message}");
        }
    }

    private static async Task<byte[]> UdpAsync(byte[] query, IPEndPoint server, CancellationToken ct)
    {
        using var udp = new UdpClient(server.AddressFamily);
        udp.Connect(server);
        await udp.SendAsync(query, ct);
        var result = await udp.ReceiveAsync(ct);
        return result.Buffer;
    }

    private static async Task<byte[]> TcpAsync(byte[] query, IPEndPoint server, CancellationToken ct)
    {
        using var client = new TcpClient(server.AddressFamily) { NoDelay = true };
        await client.ConnectAsync(server.Address, server.Port, ct);
        var stream = client.GetStream();

        // Over TCP every message is preceded by its length in two bytes.
        var framed = new byte[2 + query.Length];
        framed[0] = (byte)(query.Length >> 8);
        framed[1] = (byte)query.Length;
        query.CopyTo(framed, 2);
        await stream.WriteAsync(framed, ct);

        var header = new byte[2];
        await stream.ReadExactlyAsync(header, ct);
        var response = new byte[(header[0] << 8) | header[1]];
        await stream.ReadExactlyAsync(response, ct);
        return response;
    }
}

/// <summary>An answer, with how it was got.</summary>
public sealed record DnsResponse(
    DnsQuestion Question, IPEndPoint Server, DnsMessage Message, bool UsedTcp, bool RetriedOverTcp, TimeSpan Elapsed, int Size)
{
    public string Status => DnsTypes.RcodeName(Message.FullRcode);
}

/// <summary>Asks a DNS server a question and reads the answer.</summary>
public static class DnsLookup
{
    /// <exception cref="DnsQueryException">No usable answer: the server could not be reached, or did not answer in time.</exception>
    public static async Task<DnsResponse> QueryAsync(
        DnsQuestion question, DnsQueryOptions options, IDnsTransport? transport = null, CancellationToken ct = default)
    {
        transport ??= SystemDnsTransport.Instance;

        var id = (ushort)RandomNumberGenerator.GetInt32(0x10000);
        var request = new DnsMessage
        {
            Id = id,
            Flags = new DnsFlags(false, 0, false, false, options.Recurse, false, options.AuthenticData, options.CheckingDisabled, 0),
        };
        request.Questions.Add(question);
        if (options.Edns || options.Dnssec) request.Edns = new DnsEdns(options.UdpPayload, 0, 0, options.Dnssec, []);

        var wire = request.Encode();
        var endpoint = new IPEndPoint(options.Server, options.Port);

        DnsTimeoutException? timedOut = null;
        var attempts = options.Tcp ? 1 : Math.Max(1, options.Retries + 1);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var exchange = await transport.ExchangeAsync(wire, endpoint, options.Tcp, options.Timeout, ct);
                var message = Check(DnsMessage.Parse(exchange.Response), id);
                var elapsed = exchange.Elapsed;
                var retried = false;

                // A UDP answer that did not fit says so; the whole answer is got over TCP, as resolvers do.
                if (message.Flags.Truncated && !options.Tcp && !options.UdpOnly)
                {
                    exchange = await transport.ExchangeAsync(wire, endpoint, true, options.Timeout, ct);
                    message = Check(DnsMessage.Parse(exchange.Response), id);
                    elapsed += exchange.Elapsed;
                    retried = true;
                }

                return new DnsResponse(question, endpoint, message, exchange.UsedTcp, retried, elapsed, exchange.Response.Length);
            }
            catch (DnsTimeoutException ex)
            {
                timedOut = ex;
            }
        }

        throw timedOut!;
    }

    private static DnsMessage Check(DnsMessage message, ushort id)
    {
        if (message.Id != id || !message.Flags.Response)
            throw new DnsQueryException("The server's answer does not match the question that was sent (its id is different).");
        return message;
    }
}

/// <summary>Where to find DNS servers to ask.</summary>
public static class DnsServers
{
    /// <summary>The grid's members that serve DNS, by their address.</summary>
    public static List<DnsServerChoice> FromMembers(GridInventory inventory)
    {
        var servers = new List<DnsServerChoice>();
        foreach (var member in inventory.Members.Where(m => m.Services.Contains("DNS", StringComparer.OrdinalIgnoreCase)))
        {
            var text = member.Ipv4Address ?? member.Ipv6Address;
            if (IPAddress.TryParse(text, out var address)) servers.Add(new DnsServerChoice(member.HostName, address));
        }
        return servers;
    }

    /// <summary>The resolvers this computer is configured to use (IPv4 first), or none.</summary>
    public static List<IPAddress> Resolvers()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().DnsAddresses)
                .Where(a => !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal)
                .Distinct()
                .OrderBy(a => a.AddressFamily != AddressFamily.InterNetwork)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>A server given as an address or a name (a name is looked up with this computer's resolver).</summary>
    /// <exception cref="DnsQueryException">The name cannot be looked up.</exception>
    public static async Task<IPAddress> ResolveAsync(string server, CancellationToken ct = default)
    {
        server = server.Trim().TrimStart('@');
        if (IPAddress.TryParse(server, out var address)) return address;
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(server, ct);
            return addresses.OrderBy(a => a.AddressFamily != AddressFamily.InterNetwork).FirstOrDefault()
                   ?? throw new DnsQueryException($"\"{server}\" has no address.");
        }
        catch (SocketException)
        {
            throw new DnsQueryException($"Could not find the server \"{server}\" (use its IP address, or check the name).");
        }
    }
}

/// <summary>A DNS server to ask, and what to call it.</summary>
public sealed record DnsServerChoice(string Label, IPAddress Address);
