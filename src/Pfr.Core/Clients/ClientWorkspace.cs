using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;
using Pfr.Core.Storage;

namespace Pfr.Core.Clients;

/// <summary>
/// Client workspaces kept in an encrypted <see cref="SecureStore"/>:
/// <code>
/// clients/&lt;client&gt;/client.pfr                         client, contacts, notes, grids and snapshot lists
/// clients/&lt;client&gt;/log.pfr                            engagement log
/// clients/&lt;client&gt;/logins.pfr                         logins and API keys for the client's own sites
/// clients/&lt;client&gt;/grids/&lt;grid&gt;/snapshots/&lt;id&gt;.pfr    parsed inventory (gzip JSON)
/// clients/&lt;client&gt;/grids/&lt;grid&gt;/connection.pfr        WAPI address and accounts (optional)
/// </code>
/// Every file is encrypted; folder names are random ids. Snapshots open straight
/// from here, so switching grids or clients never needs the original backup.
/// </summary>
public sealed class ClientWorkspace(SecureStore store)
{
    private const string ClientsFolder = "clients";
    private const string SettingsFile = "settings.pfr";

    public SecureStore Store { get; } = store;

    private static string ClientFile(string id) => $"{ClientsFolder}/{id}/client.pfr";
    private static string LogFile(string id) => $"{ClientsFolder}/{id}/log.pfr";
    private static string LoginsFile(string id) => $"{ClientsFolder}/{id}/logins.pfr";
    private static string GridFolder(string clientId, string gridId) => $"{ClientsFolder}/{clientId}/grids/{gridId}";
    private static string SnapshotFile(string clientId, string gridId, string snapshotId) =>
        $"{GridFolder(clientId, gridId)}/snapshots/{snapshotId}.pfr";
    private static string ConnectionFile(string clientId, string gridId) =>
        $"{GridFolder(clientId, gridId)}/connection.pfr";

    // ---- Clients ---------------------------------------------------------

