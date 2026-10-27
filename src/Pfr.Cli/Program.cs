using System.Text.Json;
using System.Text.Json.Serialization;
using Pfr.Core;
using Pfr.Core.Clients;
using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;
using Pfr.Core.Storage;

namespace Pfr.Cli;

public static class Program
{
    /// <summary>Master password for scripted use; otherwise it is prompted without echo.</summary>
    public const string PasswordVariable = "PFR_MASTER_PASSWORD";

    public static int Main(string[] args)
    {
        var cli = new Args(args);
        if (cli.Command is null or "help" or "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return cli.Command switch
            {
                "version" or "--version" => Version(),
                "inspect" => Inspect(cli),
                "init" => Init(cli),
                "clients" => ListClients(cli),
                "client-add" => AddClient(cli),
                "import" => Import(cli),
                "grids" => ListGrids(cli),
                "show" => Show(cli),
                _ => Fail($"Unknown command: {cli.Command}\nRun 'pfr help' for usage.", 2),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or System.Xml.XmlException or ArgumentException or InvalidOperationException)
        {
            return Fail($"Error: {ex.Message}");
        }
    }

    private static int Fail(string message, int code = 1)
    {
        Console.Error.WriteLine(message);
        return code;
    }

    private static int Version()
    {
        Console.WriteLine(AppInfo.Banner);
        return 0;
    }

    // ---- Backups -------------------------------------------------------------

    private static int Inspect(Args cli)
    {
        if (cli.Positional.Count != 1) return Fail("Usage: pfr inspect <backup.bak | onedb.xml> [--json]", 2);
        var path = cli.Positional[0];
        if (!File.Exists(path)) return Fail($"File not found: {path}");

        var inventory = BackupParser.ParseFile(path);
        BackupParser.EnsureGridName(inventory, Path.GetFileName(path));
        Print(inventory, cli.Has("--json"));
        return 0;
    }

    private static void Print(GridInventory inventory, bool json)
    {
        if (json)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Converters = { new JsonStringEnumConverter() },
            };
            Console.WriteLine(JsonSerializer.Serialize(inventory, options));
        }
        else
        {
            Console.Write(InventorySummary.Render(inventory));
        }
    }

    // ---- Client data ----------------------------------------------------------

    private static SecureStore OpenStore(Args cli) => new(DataLocation.Resolve(cli.Value("--data")));

    private static int Init(Args cli)
    {
        using var store = OpenStore(cli);
        if (store.State != StoreState.NotInitialized) return Fail($"Client data already exists in {store.Root}.");
        if (DataLocation.CheckWritable(store.Root) is { } problem) return Fail(problem);

        var password = Environment.GetEnvironmentVariable(PasswordVariable);
        if (string.IsNullOrEmpty(password))
        {
            password = ReadPassword($"New master password (at least {SecureStore.MinimumPasswordLength} characters): ");
            if (ReadPassword("Repeat master password: ") != password) return Fail("The passwords don't match.");
        }

        store.Initialize(password);
        Console.WriteLine($"Created encrypted client data in {store.Root}.");
        Console.WriteLine("If you forget the master password the data cannot be recovered.");
        return 0;
    }

    /// <summary>Opens and unlocks the store, or explains why it can't.</summary>
    private static ClientWorkspace Unlock(Args cli)
    {
        var store = OpenStore(cli);
        if (store.State == StoreState.NotInitialized)
            throw new InvalidOperationException($"No client data in {store.Root}. Run 'pfr init' first.");

        var password = Environment.GetEnvironmentVariable(PasswordVariable);
        if (string.IsNullOrEmpty(password)) password = ReadPassword("Master password: ");
        if (!store.Unlock(password)) throw new InvalidOperationException("That password is not correct.");
        return new ClientWorkspace(store);
    }

    private static int ListClients(Args cli)
    {
        var ws = Unlock(cli);
        using var _ = ws.Store;
        var clients = ws.ListClients();
        if (clients.Count == 0)
        {
            Console.WriteLine("No clients yet. Add one with: pfr client-add <name>");
            return 0;
        }

        foreach (var c in clients)
        {
            Console.WriteLine($"{c.Name}  ({Count(c.Grids.Count, "grid")}, {Count(c.Contacts.Count, "contact")})");
            foreach (var g in c.Grids.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(g.LatestSnapshot is { } s
                    ? $"  {g.Name,-30} latest {s.ImportedUtc.ToLocalTime():yyyy-MM-dd}  NIOS {Short(s.NiosVersion)}  ({Count(g.Snapshots.Count, "snapshot")})"
                    : $"  {g.Name,-30} no snapshots");
            }
        }
        return 0;
    }

    private static int AddClient(Args cli)
    {
        if (cli.Positional.Count != 1) return Fail("Usage: pfr client-add <name>", 2);
        var ws = Unlock(cli);
        using var _ = ws.Store;
        var client = ws.CreateClient(cli.Positional[0]);
        Console.WriteLine($"Added client {client.Name}.");
        return 0;
    }

    private static int Import(Args cli)
    {
        if (cli.Value("--client") is not { } clientName || cli.Positional.Count != 1)
            return Fail("Usage: pfr import --client <name> <backup> [--label <text>]", 2);
        var path = cli.Positional[0];
        if (!File.Exists(path)) return Fail($"File not found: {path}");

        var ws = Unlock(cli);
        using var _ = ws.Store;
        var client = FindClient(ws, clientName);
        var duplicate = ws.FindDuplicate(client.Id, ClientWorkspace.Sha256Of(path));
        var (grid, info) = ws.ImportBackup(client.Id, path, label: cli.Value("--label") ?? "");

        Console.WriteLine($"Imported {Path.GetFileName(path)} into {client.Name} › {grid.Name}: " +
                          $"{Count(info.Members, "member")}, {Count(info.Zones, "zone")}, {Count(info.Networks, "network")}, " +
                          $"{Count(info.DhcpRanges, "DHCP range")}, {Count(info.Records, "record")}.");
        if (duplicate is { } d)
            Console.WriteLine($"Note: the same file was already imported on {d.Snapshot.ImportedUtc.ToLocalTime():yyyy-MM-dd}.");
        return 0;
    }

    private static int ListGrids(Args cli)
    {
        if (cli.Value("--client") is not { } clientName) return Fail("Usage: pfr grids --client <name>", 2);
        var ws = Unlock(cli);
        using var _ = ws.Store;
        var client = FindClient(ws, clientName);
        if (client.Grids.Count == 0)
        {
            Console.WriteLine($"{client.Name} has no grids yet. Import a backup with: pfr import --client \"{client.Name}\" <backup>");
            return 0;
        }

        foreach (var g in client.Grids.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(g.Name);
            foreach (var s in g.Snapshots.OrderByDescending(s => s.ImportedUtc))
            {
                var label = s.Label.Length > 0 ? $"  \"{s.Label}\"" : "";
                Console.WriteLine($"  {s.ImportedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  NIOS {Short(s.NiosVersion)}  " +
                                  $"{Count(s.Members, "member")}, {Count(s.Zones, "zone")}, {Count(s.Records, "record")}  {s.SourceFileName}{label}");
            }
        }
        return 0;
    }

    /// <summary>Summarises a client grid's latest snapshot: no backup file needed.</summary>
    private static int Show(Args cli)
    {
        if (cli.Value("--client") is not { } clientName || cli.Value("--grid") is not { } gridName)
            return Fail("Usage: pfr show --client <name> --grid <name> [--json]", 2);
        var ws = Unlock(cli);
        using var _ = ws.Store;
        var client = FindClient(ws, clientName);
        var grid = client.Grids.FirstOrDefault(g => string.Equals(g.Name, gridName, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException(
                       $"{client.Name} has no grid named \"{gridName}\". Grids: {string.Join(", ", client.Grids.Select(g => g.Name).Order(StringComparer.OrdinalIgnoreCase))}");
        if (ws.LoadLatest(client.Id, grid.Id) is not { } latest)
            return Fail($"{client.Name} › {grid.Name} has no snapshots yet.");

        if (!cli.Has("--json"))
            Console.WriteLine($"{client.Name} › {grid.Name}, snapshot {latest.Snapshot.ImportedUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n");
        Print(latest.Inventory, cli.Has("--json"));
        return 0;
    }

    private static Client FindClient(ClientWorkspace ws, string name) =>
        ws.ListClients().FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"No client named \"{name}\". Run 'pfr clients' to list them.");

    // ---- Helpers ----------------------------------------------------------------

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string Short(string? version) => version?.Split('-')[0] ?? "?";

    /// <summary>Reads a password without echoing it (falls back to a plain line when input is redirected).</summary>
    private static string ReadPassword(string prompt)
    {
        Console.Error.Write(prompt);
        if (Console.IsInputRedirected)
        {
            var line = Console.ReadLine() ?? "";
            Console.Error.WriteLine();
            return line;
        }

        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0) chars.RemoveAt(chars.Count - 1);
                continue;
            }
            if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        Console.Error.WriteLine();
        return new string(chars.ToArray());
    }

    private static void PrintHelp()
    {
        Console.WriteLine(AppInfo.Banner);
        Console.WriteLine();
        Console.WriteLine("Usage: pfr <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Backups:");
        Console.WriteLine("  inspect <file> [--json]                       Summarize a NIOS grid backup (.bak) or onedb.xml");
        Console.WriteLine();
        Console.WriteLine("Client data (encrypted; master password prompted, or set " + PasswordVariable + "):");
        Console.WriteLine("  init                                          Create the encrypted client data folder");
        Console.WriteLine("  clients                                       List clients and their grids");
        Console.WriteLine("  client-add <name>                             Add a client");
        Console.WriteLine("  import --client <name> <file> [--label <t>]   Store a backup as a snapshot of its grid");
        Console.WriteLine("  grids --client <name>                         List a client's grids and snapshots");
        Console.WriteLine("  show --client <name> --grid <name> [--json]   Summarize a grid's latest snapshot");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine($"  --data <folder>   Client data folder (default: {DataLocation.FolderName} next to pfr, or ${DataLocation.EnvironmentVariable})");
        Console.WriteLine();
        Console.WriteLine("Other:");
        Console.WriteLine("  help | version");
    }

    /// <summary>Minimal argument parser: a command, --name value options, --flags and positionals.</summary>
    private sealed class Args
    {
        private static readonly HashSet<string> Flags = ["--json"];
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

        public Args(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (i == 0 && !a.StartsWith("--", StringComparison.Ordinal) || i == 0 && a is "--help" or "--version")
                {
                    Command = a;
                }
                else if (Flags.Contains(a))
                {
                    _flags.Add(a);
                }
                else if (a.StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
                {
                    _values[a] = args[++i];
                }
                else
                {
                    Positional.Add(a);
                }
            }
        }

        public string? Command { get; }
        public List<string> Positional { get; } = [];
        public string? Value(string name) => _values.GetValueOrDefault(name);
        public bool Has(string flag) => _flags.Contains(flag);
    }
}
