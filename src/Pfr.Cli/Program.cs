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

    /// <summary>Grid account passwords for scripted use; otherwise they are prompted without echo.</summary>
    public const string ReadOnlyPasswordVariable = "PFR_RO_PASSWORD";
    public const string ReadWritePasswordVariable = "PFR_RW_PASSWORD";

    /// <summary>Secrets for client site logins, for scripted use; otherwise they are prompted without echo.</summary>
    public const string LoginPasswordVariable = "PFR_LOGIN_PASSWORD";
    public const string LoginApiKeyVariable = "PFR_LOGIN_API_KEY";

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
                "connection-set" => SetConnection(cli),
                "connection-show" => ShowConnection(cli),
                "connection-remove" => RemoveConnection(cli),
                "logins" => ListLogins(cli),
                "login-set" => SetLogin(cli),
                "login-show" => ShowLogin(cli),
                "login-remove" => RemoveLogin(cli),
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
            Console.WriteLine(ws.HasConnection(client.Id, g.Id) ? $"{g.Name}   [connection saved]" : g.Name);
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
        var grid = FindGrid(client, gridName);
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

    private static ClientGrid FindGrid(Client client, string name) =>
        client.Grids.FirstOrDefault(g => string.Equals(g.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException(
            $"{client.Name} has no grid named \"{name}\". Grids: {string.Join(", ", client.Grids.Select(g => g.Name).Order(StringComparer.OrdinalIgnoreCase))}");

    // ---- Grid connections ------------------------------------------------------

    private const string ConnectionSetUsage =
        "Usage: pfr connection-set --client <name> --grid <name> [--address <host>] [--port <n>] [--wapi-version <v|auto>]\n" +
        "                          [--ro-user <id>] [--rw-user <id>]\n" +
        "Passwords are never given on the command line: they are prompted, or read from " +
        ReadOnlyPasswordVariable + " / " + ReadWritePasswordVariable + ".";

    /// <summary>Creates or updates a grid's connection; options left out keep their saved values.</summary>
    private static int SetConnection(Args cli)
    {
        if (cli.Value("--client") is not { } clientName || cli.Value("--grid") is not { } gridName)
            return Fail(ConnectionSetUsage, 2);

        var ws = Unlock(cli);
        using var _ = ws.Store;
        var client = FindClient(ws, clientName);
        var grid = FindGrid(client, gridName);
        var connection = ws.GetConnection(client.Id, grid.Id) ?? new GridConnection();

        if (cli.Value("--address") is { } address) connection.Address = address;
        if (cli.Value("--port") is { } port)
        {
            connection.Port = int.TryParse(port, out var number)
                ? number
                : throw new ArgumentException($"\"{port}\" is not a valid port.");
        }
        if (cli.Value("--wapi-version") is { } version) connection.WapiVersion = version;
        if (cli.Value("--ro-user") is { } roUser)
        {
            connection.ReadOnly = new Credential
            {
                Username = roUser,
                Password = AccountPassword(ReadOnlyPasswordVariable, "read-only", roUser),
            };
        }
        if (cli.Value("--rw-user") is { } rwUser)
        {
            connection.ReadWrite = new Credential
            {
                Username = rwUser,
                Password = AccountPassword(ReadWritePasswordVariable, "read-write", rwUser),
            };
        }

        ws.SaveConnection(client.Id, grid.Id, connection);
        Console.WriteLine($"Saved the connection for {client.Name} › {grid.Name}.");
        PrintConnection(connection, reveal: false);
        return 0;
    }

    private static string AccountPassword(string variable, string kind, string user)
    {
        var password = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrEmpty(password) ? ReadPassword($"Password for {kind} account {user}: ") : password;
    }

    /// <summary>Shows a grid's connection. Passwords are hidden unless <c>--reveal</c> is given.</summary>
    private static int ShowConnection(Args cli)
    {
        if (cli.Value("--client") is not { } clientName || cli.Value("--grid") is not { } gridName)
            return Fail("Usage: pfr connection-show --client <name> --grid <name> [--reveal]", 2);

        var ws = Unlock(cli);
        using var _ = ws.Store;
        var client = FindClient(ws, clientName);
        var grid = FindGrid(client, gridName);
        if (ws.GetConnection(client.Id, grid.Id) is not { } connection)
        {
            return Fail($"{client.Name} › {grid.Name} has no saved connection. Add one with: " +
                        $"pfr connection-set --client \"{client.Name}\" --grid \"{grid.Name}\" --address <host> --ro-user <id>");
        }

        Console.WriteLine($"{client.Name} › {grid.Name}");
        PrintConnection(connection, cli.Has("--reveal"));
        return 0;
    }

    private static int RemoveConnection(Args cli)
    {
        if (cli.Value("--client") is not { } clientName || cli.Value("--grid") is not { } gridName)
            return Fail("Usage: pfr connection-remove --client <name> --grid <name>", 2);

        var ws = Unlock(cli);
        using var _ = ws.Store;
        var client = FindClient(ws, clientName);
        var grid = FindGrid(client, gridName);
        if (!ws.HasConnection(client.Id, grid.Id)) return Fail($"{client.Name} › {grid.Name} has no saved connection.");

        ws.DeleteConnection(client.Id, grid.Id);
        Console.WriteLine($"Removed the saved connection for {client.Name} › {grid.Name}.");
        return 0;
    }

    private static void PrintConnection(GridConnection c, bool reveal)
    {
        Console.WriteLine($"  Address:       {c.Address}{(c.Port == GridConnection.DefaultPort ? "" : $" (port {c.Port})")}");
        Console.WriteLine($"  WAPI version:  {c.WapiVersion ?? "detected on connect"}");
        Console.WriteLine($"  Read-only:     {Describe(c.ReadOnly, reveal)}");
        Console.WriteLine($"  Read-write:    {Describe(c.ReadWrite, reveal)}");
        Console.WriteLine($"  Saved:         {c.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
    }

    private static string Describe(Credential? account, bool reveal) =>
        account is null ? "not set" : reveal ? $"{account.Username} / {account.Password}" : $"{account.Username}  (password saved)";

    // ---- Client site logins and API keys ----------------------------------------

    private const string LoginSetUsage =
        "Usage: pfr login-set --client <name> --title <title> [--url <site>] [--user <id>] [--notes <text>] [--new-title <title>]\n" +
        "                     [--password] [--api-key]\n" +
        "Creates the login, or updates it if the title exists (options left out keep their saved values).\n" +
        "--password and --api-key prompt for the secret without echo (Enter alone clears it), or read " +
        LoginPasswordVariable + " / " + LoginApiKeyVariable + ". Secrets are never given on the command line.";

    private static SiteLogin? FindLogin(ClientWorkspace ws, Client client, string title) =>
        ws.GetLogins(client.Id).FirstOrDefault(l => string.Equals(l.Title, title.Trim(), StringComparison.OrdinalIgnoreCase));

    private static int ListLogins(Args cli)
    {
        if (cli.Value("--client") is not { } clientName) return Fail("Usage: pfr logins --client <name>", 2);
        var ws = Unlock(cli);
        using var _ = ws.Store;
        var client = FindClient(ws, clientName);
        var logins = ws.GetLogins(client.Id);
        if (logins.Count == 0)
        {
            Console.WriteLine($"{client.Name} has no saved logins. Add one with: pfr login-set --client \"{client.Name}\" --title <title> --url <site> --user <id> --password");
            return 0;
        }

        Console.WriteLine($"{client.Name}: {Count(logins.Count, "login")}");
        foreach (var l in logins)
        {
            var saved = new List<string>();
            if (l.Username.Length > 0) saved.Add($"user {l.Username}");
            if (l.Password.Length > 0) saved.Add("password");
            if (l.ApiKey.Length > 0) saved.Add("API key");
            Console.WriteLine($"  {l.Title,-30} {l.Url,-36} {string.Join(", ", saved)}");
        }
        return 0;
    }

    private static int SetLogin(Args cli)
    {
        if (cli.Value("--client") is not { } clientName || cli.Value("--title") is not { } title)
            return Fail(LoginSetUsage, 2);

        var ws = Unlock(cli);
        using var _ = ws.Store;
        var client = FindClient(ws, clientName);
        var existing = FindLogin(ws, client, title);
        var login = existing ?? new SiteLogin { Title = title };

        if (cli.Value("--new-title") is { } newTitle) login.Title = newTitle;
        if (cli.Value("--url") is { } url) login.Url = url;
        if (cli.Value("--user") is { } user) login.Username = user;
        if (cli.Value("--notes") is { } notes) login.Notes = notes;
        if (cli.Has("--password")) login.Password = SecretValue(LoginPasswordVariable, $"Password for {login.Title.Trim()}: ");
        if (cli.Has("--api-key")) login.ApiKey = SecretValue(LoginApiKeyVariable, $"API key for {login.Title.Trim()}: ");

        ws.SaveLogin(client.Id, login);
        Console.WriteLine($"{(existing is null ? "Added" : "Updated")} login \"{login.Title}\" for {client.Name}.");
        return 0;
    }

    private static string SecretValue(string variable, string prompt)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrEmpty(value) ? ReadPassword(prompt) : value;
    }

    /// <summary>Shows a login. Secrets are hidden unless <c>--reveal</c> is given.</summary>
    private static int ShowLogin(Args cli)
    {
        if (cli.Value("--client") is not { } clientName || cli.Value("--title") is not { } title)
            return Fail("Usage: pfr login-show --client <name> --title <title> [--reveal]", 2);

        var ws = Unlock(cli);
        using var _ = ws.Store;
        var client = FindClient(ws, clientName);
        var login = FindLogin(ws, client, title)
                    ?? throw new ArgumentException($"{client.Name} has no login titled \"{title}\". Run 'pfr logins --client \"{client.Name}\"' to list them.");
        var reveal = cli.Has("--reveal");

        Console.WriteLine($"{client.Name} › {login.Title}");
        Console.WriteLine($"  Site:      {(login.Url.Length > 0 ? login.Url : "-")}");
        Console.WriteLine($"  User id:   {(login.Username.Length > 0 ? login.Username : "-")}");
        Console.WriteLine($"  Password:  {Secret(login.Password, reveal)}");
        Console.WriteLine($"  API key:   {Secret(login.ApiKey, reveal)}");
        if (login.Notes.Length > 0) Console.WriteLine($"  Notes:     {login.Notes}");
        Console.WriteLine($"  Saved:     {login.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        return 0;
    }

    private static string Secret(string value, bool reveal) =>
        value.Length == 0 ? "-" : reveal ? value : "(saved)";

    private static int RemoveLogin(Args cli)
    {
        if (cli.Value("--client") is not { } clientName || cli.Value("--title") is not { } title)
            return Fail("Usage: pfr login-remove --client <name> --title <title>", 2);

        var ws = Unlock(cli);
        using var _ = ws.Store;
        var client = FindClient(ws, clientName);
        var login = FindLogin(ws, client, title) ?? throw new ArgumentException($"{client.Name} has no login titled \"{title}\".");

        ws.DeleteLogin(client.Id, login.Id);
        Console.WriteLine($"Removed login \"{login.Title}\" from {client.Name}.");
        return 0;
    }

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
        Console.WriteLine("Grid connections (WAPI address and accounts, stored encrypted with the client):");
        Console.WriteLine("  connection-set --client <n> --grid <n> [--address <host>] [--port <n>] [--wapi-version <v>]");
        Console.WriteLine("                 [--ro-user <id>] [--rw-user <id>]   Passwords are prompted, or set " + ReadOnlyPasswordVariable + " / " + ReadWritePasswordVariable);
        Console.WriteLine("  connection-show --client <n> --grid <n> [--reveal]   Show a connection (passwords hidden unless --reveal)");
        Console.WriteLine("  connection-remove --client <n> --grid <n>    Delete a grid's saved connection");
        Console.WriteLine();
        Console.WriteLine("Client site logins and API keys (portals, VPNs, cloud consoles...; stored encrypted with the client):");
        Console.WriteLine("  logins --client <n>                          List a client's logins (no secrets shown)");
        Console.WriteLine("  login-set --client <n> --title <t> [--url <site>] [--user <id>] [--notes <text>] [--new-title <t>]");
        Console.WriteLine("            [--password] [--api-key]           Add or update; secrets are prompted, or set " + LoginPasswordVariable + " / " + LoginApiKeyVariable);
        Console.WriteLine("  login-show --client <n> --title <t> [--reveal]   Show a login (secrets hidden unless --reveal)");
        Console.WriteLine("  login-remove --client <n> --title <t>        Delete a login");
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
        private static readonly HashSet<string> Flags = ["--json", "--reveal", "--password", "--api-key"];
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