    /// <summary>All clients, by name. Unreadable entries are reported in <paramref name="problems"/>.</summary>
    public List<Client> ListClients(List<string>? problems = null)
    {
        var clients = new List<Client>();
        foreach (var id in Store.Folders(ClientsFolder))
        {
            if (!Store.Exists(ClientFile(id))) continue;
            try
            {
                clients.Add(Store.ReadJson<Client>(ClientFile(id)));
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException)
            {
                problems?.Add($"Client folder {id} could not be read: {ex.Message}");
            }
        }
        return clients.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Client GetClient(string id) => Store.ReadJson<Client>(ClientFile(id));

    public Client CreateClient(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A client needs a name.");
        if (ListClients().Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A client named \"{name}\" already exists.");

        var client = new Client { Name = name };
        Store.WriteJson(ClientFile(client.Id), client);
        Store.WriteJson(LogFile(client.Id), new List<LogEntry>());
        return client;
    }

    public void SaveClient(Client client)
    {
        client.Name = client.Name.Trim();
        if (client.Name.Length == 0) throw new ArgumentException("A client needs a name.");
        if (ListClients().Any(c => c.Id != client.Id
                                   && string.Equals(c.Name, client.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A client named \"{client.Name}\" already exists.");
        foreach (var g in client.Grids) g.Name = g.Name.Trim();
        if (client.Grids.Any(g => g.Name.Length == 0)) throw new ArgumentException("A grid needs a name.");
        if (client.Grids.GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new ArgumentException("Each of a client's grids needs a different name.");

        client.UpdatedUtc = DateTimeOffset.UtcNow;
        Store.WriteJson(ClientFile(client.Id), client);
    }

    /// <summary>Removes the client and everything stored for it.</summary>
    public void DeleteClient(string id) => Store.DeleteFolder($"{ClientsFolder}/{id}");

    // ---- Grids -------------------------------------------------------------

    public ClientGrid CreateGrid(string clientId, string name)
    {
        var client = GetClient(clientId);
        var grid = new ClientGrid { Name = UniqueGridName(client, name) };
        client.Grids.Add(grid);
        SaveClient(client);
        return grid;
    }

    /// <summary>Removes a grid and all of its snapshots.</summary>
    public void DeleteGrid(string clientId, string gridId)
    {
        var client = GetClient(clientId);
        client.Grids.RemoveAll(g => g.Id == gridId);
        SaveClient(client);
        Store.DeleteFolder(GridFolder(clientId, gridId));
    }

    // ---- Connections -----------------------------------------------------

    /// <summary>The saved WAPI connection of a grid, or null if none has been saved.</summary>
    public GridConnection? GetConnection(string clientId, string gridId) =>
        Store.Exists(ConnectionFile(clientId, gridId))
            ? Store.ReadJson<GridConnection>(ConnectionFile(clientId, gridId))
            : null;

    public bool HasConnection(string clientId, string gridId) => Store.Exists(ConnectionFile(clientId, gridId));

    /// <summary>
    /// Saves (or replaces) a grid's connection after normalising and checking it.
    /// Deleting the grid or the client deletes the connection with it.
    /// </summary>
    /// <exception cref="ArgumentException">The values are not usable, or the grid no longer exists; nothing is stored.</exception>
    public GridConnection SaveConnection(string clientId, string gridId, GridConnection connection)
    {
        if (GetClient(clientId).FindGrid(gridId) is null) throw new ArgumentException("That grid no longer exists.");
        connection.Normalize();
        connection.UpdatedUtc = DateTimeOffset.UtcNow;
        Store.WriteJson(ConnectionFile(clientId, gridId), connection);
        return connection;
    }

    public void DeleteConnection(string clientId, string gridId) => Store.Delete(ConnectionFile(clientId, gridId));

    /// <summary>
    /// The grid a new backup belongs to: the one with the same grid name if there is
    /// one, otherwise a new grid named after it.
    /// </summary>
    private static ClientGrid MatchOrCreateGrid(Client client, GridInventory inventory)
    {
        // The grid name is required (see BackupParser.EnsureGridName), so it always identifies the grid.
        var name = inventory.GridName!.Trim();
        var match = client.Grids.FirstOrDefault(g =>
            g.Snapshots.Any(s => string.Equals(s.GridName?.Trim(), name, StringComparison.OrdinalIgnoreCase))
            || string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        var grid = new ClientGrid { Name = UniqueGridName(client, name) };
        client.Grids.Add(grid);
        return grid;
    }

    private static string UniqueGridName(Client client, string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Grid" : name.Trim();
        var candidate = name;
        for (var i = 2; client.Grids.Any(g => string.Equals(g.Name, candidate, StringComparison.OrdinalIgnoreCase)); i++)
        {
            candidate = $"{name} ({i})";
        }
        return candidate;
    }

    // ---- Snapshots -------------------------------------------------------

    /// <summary>
    /// Parses a backup and stores it as a new snapshot. With no <paramref name="gridId"/>,
    /// it goes to the client's grid of the same name, or a new grid.
    /// </summary>
    public (ClientGrid Grid, SnapshotInfo Snapshot) ImportBackup(
        string clientId, string backupPath, string? gridId = null, string label = "")
    {
        var inventory = BackupParser.ParseFile(backupPath);
        var file = new FileInfo(backupPath);
        return AddSnapshot(clientId, inventory, file.Name, file.Length, Sha256Of(backupPath), gridId, label);
    }

    /// <summary>Stores an already-parsed inventory as a new snapshot (grid chosen as for <see cref="ImportBackup"/>).</summary>
    /// <exception cref="InvalidDataException">The inventory has no grid name; nothing is stored.</exception>
    public (ClientGrid Grid, SnapshotInfo Snapshot) AddSnapshot(string clientId, GridInventory inventory,
        string sourceFileName, long sourceBytes, string sourceSha256, string? gridId = null, string label = "")
    {
        BackupParser.EnsureGridName(inventory, sourceFileName);
        var client = GetClient(clientId);
        var grid = gridId is null
            ? MatchOrCreateGrid(client, inventory)
            : client.FindGrid(gridId) ?? throw new ArgumentException("That grid no longer exists.");

        var info = new SnapshotInfo
        {
            Label = label.Trim(),
            SourceFileName = sourceFileName,
            SourceBytes = sourceBytes,
            SourceSha256 = sourceSha256,
            GridName = inventory.GridName,
            NiosVersion = inventory.NiosVersion,
            Members = inventory.Members.Count,
            Zones = inventory.Zones.Count,
            Networks = inventory.Networks.Count(n => !n.IsContainer),
            DhcpRanges = inventory.DhcpRanges.Count,
            Records = inventory.Records.Count,
        };

        // Inventory first, then the index, so a crash never lists a snapshot that isn't there.
        Store.Write(SnapshotFile(clientId, grid.Id, info.Id),
            Compress(JsonSerializer.SerializeToUtf8Bytes(inventory, StoreJson.Options)));
        grid.Snapshots.Add(info);
        SaveClient(client);
        return (grid, info);
    }

    public GridInventory LoadSnapshot(string clientId, string gridId, string snapshotId)
    {
        var json = Decompress(Store.Read(SnapshotFile(clientId, gridId, snapshotId)));
        return JsonSerializer.Deserialize<GridInventory>(json, StoreJson.Options)
               ?? throw new InvalidDataException("Snapshot is empty.");
    }

    /// <summary>The newest snapshot of a grid, or null if it has none.</summary>
    public (SnapshotInfo Snapshot, GridInventory Inventory)? LoadLatest(string clientId, string gridId)
    {
        var grid = GetClient(clientId).FindGrid(gridId);
        return grid?.LatestSnapshot is { } latest ? (latest, LoadSnapshot(clientId, gridId, latest.Id)) : null;
    }

    public void DeleteSnapshot(string clientId, string gridId, string snapshotId)
    {
        var client = GetClient(clientId);
        client.FindGrid(gridId)?.Snapshots.RemoveAll(s => s.Id == snapshotId);
        SaveClient(client);
        Store.Delete(SnapshotFile(clientId, gridId, snapshotId));
    }

    // ---- Last viewed -------------------------------------------------------

    public WorkspaceSettings GetSettings() =>
        Store.Exists(SettingsFile) ? Store.ReadJson<WorkspaceSettings>(SettingsFile) : new WorkspaceSettings();

    /// <summary>Remembers the snapshot being viewed so it reopens after the next unlock.</summary>
    public void RememberLastViewed(string clientId, string gridId, string snapshotId) =>
        Store.WriteJson(SettingsFile, new WorkspaceSettings
        {
            LastClientId = clientId, LastGridId = gridId, LastSnapshotId = snapshotId,
        });

    /// <summary>The last viewed client, grid and snapshot, if they all still exist.</summary>
    public (Client Client, ClientGrid Grid, SnapshotInfo Snapshot)? LastViewed()
    {
        var s = GetSettings();
        if (s.LastClientId is not { } clientId || s.LastGridId is not { } gridId || s.LastSnapshotId is not { } snapshotId
            || !Store.Exists(ClientFile(clientId)))
        {
            return null;
        }

        var client = GetClient(clientId);
        var grid = client.FindGrid(gridId);
        var snapshot = grid?.Snapshots.FirstOrDefault(x => x.Id == snapshotId);
        return grid is not null && snapshot is not null ? (client, grid, snapshot) : null;
    }

    /// <summary>An existing snapshot of the same file (same SHA-256), if any, and its grid.</summary>
    public (ClientGrid Grid, SnapshotInfo Snapshot)? FindDuplicate(string clientId, string sha256)
    {
        foreach (var grid in GetClient(clientId).Grids)
        {
            var match = grid.Snapshots.FirstOrDefault(s =>
                string.Equals(s.SourceSha256, sha256, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return (grid, match);
        }
        return null;
    }

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // ---- Site logins and API keys ------------------------------------------

    /// <summary>A client's logins and API keys, by title (empty if none have been saved).</summary>
    public List<SiteLogin> GetLogins(string clientId) =>
        (Store.Exists(LoginsFile(clientId)) ? Store.ReadJson<List<SiteLogin>>(LoginsFile(clientId)) : [])
        .OrderBy(l => l.Title, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Adds a login, or replaces the one with the same id, after tidying and checking it.
    /// Titles are unique per client (ignoring case). Deleting the client deletes its logins with it.
    /// </summary>
    /// <exception cref="ArgumentException">No title, a title already in use, or the client no longer exists; nothing is stored.</exception>
    public SiteLogin SaveLogin(string clientId, SiteLogin login)
    {
        if (!Store.Exists(ClientFile(clientId))) throw new ArgumentException("That client no longer exists.");
        login.Normalize();

        var logins = GetLogins(clientId);
        if (logins.Any(l => l.Id != login.Id && string.Equals(l.Title, login.Title, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"This client already has a login titled \"{login.Title}\".");

        login.UpdatedUtc = DateTimeOffset.UtcNow;
        logins.RemoveAll(l => l.Id == login.Id);
        logins.Add(login);
        Store.WriteJson(LoginsFile(clientId), logins);
        return login;
    }

    public void DeleteLogin(string clientId, string loginId)
    {
        var logins = GetLogins(clientId);
        if (logins.RemoveAll(l => l.Id == loginId) > 0) Store.WriteJson(LoginsFile(clientId), logins);
    }

    // ---- Engagement log --------------------------------------------------

    /// <summary>Entries newest first.</summary>
    public List<LogEntry> GetLog(string clientId) =>
        (Store.Exists(LogFile(clientId)) ? Store.ReadJson<List<LogEntry>>(LogFile(clientId)) : [])
        .OrderByDescending(e => e.Date).ToList();

    public LogEntry SaveLogEntry(string clientId, LogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Title) && string.IsNullOrWhiteSpace(entry.Text))
            throw new ArgumentException("A log entry needs a title or some text.");

        var log = GetLog(clientId);
        log.RemoveAll(e => e.Id == entry.Id);
        log.Add(entry);
        Store.WriteJson(LogFile(clientId), log);
        return entry;
    }

    public void DeleteLogEntry(string clientId, string entryId)
    {
        var log = GetLog(clientId);
        log.RemoveAll(e => e.Id == entryId);
        Store.WriteJson(LogFile(clientId), log);
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
        {
            gzip.Write(data);
        }
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
