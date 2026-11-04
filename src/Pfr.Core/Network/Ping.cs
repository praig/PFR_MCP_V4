using System.Net;
using System.Net.Sockets;
using System.Text;
using Pfr.Core.Nios.Model;

namespace Pfr.Core.Network;

/// <summary>A host to ping: what to call it, and its name or address.</summary>
public sealed record PingTarget(string Label, string Host);

public sealed record PingSettings
{
    /// <summary>Pings sent to each target.</summary>
    public int Count { get; init; } = 4;

    /// <summary>How long to wait for each reply.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Pause between one ping and the next to the same target.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);

    public int PayloadBytes { get; init; } = 32;
    public int Ttl { get; init; } = 128;

    /// <summary>Use an IPv6 address when a name has both.</summary>
    public bool PreferIpv6 { get; init; }

    /// <summary>Targets pinged at the same time.</summary>
    public int MaxParallel { get; init; } = 16;
}

public enum PingOutcome
{
    Success,
    TimedOut,

    /// <summary>A router or the host said it cannot be reached.</summary>
    Unreachable,

    /// <summary>The ping could not be sent (no permission, no route...).</summary>
    Error,
}

public sealed record PingAttempt(int Sequence, PingOutcome Outcome, TimeSpan? RoundTrip, string? Detail, string? ReplyFrom);

/// <summary>What has happened to one target so far. Each progress report is a new, unchanging snapshot.</summary>
public sealed record PingResult(
    PingTarget Target, string? Address, string? Error, IReadOnlyList<PingAttempt> Attempts, bool Finished)
{
    public int Sent => Attempts.Count;
    public int Received => Attempts.Count(a => a.Outcome == PingOutcome.Success);
    public double LossPercent => Sent == 0 ? 0 : (Sent - Received) * 100.0 / Sent;

    private IEnumerable<TimeSpan> RoundTrips => Attempts.Where(a => a.RoundTrip is not null).Select(a => a.RoundTrip!.Value);
    public TimeSpan? Min => RoundTrips.Any() ? RoundTrips.Min() : null;
    public TimeSpan? Max => RoundTrips.Any() ? RoundTrips.Max() : null;
    public TimeSpan? Average => RoundTrips.Any() ? TimeSpan.FromTicks((long)RoundTrips.Average(t => t.Ticks)) : null;

    /// <summary>"up", "down", "30% loss", "pending", or why the name could not be resolved.</summary>
    public string Status =>
        Error is not null ? $"cannot resolve: {Error}"
        : Sent == 0 ? "pending"
        : Received == 0 ? (Attempts.Any(a => a.Outcome == PingOutcome.Unreachable) ? "unreachable"
            : Attempts.Any(a => a.Outcome == PingOutcome.Error) ? "error: " + Attempts.First(a => a.Outcome == PingOutcome.Error).Detail
            : "down")
        : Received < Sent ? $"{LossPercent:0}% loss"
        : "up";
}

/// <summary>Sends one ping. The system's own ping is used; tests replace it.</summary>
public interface IPinger
{
    Task<PingAttempt> PingAsync(IPAddress address, int sequence, PingSettings settings, CancellationToken ct);
}

/// <summary>Pings with the operating system's ICMP support (no extra software, no administrator rights needed).</summary>
public sealed class SystemPinger : IPinger
{
    public async Task<PingAttempt> PingAsync(IPAddress address, int sequence, PingSettings settings, CancellationToken ct)
    {
        var payload = new byte[Math.Clamp(settings.PayloadBytes, 0, 1400)];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)('a' + i % 23);

