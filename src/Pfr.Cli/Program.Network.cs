using System.Net;
using Pfr.Core.Clients;
using Pfr.Core.Network;

namespace Pfr.Cli;

// Network tools: ping, dig, and SSH command capture (spec §11). Only hosts named on the command line (or taken from a
// grid the user names) are contacted.
public static partial class Program
{
    /// <summary>Password for the SSH account, for scripted use; otherwise it is prompted without echo.</summary>
    public const string SshPasswordVariable = "PFR_SSH_PASSWORD";

    private static int IntOption(Args cli, string name, int fallback)
    {
        if (cli.Value(name) is not { } text) return fallback;
        return int.TryParse(text, out var value) && value > 0
            ? value
            : throw new ArgumentException($"{name} needs a whole number greater than zero (not \"{text}\").");
    }

    /// <summary>Ctrl+C stops the work and still prints what was found, instead of ending the program.</summary>
    private static CancellationTokenSource CancelOnCtrlC()
    {
        var source = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            source.Cancel();
        };
        return source;
    }

    /// <summary>The latest snapshot of a client's grid, for commands that take their targets from it.</summary>
    private static Pfr.Core.Nios.Model.GridInventory LatestInventory(Args cli, out ClientWorkspace workspace, out Client client, out ClientGrid grid)
    {
        if (cli.Value("--client") is not { } clientName || cli.Value("--grid") is not { } gridName)
            throw new ArgumentException("This needs --client <name> and --grid <name>.");
        workspace = Unlock(cli);
        client = FindClient(workspace, clientName);
        grid = FindGrid(client, gridName);
        return workspace.LoadLatest(client.Id, grid.Id)?.Inventory
               ?? throw new InvalidOperationException($"{client.Name} › {grid.Name} has no snapshots yet.");
    }

    // ---- ping ---------------------------------------------------------------------------

    private const string PingUsage =
        "Usage: pfr ping <host>... [--file <list>] [--members --client <name> --grid <name> [--no-nodes]]\n" +
        "                [--count <n>] [--timeout <ms>] [--interval <ms>] [--ipv6]";

    private static int RunPing(Args cli)
    {
        var targets = cli.Positional.Select(h => new PingTarget(h, h)).ToList();
        if (cli.Value("--file") is { } file) targets.AddRange(PingTargets.Parse(File.ReadAllText(file)));

        if (cli.Has("--members"))
        {
            var inventory = LatestInventory(cli, out var workspace, out _, out _);
            using var store = workspace.Store;
            targets.AddRange(PingTargets.FromMembers(inventory, includeNodes: !cli.Has("--no-nodes")));
        }

        targets = targets.DistinctBy(t => t.Host, StringComparer.OrdinalIgnoreCase).ToList();
        if (targets.Count == 0) return Fail(PingUsage, 2);

        var settings = new PingSettings
        {
            Count = IntOption(cli, "--count", 4),
            Timeout = TimeSpan.FromMilliseconds(IntOption(cli, "--timeout", 2000)),
            Interval = TimeSpan.FromMilliseconds(IntOption(cli, "--interval", 1000)),
            PreferIpv6 = cli.Has("--ipv6"),
        };

        using var cancel = CancelOnCtrlC();
        var results = PingRunner.RunAsync(targets, settings, ct: cancel.Token).GetAwaiter().GetResult();
        Console.Write(PingReport.Text(results));
        return results.All(r => r.Received > 0) ? 0 : 1;
    }

    // ---- dig ----------------------------------------------------------------------------

    private const string DigUsage =
        "Usage: pfr dig [@server] <name> [type] [class] [+short] [+tcp] [+dnssec] [+cd] [+norecurse] [--port <n>] [--timeout <s>]\n" +
        "       pfr dig [@server] -x <address>\n" +
        "       pfr dig --batch <file> [@server] [type]                     names (and addresses) from a file, one per line\n" +
        "       pfr dig --compare <name> [type] (--client <n> --grid <n> | --servers <a,b,...>)\n" +
        "                                                                  ask every DNS member (or the listed servers) and compare";

    private static int RunDig(Args cli)
    {
        // dig's own spellings (+short, @server, -x) are understood as well as the --options.
        var server = cli.Value("--server");
        bool tcp = cli.Has("--tcp"), dnssec = cli.Has("--dnssec"), shortForm = cli.Has("--short");
        bool checkingDisabled = cli.Has("--cd"), noRecurse = cli.Has("--no-recurse"), reverse = cli.Has("--reverse");
        var words = new List<string>();
        foreach (var token in cli.Positional)
        {
            if (token.StartsWith('@')) server = token[1..];
            else if (token == "+short") shortForm = true;
            else if (token == "+tcp") tcp = true;
            else if (token == "+dnssec") dnssec = true;
            else if (token is "+cd" or "+cdflag") checkingDisabled = true;
            else if (token is "+norecurse" or "+norec") noRecurse = true;
            else if (token == "-x") reverse = true;
            else words.Add(token);
        }

        var type = (DnsType?)null;
        var dnsClass = DnsClass.IN;
        string? name = null;
        foreach (var word in words)
        {
            if (name is null) name = word;
            else if (DnsTypes.TryParse(word, out DnsType parsedType)) type = parsedType;
            else if (DnsTypes.TryParse(word, out DnsClass parsedClass)) dnsClass = parsedClass;
            else throw new ArgumentException($"\"{word}\" is not a record type or class.");
        }

        using var cancel = CancelOnCtrlC();
        var timeout = TimeSpan.FromSeconds(IntOption(cli, "--timeout", 5));
        var port = IntOption(cli, "--port", 53);

        DnsQueryOptions Options(IPAddress serverAddress) => new()
        {
            Server = serverAddress, Port = port, Tcp = tcp, Dnssec = dnssec, CheckingDisabled = checkingDisabled,
            Recurse = !noRecurse, Timeout = timeout,
        };

        if (cli.Has("--compare")) return DigCompare(cli, name, type, dnsClass, Options, cancel.Token);

        var address = ResolveDigServer(server);

        if (cli.Value("--batch") is { } batchFile)
        {
            var (items, problems) = DnsBatch.Parse(File.ReadAllText(batchFile), type ?? DnsType.A);
            foreach (var problem in problems) Console.Error.WriteLine(problem);
            var results = DnsBatch.RunAsync(items, Options(address), ct: cancel.Token).GetAwaiter().GetResult();
            Console.Write(DnsBatch.Text(results));
            return results.Any(r => r.Error is not null) ? 1 : 0;
        }

        if (name is null) return Fail(DigUsage, 2);
        if (reverse)
        {
            if (!IPAddress.TryParse(name, out var asked)) throw new ArgumentException($"-x needs an IP address (not \"{name}\").");
            name = DnsNames.ReverseName(asked);
            type ??= DnsType.PTR;
        }

        var response = DnsLookup.QueryAsync(new DnsQuestion(name, type ?? DnsType.A, dnsClass), Options(address), ct: cancel.Token)
            .GetAwaiter().GetResult();
        Console.Write(DigFormatter.Format(response, shortForm));
        return 0;
    }

    /// <summary>The server to ask: the one named, or else this computer's own resolver (and says so).</summary>
    private static IPAddress ResolveDigServer(string? server)
    {
        if (!string.IsNullOrWhiteSpace(server)) return DnsServers.ResolveAsync(server).GetAwaiter().GetResult();

        var system = DnsServers.Resolvers().FirstOrDefault()
                     ?? throw new ArgumentException("No server was given and this computer has no DNS server configured. Add @server.");
        Console.Error.WriteLine($";; no server given: using this computer's resolver, {system}");
        return system;
    }

    private static int DigCompare(Args cli, string? name, DnsType? type, DnsClass dnsClass,
        Func<IPAddress, DnsQueryOptions> options, CancellationToken ct)
    {
        if (name is null) return Fail(DigUsage, 2);

        List<DnsServerChoice> servers;
        if (cli.Value("--servers") is { } list)
        {
            servers = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => new DnsServerChoice(s, DnsServers.ResolveAsync(s).GetAwaiter().GetResult())).ToList();
        }
        else
        {
            var inventory = LatestInventory(cli, out var workspace, out _, out _);
            workspace.Store.Dispose();
            servers = DnsServers.FromMembers(inventory);
        }
        if (servers.Count == 0) return Fail("No DNS servers to compare: none of the grid's members serves DNS (or --servers was empty).");

        var comparison = DnsCompare.RunAsync(new DnsQuestion(name, type ?? DnsType.A, dnsClass), servers, options(servers[0].Address), ct: ct)
            .GetAwaiter().GetResult();
        Console.Write(DnsCompare.Text(comparison));
        return comparison.AllAgree ? 0 : 1; // so a script can tell
    }

    // ---- ssh ----------------------------------------------------------------------------

    private const string SshUsage =
        "Usage: pfr ssh-run --client <name> (--host <h>... | --grid <name> --members) --command \"show version\" [--command ...]\n" +
        "                   [--port <n>] [--user <id> | --account ro|rw] [--allow-changes] [--pin SHA256:...]\n" +
        "                   [--timeout <s>] [--json] [--out <file>]\n" +
        "       pfr ssh-keys --client <name>                                  list the pinned host keys\n" +
        "       pfr ssh-key-remove --client <name> --host <h> [--port <n>]    forget a pinned host key";

    private static int RunSsh(Args cli)
    {
        if (cli.Value("--client") is not { } clientName) return Fail(SshUsage, 2);
        var commands = cli.All("--command");
        if (commands.Count == 0) return Fail(SshUsage, 2);

        var workspace = Unlock(cli);
        using var _ = workspace.Store;
        var client = FindClient(workspace, clientName);
        var grid = cli.Value("--grid") is { } gridName ? FindGrid(client, gridName) : null;
        var port = IntOption(cli, "--port", 22);

        var targets = cli.All("--host").Select(h => new SshTarget(h, h, port)).ToList();
        if (cli.Has("--members"))
        {
            if (grid is null) return Fail("--members needs --grid <name>.", 2);
            var inventory = workspace.LoadLatest(client.Id, grid.Id)?.Inventory
                            ?? throw new InvalidOperationException($"{client.Name} › {grid.Name} has no snapshots yet.");
            targets.AddRange(inventory.Members.Select(m => new SshTarget(m.HostName, m.Ipv4Address ?? m.Ipv6Address ?? m.HostName, port)));
        }
        if (targets.Count == 0) return Fail(SshUsage, 2);

        var account = SshAccountFor(cli, workspace, client, grid);
        var keys = new ClientSshHostKeys(workspace, client.Id);

        // --pin is the fingerprint the engineer expects for a host with no pinned key yet: it is accepted only if the host
        // presents exactly that key, and only then pinned.
        string? expectedPin = null;
        if (cli.Value("--pin") is { } pin)
        {
            if (targets.Count != 1) throw new ArgumentException("--pin is for one host. Pin each host's key when asked, or run them one at a time.");
            expectedPin = SshFingerprint.Normalize(pin)
                          ?? throw new ArgumentException("--pin should look like SHA256:xxxxxxxx... (43 characters after SHA256:).");
        }

        var options = new SshRunOptions
        {
            AllowChanges = cli.Has("--allow-changes"),
            CommandTimeout = TimeSpan.FromSeconds(IntOption(cli, "--timeout", 60)),
        };

        using var cancel = CancelOnCtrlC();
        var sessions = new SshRunner(new SshNetClient(), keys)
            .RunAsync(targets, account, commands, options, (ex, token) => ConfirmSshKey(ex, expectedPin), ct: cancel.Token)
            .GetAwaiter().GetResult();

        var output = cli.Has("--json") ? SshReport.Json(sessions) : SshReport.Text(sessions);
        if (cli.Value("--out") is { } path) File.WriteAllText(path, output);
        else Console.Write(output);
        return sessions.All(s => s.Succeeded) ? 0 : 1;
    }

    /// <summary>The account to sign in with: a user id given here, or the grid's saved read-only (or read-write) account.</summary>
    private static SshAccount SshAccountFor(Args cli, ClientWorkspace workspace, Client client, ClientGrid? grid)
    {
        if (cli.Value("--user") is { } user)
            return new SshAccount(user, AccountPassword(SshPasswordVariable, "SSH", user));

        var kind = (cli.Value("--account") ?? "ro").ToLowerInvariant();
        if (kind is not ("ro" or "rw")) throw new ArgumentException("--account should be ro or rw.");
        if (grid is null) throw new ArgumentException("Give --user <id>, or --grid <name> to use that grid's saved account.");

        var connection = workspace.GetConnection(client.Id, grid.Id)
                         ?? throw new ArgumentException($"{grid.Name} has no saved connection. Give --user <id>, or save its connection first.");
        var credential = kind == "rw" ? connection.ReadWrite : connection.ReadOnly;
        return credential is null
            ? throw new ArgumentException($"{grid.Name}'s connection has no {(kind == "rw" ? "read-write" : "read-only")} account. Give --user <id>.")
            : new SshAccount(credential.Username, credential.Password);
    }

    /// <summary>
    /// Decides whether to trust the key a host with no pinned key presented: yes if it is the one given with --pin;
    /// otherwise the engineer is shown it and asked. With no one to ask, says how to pin it instead.
    /// </summary>
    private static Task<bool> ConfirmSshKey(SshHostKeyException ex, string? expectedPin)
    {
        var key = ex.Presented;
        if (expectedPin is not null)
        {
            if (key.Fingerprint == expectedPin) return Task.FromResult(true);
            Console.Error.WriteLine($"--pin does not match the key {ex.Target.Host} presented ({key.Fingerprint}). Nothing was pinned.");
            return Task.FromResult(false);
        }

        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine($"{ex.Target.Host} has no pinned SSH host key. To trust the one it presented, run again with --pin {key.Fingerprint}");
            return Task.FromResult(false);
        }

        Console.Error.WriteLine($"{ex.Target.Host} presented this SSH host key:\n  {key.Algorithm}  {key.Fingerprint}");
        Console.Error.WriteLine("Compare it with the fingerprint you expect for this appliance before trusting it.");
        Console.Error.Write("Trust and pin this key? [y/N] ");
        return Task.FromResult(string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase));
    }

    private static int ListSshKeys(Args cli)
    {
        if (cli.Value("--client") is not { } clientName) return Fail("Usage: pfr ssh-keys --client <name>", 2);
        var workspace = Unlock(cli);
        using var _ = workspace.Store;
        var client = FindClient(workspace, clientName);

        var keys = workspace.GetSshHostKeys(client.Id);
        if (keys.Count == 0)
        {
            Console.WriteLine($"{client.Name} has no pinned SSH host keys.");
            return 0;
        }
        foreach (var key in keys)
            Console.WriteLine($"{key.Host}:{key.Port}  {key.Fingerprint}  {key.Algorithm}  pinned {key.PinnedUtc.ToLocalTime():yyyy-MM-dd}");
        return 0;
    }

    private static int RemoveSshKey(Args cli)
    {
        if (cli.Value("--client") is not { } clientName || cli.Value("--host") is not { } host)
            return Fail("Usage: pfr ssh-key-remove --client <name> --host <h> [--port <n>]", 2);
        var workspace = Unlock(cli);
        using var _ = workspace.Store;
        var client = FindClient(workspace, clientName);
        var port = IntOption(cli, "--port", 22);

        if (workspace.FindSshHostKey(client.Id, host, port) is null) return Fail($"No key is pinned for {host}:{port}.");
        workspace.DeleteSshHostKey(client.Id, host, port);
        Console.WriteLine($"Forgot the pinned key for {host}:{port}. It will have to be confirmed again.");
        return 0;
    }
}