        using var ping = new System.Net.NetworkInformation.Ping();
        var options = new System.Net.NetworkInformation.PingOptions(Math.Clamp(settings.Ttl, 1, 255), false);
        try
        {
            var reply = await ping.SendPingAsync(address, (int)settings.Timeout.TotalMilliseconds, payload, options).WaitAsync(ct);
            return reply.Status switch
            {
                System.Net.NetworkInformation.IPStatus.Success =>
                    new PingAttempt(sequence, PingOutcome.Success, TimeSpan.FromMilliseconds(reply.RoundtripTime), null, reply.Address?.ToString()),
                System.Net.NetworkInformation.IPStatus.TimedOut =>
                    new PingAttempt(sequence, PingOutcome.TimedOut, null, null, null),
                System.Net.NetworkInformation.IPStatus.DestinationNetworkUnreachable
                    or System.Net.NetworkInformation.IPStatus.DestinationHostUnreachable
                    or System.Net.NetworkInformation.IPStatus.DestinationProtocolUnreachable
                    or System.Net.NetworkInformation.IPStatus.DestinationPortUnreachable
                    or System.Net.NetworkInformation.IPStatus.DestinationUnreachable =>
                    new PingAttempt(sequence, PingOutcome.Unreachable, null, reply.Status.ToString(), reply.Address?.ToString()),
                _ => new PingAttempt(sequence, PingOutcome.Error, null, reply.Status.ToString(), reply.Address?.ToString()),
            };
        }
        catch (System.Net.NetworkInformation.PingException ex)
        {
            return new PingAttempt(sequence, PingOutcome.Error, null, ex.InnerException?.Message ?? ex.Message, null);
        }
    }
}

/// <summary>
/// Pings a list of targets, several at a time. Only the hosts the user names are contacted. Cancelling stops quickly
/// and returns what has been measured so far.
/// </summary>
public static class PingRunner
{
    /// <param name="targets">Who to ping.</param>
    /// <param name="settings">How many pings, how long to wait...</param>
    /// <param name="pinger">Sends the pings (the system's by default).</param>
    /// <param name="resolve">Turns a name into addresses (the system's by default).</param>
    /// <param name="progress">Told about every new result for a target as it happens.</param>
    public static async Task<IReadOnlyList<PingResult>> RunAsync(
        IReadOnlyList<PingTarget> targets, PingSettings settings, IPinger? pinger = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        IProgress<PingResult>? progress = null, CancellationToken ct = default)
    {
        pinger ??= new SystemPinger();
        resolve ??= (host, token) => System.Net.Dns.GetHostAddressesAsync(host, token);

        var results = targets.Select(t => new PingResult(t, null, null, [], false)).ToArray();
        using var gate = new SemaphoreSlim(Math.Clamp(settings.MaxParallel, 1, 128));

        var tasks = targets.Select(async (target, index) =>
        {
            try
            {
                await gate.WaitAsync(ct);
                try
                {
                    results[index] = await RunOneAsync(target, settings, pinger, resolve, progress, r => results[index] = r, ct);
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped: keep whatever was recorded for this target.
            }
        }).ToArray();

        await Task.WhenAll(tasks);
        return results;
    }

    private static async Task<PingResult> RunOneAsync(PingTarget target, PingSettings settings, IPinger pinger,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve, IProgress<PingResult>? progress,
        Action<PingResult> record, CancellationToken ct)
    {
        var result = new PingResult(target, null, null, [], false);
        void Update(PingResult next)
        {
            result = next;
            record(next);
            progress?.Report(next);
        }

        IPAddress address;
        try
        {
            address = await AddressOfAsync(target.Host, settings.PreferIpv6, resolve, ct);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or InvalidOperationException)
        {
            Update(result with { Error = ex is SocketException ? "name not found" : ex.Message, Finished = true });
            return result;
        }

        Update(result with { Address = address.ToString() });

        var count = Math.Clamp(settings.Count, 1, 1000);
        var attempts = new List<PingAttempt>();
        for (var i = 1; i <= count; i++)
        {
            ct.ThrowIfCancellationRequested();
            attempts.Add(await pinger.PingAsync(address, i, settings, ct));
            Update(result with { Attempts = attempts.ToArray(), Finished = i == count });
            if (i < count && settings.Interval > TimeSpan.Zero) await Task.Delay(settings.Interval, ct);
        }
        return result;
    }

    private static async Task<IPAddress> AddressOfAsync(string host, bool preferIpv6,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken ct)
    {
        host = host.Trim();
        if (IPAddress.TryParse(host, out var literal)) return literal;
        if (host.Length == 0) throw new ArgumentException("No host name.");

        var addresses = await resolve(host, ct);
        var wanted = preferIpv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        return addresses.FirstOrDefault(a => a.AddressFamily == wanted)
               ?? addresses.FirstOrDefault()
               ?? throw new InvalidOperationException("the name has no addresses");
    }
}

/// <summary>Ways to make a list of ping targets.</summary>
public static class PingTargets
{
    /// <summary>
    /// Names or addresses separated by spaces, commas, semicolons or new lines. "#" starts a comment. Repeats
    /// are dropped. A line with one host and a comment (<c>10.0.0.1  # gm.lab.test</c>) uses the comment as the
    /// target's label, which is how a list of members keeps their names.
    /// </summary>
    public static List<PingTarget> Parse(string? text)
    {
        var targets = new List<PingTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in (text ?? "").Split('\n'))
        {
            var parts = rawLine.Split('#', 2);
            var hosts = parts[0].Split([' ', '\t', ',', ';', '\r'], StringSplitOptions.RemoveEmptyEntries);
            var comment = parts.Length > 1 ? parts[1].Trim() : "";
            foreach (var host in hosts)
            {
                var label = hosts.Length == 1 && comment.Length > 0 ? comment : host;
                if (seen.Add(host)) targets.Add(new PingTarget(label, host));
            }
        }
        return targets;
    }

    /// <summary>
    /// Every member of a grid by its address, and each node of an HA pair on its own, so a whole grid can be checked
    /// at once.
    /// </summary>
    public static List<PingTarget> FromMembers(GridInventory inventory, bool includeNodes = true)
    {
        var targets = new List<PingTarget>();
        foreach (var member in inventory.Members)
        {
            targets.Add(new PingTarget(member.HostName, member.Ipv4Address ?? member.Ipv6Address ?? member.HostName));
            if (!includeNodes || member.Nodes.Count < 2) continue;

            foreach (var node in member.Nodes)
            {
                var address = node.Ipv4Address ?? node.Ipv6Address;
                if (address is null) continue;
                targets.Add(new PingTarget($"{member.HostName} (node {node.Position + 1})", address));
            }
        }
        return targets;
    }
}

/// <summary>A ping summary as plain text.</summary>
public static class PingReport
{
    public static string Text(IReadOnlyList<PingResult> results)
    {
        if (results.Count == 0) return "Nothing to ping.\n";

        var rows = results.Select(r => new[]
        {
            r.Target.Label, r.Address ?? "", r.Sent.ToString(), r.Received.ToString(),
            r.Sent == 0 ? "" : $"{r.LossPercent:0}%", Rtt(r.Min), Rtt(r.Average), Rtt(r.Max), r.Status,
        }).ToList();
        string[] headers = ["Target", "Address", "Sent", "Received", "Loss", "Min", "Avg", "Max", "Status"];

        var widths = headers.Select((h, i) => Math.Max(h.Length, rows.Max(r => r[i].Length))).ToArray();
        var text = new StringBuilder();
        void Line(string[] cells) =>
            text.AppendLine(string.Join("  ", cells.Select((c, i) => i is >= 2 and <= 7 ? c.PadLeft(widths[i]) : c.PadRight(widths[i]))).TrimEnd());

        Line(headers);
        foreach (var row in rows) Line(row);
        return text.ToString();
    }

    /// <summary>"&lt;1 ms", "12 ms", or "-" when there was no reply.</summary>
    public static string Rtt(TimeSpan? roundTrip) => roundTrip switch
    {
        null => "-",
        { } t when t < TimeSpan.FromMilliseconds(1) => "<1 ms",
        { } t => $"{t.TotalMilliseconds:0} ms",
    };
}
